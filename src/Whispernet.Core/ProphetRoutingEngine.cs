namespace Whispernet.Core;

/// <summary>
/// Candidat relais sélectionné pour acheminer un bundle DTN.
/// </summary>
public sealed record RelayCandidateScore(
    AuthorId AuthorId,
    NodeId NodeId,
    double TimetableScore,
    double ProphetPredictability,
    double CombinedScore);

/// <summary>
/// Moteur de routage probabiliste PRoPHET (P_init=0.75, beta=0.25, gamma=0.98)
/// combiné au profilage horaire (jours ouvrés / week-ends) et aux groupes mutuels.
/// </summary>
public static class ProphetRoutingEngine
{
    public const double PInit = 0.75;
    public const double Beta = 0.25;
    public const double Gamma = 0.98;

    /// <summary>
    /// Met à jour la prédictibilité de livraison lors d'une rencontre directe.
    /// </summary>
    public static double UpdateEncounter(double currentP, DateTimeOffset lastAgedUtc, DateTimeOffset nowUtc)
    {
        double aged = AgePredictability(currentP, nowUtc - lastAgedUtc);
        return Math.Clamp(aged + (1.0 - aged) * PInit, 0.0, 1.0);
    }

    /// <summary>
    /// Vieillissement exponentiel de la prédictibilité PRoPHET par tranche horaire.
    /// </summary>
    public static double AgePredictability(double currentP, TimeSpan elapsed)
    {
        if (currentP <= 0.0)
        {
            return 0.0;
        }

        double hours = Math.Max(0.0, elapsed.TotalHours);
        if (hours <= 0.0)
        {
            return Math.Clamp(currentP, 0.0, 1.0);
        }

        return Math.Clamp(currentP * Math.Pow(Gamma, hours), 0.0, 1.0);
    }

    /// <summary>
    /// Mise à jour transitive : P(A,C) = P(A,C)_old + (1 - P(A,C)_old) * P(A,B) * P(B,C) * beta.
    /// </summary>
    public static double UpdateTransitivity(double pAC, double pAB, double pBC)
    {
        double next = pAC + (1.0 - pAC) * pAB * pBC * Beta;
        return Math.Clamp(next, 0.0, 1.0);
    }

    /// <summary>
    /// Sélectionne les <paramref name="maxRelays"/> meilleurs pairs mutuels partageant au moins un groupe gouverné
    /// avec le destinataire, classés par correspondance de créneaux horaires et prédictibilité PRoPHET.
    /// </summary>
    public static IReadOnlyList<RelayCandidateScore> SelectTopRelays(
        AuthorId selfAuthorId,
        AuthorId recipientAuthorId,
        IEnumerable<GroupGovernanceState> governedGroups,
        IReadOnlyDictionary<AuthorId, PeerActivityProfile> activityProfiles,
        IReadOnlyDictionary<AuthorId, double> prophetToRecipient,
        DateTimeOffset nowUtc,
        int maxRelays = 3)
    {
        if (maxRelays <= 0)
        {
            return Array.Empty<RelayCandidateScore>();
        }

        // 1. Collecter les pairs mutuels admis dans au moins un groupe commun avec le destinataire (ou tout groupe commun si le destinataire est connu)
        var mutualPeers = new Dictionary<AuthorId, NodeId>();
        var anyGroupPeers = new Dictionary<AuthorId, NodeId>();

        foreach (GroupGovernanceState gov in governedGroups)
        {
            if (gov.CurrentManifest.IsTerminated)
            {
                continue;
            }

            bool selfAdmitted = gov.GetMemberStatus(selfAuthorId) is MemberStatus.Admitted or MemberStatus.Silenced;
            if (!selfAdmitted)
            {
                continue;
            }

            bool recipientInGroup = gov.GetMemberStatus(recipientAuthorId) is MemberStatus.Admitted or MemberStatus.Silenced;

            foreach ((AuthorId memberId, ModerationAction lastAction) in gov.MemberStates)
            {
                if (memberId == selfAuthorId || memberId == recipientAuthorId)
                {
                    continue;
                }

                if (lastAction.NewStatus is not (MemberStatus.Admitted or MemberStatus.Silenced))
                {
                    continue;
                }

                NodeId? nodeId = lastAction.TargetLastKnownNodeId;
                if (nodeId is null && activityProfiles.TryGetValue(memberId, out PeerActivityProfile? prof))
                {
                    nodeId = prof.LastKnownNodeId;
                }

                if (nodeId is not NodeId validNode || string.IsNullOrWhiteSpace(validNode.Value))
                {
                    continue;
                }

                anyGroupPeers.TryAdd(memberId, validNode);
                if (recipientInGroup)
                {
                    mutualPeers.TryAdd(memberId, validNode);
                }
            }
        }

        Dictionary<AuthorId, NodeId> pool = mutualPeers.Count > 0 ? mutualPeers : anyGroupPeers;
        if (pool.Count == 0)
        {
            return Array.Empty<RelayCandidateScore>();
        }

        activityProfiles.TryGetValue(recipientAuthorId, out PeerActivityProfile? recipientProfile);

        var scored = new List<RelayCandidateScore>(pool.Count);
        foreach ((AuthorId candAuthor, NodeId candNode) in pool)
        {
            double timetableScore = activityProfiles.TryGetValue(candAuthor, out PeerActivityProfile? candProfile)
                ? PeerActivityTimetable.ComputeTimetableMatchScore(candProfile, recipientProfile, nowUtc)
                : 0.10;

            double prophetScore = prophetToRecipient.TryGetValue(candAuthor, out double pVal)
                ? Math.Clamp(pVal, 0.0, 1.0)
                : 0.25;

            double combined = 0.60 * timetableScore + 0.40 * prophetScore;
            scored.Add(new RelayCandidateScore(candAuthor, candNode, timetableScore, prophetScore, combined));
        }

        return scored
            .OrderByDescending(static s => s.CombinedScore)
            .ThenByDescending(static s => s.TimetableScore)
            .ThenBy(static s => s.AuthorId.Value, StringComparer.Ordinal)
            .Take(maxRelays)
            .ToList();
    }
}
