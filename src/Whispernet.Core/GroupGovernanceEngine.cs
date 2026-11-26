using System.Security.Cryptography;

namespace Whispernet.Core;

/// <summary>
/// Moteur Sans-I/O de gouvernance de groupe basé sur un DAG causal :
/// chaque décision (modération, promotion, révocation de modérateur) est triée de manière déterministe
/// puis rejouée depuis le dernier checkpoint du manifeste, ce qui annule automatiquement les actions
/// d'un modérateur révoqué même si la révocation arrive en retard sur le réseau.
/// </summary>
public static class GroupGovernanceEngine
{
    public delegate bool SignatureVerifier(
        ReadOnlySpan<byte> publicKeySpki,
        ReadOnlySpan<byte> signableData,
        ReadOnlySpan<byte> signature);

    /// <summary>
    /// Valide et applique une nouvelle version du manifeste (y compris un éventuel checkpoint compacté).
    /// </summary>
    public static bool TryApplyManifest(
        GroupGovernanceState state,
        GroupManifest manifest,
        SignatureVerifier verifySignature,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(verifySignature);

        if (manifest.GroupId != state.GroupId)
        {
            error = "Manifest GroupId does not match this group.";
            return false;
        }

        if (state.CurrentManifest.IsTerminated)
        {
            error = "Group is permanently terminated and cannot accept manifest updates.";
            return false;
        }

        if (manifest.Version <= state.CurrentManifest.Version)
        {
            error = $"Manifest version ({manifest.Version}) must be greater than current version ({state.CurrentManifest.Version}).";
            return false;
        }

        byte[] ownerPubKey;
        byte[] ownerSig;
        try
        {
            ownerPubKey = Convert.FromBase64String(manifest.OwnerPublicKeySpkiBase64);
            ownerSig = Convert.FromBase64String(manifest.OwnerSignatureBase64);
        }
        catch (FormatException)
        {
            error = "Invalid Base64 encoding in manifest public key or signature.";
            return false;
        }

        if (GroupId.FromOwnerPublicKeySpki(ownerPubKey) != state.GroupId)
        {
            error = "Owner public key hash does not match GroupId.";
            return false;
        }

        if (!verifySignature(ownerPubKey, manifest.BuildSignableBytes(), ownerSig))
        {
            error = "Invalid owner signature on GroupManifest.";
            return false;
        }

        foreach ((AuthorId modId, string modPubKeyB64) in manifest.ModeratorPublicKeysSpkiBase64)
        {
            byte[] modPubKey;
            try
            {
                modPubKey = Convert.FromBase64String(modPubKeyB64);
            }
            catch (FormatException)
            {
                error = $"Invalid Base64 public key for moderator {modId}.";
                return false;
            }

            var derived = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(modPubKey)));
            if (derived != modId)
            {
                error = $"Moderator public key does not match AuthorId {modId}.";
                return false;
            }
        }

        state.CurrentManifest = manifest;

        if (manifest.CheckpointMemberStates is not null)
        {
            state.BaseCheckpointStatuses.Clear();
            foreach ((AuthorId mId, MemberStatus status) in manifest.CheckpointMemberStates)
            {
                state.BaseCheckpointStatuses[mId] = status;
            }
        }

        // Supprimer les événements absorbés par le checkpoint
        if (manifest.CheckpointLamportClock > 0)
        {
            foreach (string id in state.GovernanceLog
                         .Where(kv => kv.Value.LamportClock <= manifest.CheckpointLamportClock)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                state.GovernanceLog.Remove(id);
            }
        }

        RebuildAndReplayGovernanceLog(state, out _);
        error = null;
        return true;
    }

    /// <summary>
    /// Valide cryptographiquement une décision de gouvernance, l'insère dans le DAG,
    /// retrie le journal et le rejoue de manière déterministe.
    /// </summary>
    public static bool TryApplyModerationAction(
        GroupGovernanceState state,
        ModerationAction action,
        SignatureVerifier verifySignature,
        out IReadOnlyList<OutAction> outActions,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(verifySignature);

        outActions = Array.Empty<OutAction>();

        if (action.GroupId != state.GroupId)
        {
            error = "ModerationAction GroupId does not match this group.";
            return false;
        }

        if (state.CurrentManifest.IsTerminated)
        {
            error = "Group is permanently terminated.";
            return false;
        }

        if (action.ManifestVersion > state.CurrentManifest.Version)
        {
            error = $"Action references unseen manifest version {action.ManifestVersion} (current={state.CurrentManifest.Version}).";
            return false;
        }

        if (action.LamportClock <= state.CurrentManifest.CheckpointLamportClock)
        {
            error = $"Action LamportClock ({action.LamportClock}) is older than manifest checkpoint ({state.CurrentManifest.CheckpointLamportClock}).";
            return false;
        }

        if (action.Parents is not null && action.Parents.Count > ModerationAction.MaxParents)
        {
            error = $"Governance action exceeds {ModerationAction.MaxParents} parents.";
            return false;
        }

        if (!TryResolveKnownSignerPublicKey(state, action.SignerAuthorId, out byte[]? signerPubKey, out bool isOwner))
        {
            error = $"Signer {action.SignerAuthorId} has no recognized authority key in this group.";
            return false;
        }

        if (action.Kind is GovernanceActionKind.PromoteModerator or GovernanceActionKind.DemoteModerator && !isOwner)
        {
            error = "Only the group authority key can promote or demote moderators.";
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(action.SignatureBase64);
        }
        catch (FormatException)
        {
            error = "Invalid Base64 signature on ModerationAction.";
            return false;
        }

        if (!verifySignature(signerPubKey!, action.BuildSignableBytes(), signature))
        {
            error = "Invalid signature on ModerationAction.";
            return false;
        }

        string actionId = action.ComputeActionId();
        if (state.GovernanceLog.ContainsKey(actionId))
        {
            error = null;
            return true;
        }

        state.GovernanceLog[actionId] = action;
        if (action.LamportClock > state.MaxLamportClock)
        {
            state.MaxLamportClock = action.LamportClock;
        }

        RebuildAndReplayGovernanceLog(state, out outActions);
        error = null;
        return true;
    }

    /// <summary>
    /// Compacte le journal de gouvernance en supprimant les anciennes décisions obsolètes (remplacées
    /// pour un même membre ou annulées suite à la révocation d'un modérateur) au-delà de la fenêtre récente.
    /// </summary>
    public static int CompactGovernanceLog(GroupGovernanceState state, ulong recentWindow = 50)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.GovernanceLog.Count == 0 || state.MaxLamportClock <= recentWindow)
        {
            return 0;
        }

        ulong cutoffClock = state.MaxLamportClock - recentWindow;
        var winningIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModerationAction winning in state.MemberStates.Values)
        {
            winningIds.Add(winning.ComputeActionId());
        }

        int removed = 0;
        foreach ((string id, ModerationAction act) in state.GovernanceLog.ToList())
        {
            if (act.LamportClock > cutoffClock)
            {
                continue;
            }

            // Conserver la dernière décision gagnante par utilisateur ainsi que les révocations encore utiles
            if (winningIds.Contains(id) || act.Kind == GovernanceActionKind.DemoteModerator)
            {
                continue;
            }

            if (state.GovernanceLog.Remove(id))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            RebuildAndReplayGovernanceLog(state, out _);
        }

        return removed;
    }

    public static bool CanAuthorPost(GroupGovernanceState state, AuthorId authorId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.CurrentManifest.IsTerminated &&
               state.GetMemberStatus(authorId) == MemberStatus.Admitted;
    }

    public static bool CanAuthorReceive(GroupGovernanceState state, AuthorId authorId)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.CurrentManifest.IsTerminated &&
               state.GetMemberStatus(authorId) is MemberStatus.Admitted or MemberStatus.Silenced;
    }

    private static void RebuildAndReplayGovernanceLog(
        GroupGovernanceState state,
        out IReadOnlyList<OutAction> outActions)
    {
        var emitted = new List<OutAction>();
        outActions = emitted;

        byte[] ownerPubKey = Convert.FromBase64String(state.CurrentManifest.OwnerPublicKeySpkiBase64);
        var ownerAuthorId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(ownerPubKey)));

        // 1. Tri topologique de Kahn sur le DAG de gouvernance
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var referencedAsParent = new HashSet<string>(StringComparer.Ordinal);

        foreach (string id in state.GovernanceLog.Keys)
        {
            inDegree[id] = 0;
            childrenOf[id] = new List<string>();
        }

        foreach ((string childId, ModerationAction child) in state.GovernanceLog)
        {
            if (child.Parents is null)
            {
                continue;
            }

            foreach (string parentId in child.Parents)
            {
                referencedAsParent.Add(parentId);
                if (state.GovernanceLog.ContainsKey(parentId))
                {
                    inDegree[childId]++;
                    childrenOf[parentId].Add(childId);
                }
            }
        }

        state.GovernanceHeadIds.Clear();
        foreach (string id in state.GovernanceLog.Keys)
        {
            if (!referencedAsParent.Contains(id))
            {
                state.GovernanceHeadIds.Add(id);
            }
        }

        // Priorité déterministe : (LamportClock, OwnerFirst=0 vs Mod=1, IssuedAtUtc, ActionId)
        var queue = new PriorityQueue<(string Id, ModerationAction Act), (ulong Clock, int AuthorityRank, long Ts, string Id)>(
            Comparer<(ulong Clock, int AuthorityRank, long Ts, string Id)>.Create(static (a, b) =>
            {
                int cmpClock = a.Clock.CompareTo(b.Clock);
                if (cmpClock != 0)
                {
                    return cmpClock;
                }

                int cmpAuth = a.AuthorityRank.CompareTo(b.AuthorityRank);
                if (cmpAuth != 0)
                {
                    return cmpAuth;
                }

                int cmpTs = a.Ts.CompareTo(b.Ts);
                if (cmpTs != 0)
                {
                    return cmpTs;
                }

                return string.CompareOrdinal(a.Id, b.Id);
            }));

        foreach ((string id, int deg) in inDegree)
        {
            if (deg == 0)
            {
                ModerationAction act = state.GovernanceLog[id];
                int rank = act.SignerAuthorId == ownerAuthorId ? 0 : 1;
                queue.Enqueue((id, act), (act.LamportClock, rank, act.IssuedAtUtc.UtcTicks, id));
            }
        }

        // 2. Réinitialiser l'état dérivé depuis le manifeste puis rejouer l'historique trié
        state.ActiveModerators.Clear();
        foreach ((AuthorId modId, string pubKey) in state.CurrentManifest.ModeratorPublicKeysSpkiBase64)
        {
            state.ActiveModerators[modId] = pubKey;
        }

        var previousBanned = state.MemberStates
            .Where(kv => kv.Value.NewStatus == MemberStatus.Banned)
            .Select(kv => kv.Key)
            .ToHashSet();

        state.MemberStates.Clear();
        state.KnownAuthorNodes.Clear();

        while (queue.Count > 0)
        {
            (string currentId, ModerationAction act) = queue.Dequeue();

            bool isOwner = act.SignerAuthorId == ownerAuthorId;
            if (act.Kind == GovernanceActionKind.PromoteModerator)
            {
                if (isOwner)
                {
                    state.ActiveModerators[act.TargetAuthorId] = act.TargetPublicKeySpkiBase64;
                }
            }
            else if (act.Kind == GovernanceActionKind.DemoteModerator)
            {
                if (isOwner)
                {
                    state.ActiveModerators.Remove(act.TargetAuthorId);
                }
            }
            else
            {
                // Ignorer automatiquement toute décision signée par un modérateur déjà révoqué à ce point du rejeu !
                if (isOwner || state.ActiveModerators.ContainsKey(act.SignerAuthorId))
                {
                    state.MemberStates[act.TargetAuthorId] = act;
                    if (act.NewStatus == MemberStatus.Banned)
                    {
                        if (!previousBanned.Contains(act.TargetAuthorId) && act.TargetLastKnownNodeId is NodeId bannedNode)
                        {
                            emitted.Add(new OutAction.NeighborDown(bannedNode));
                            emitted.Add(new OutAction.CloseConnection(bannedNode));
                        }
                        state.KnownAuthorNodes.Remove(act.TargetAuthorId);
                    }
                    else if (act.TargetLastKnownNodeId is NodeId nodeId)
                    {
                        state.KnownAuthorNodes[act.TargetAuthorId] = nodeId;
                    }
                }
            }

            foreach (string childId in childrenOf[currentId])
            {
                inDegree[childId]--;
                if (inDegree[childId] == 0)
                {
                    ModerationAction child = state.GovernanceLog[childId];
                    int rank = child.SignerAuthorId == ownerAuthorId ? 0 : 1;
                    queue.Enqueue((childId, child), (child.LamportClock, rank, child.IssuedAtUtc.UtcTicks, childId));
                }
            }
        }
    }

    private static bool TryResolveKnownSignerPublicKey(
        GroupGovernanceState state,
        AuthorId signerAuthorId,
        out byte[]? publicKeySpki,
        out bool isOwner)
    {
        byte[] ownerPubKey = Convert.FromBase64String(state.CurrentManifest.OwnerPublicKeySpkiBase64);
        var ownerAuthorId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(ownerPubKey)));
        if (signerAuthorId == ownerAuthorId)
        {
            publicKeySpki = ownerPubKey;
            isOwner = true;
            return true;
        }

        isOwner = false;
        if (state.CurrentManifest.ModeratorPublicKeysSpkiBase64.TryGetValue(signerAuthorId, out string? modPubKeyB64) ||
            state.ActiveModerators.TryGetValue(signerAuthorId, out modPubKeyB64))
        {
            publicKeySpki = Convert.FromBase64String(modPubKeyB64);
            return true;
        }

        // Permettre l'ingestion hors-ordre si l'auteur a été promu par un événement PromoteModerator déjà reçu
        foreach (ModerationAction act in state.GovernanceLog.Values)
        {
            if (act.Kind == GovernanceActionKind.PromoteModerator && act.TargetAuthorId == signerAuthorId)
            {
                publicKeySpki = Convert.FromBase64String(act.TargetPublicKeySpkiBase64);
                return true;
            }
        }

        publicKeySpki = null;
        return false;
    }
}
