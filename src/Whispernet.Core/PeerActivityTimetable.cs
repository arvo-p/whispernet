namespace Whispernet.Core;

/// <summary>
/// Profil horaire d'activité d'un pair distinguant les jours ouvrés (lundi–vendredi)
/// et le week-end (samedi–dimanche) sur 24 créneaux horaires.
/// </summary>
public sealed class PeerActivityProfile
{
    public const int HoursPerDay = 24;

    public AuthorId AuthorId { get; }
    public NodeId? LastKnownNodeId { get; set; }
    public double[] WeekdayHours { get; }
    public double[] WeekendHours { get; }
    public int TotalSamples { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; }

    public PeerActivityProfile(
        AuthorId authorId,
        NodeId? lastKnownNodeId = null,
        double[]? weekdayHours = null,
        double[]? weekendHours = null,
        int totalSamples = 0,
        DateTimeOffset? lastUpdatedUtc = null)
    {
        AuthorId = authorId;
        LastKnownNodeId = lastKnownNodeId;
        WeekdayHours = weekdayHours is { Length: HoursPerDay }
            ? (double[])weekdayHours.Clone()
            : new double[HoursPerDay];
        WeekendHours = weekendHours is { Length: HoursPerDay }
            ? (double[])weekendHours.Clone()
            : new double[HoursPerDay];
        TotalSamples = totalSamples;
        LastUpdatedUtc = lastUpdatedUtc ?? DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Moteur pur de profilage horaire et de correspondance d'activité entre pairs relais et destinataire.
/// </summary>
public static class PeerActivityTimetable
{
    /// <summary>
    /// Enregistre une observation d'activité en ligne dans le créneau horaire approprié
    /// (jour ouvré vs week-end) avec lissage léger sur les heures adjacentes.
    /// </summary>
    public static void RecordObservation(
        PeerActivityProfile profile,
        DateTimeOffset observedAt,
        NodeId? nodeId = null,
        double weight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (weight <= 0)
        {
            return;
        }

        if (nodeId is NodeId nid && !string.IsNullOrWhiteSpace(nid.Value))
        {
            profile.LastKnownNodeId = nid;
        }

        DateTimeOffset local = observedAt.ToLocalTime();
        bool isWeekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        double[] buckets = isWeekend ? profile.WeekendHours : profile.WeekdayHours;
        int hour = local.Hour;

        // Lissage exponentiel léger pour éviter l'oubli brutal tout en privilégiant l'activité récente
        for (int i = 0; i < PeerActivityProfile.HoursPerDay; i++)
        {
            buckets[i] *= 0.995;
        }

        buckets[hour] += weight;
        buckets[(hour + 23) % 24] += weight * 0.25;
        buckets[(hour + 1) % 24] += weight * 0.25;

        profile.TotalSamples++;
        if (observedAt > profile.LastUpdatedUtc)
        {
            profile.LastUpdatedUtc = observedAt;
        }
    }

    /// <summary>
    /// Calcule un score [0..1] mesurant l'adéquation entre les heures de présence d'un relais candidat
    /// et celles du destinataire, en distinguant jours ouvrés et week-ends.
    /// </summary>
    public static double ComputeTimetableMatchScore(
        PeerActivityProfile candidate,
        PeerActivityProfile? recipient,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        DateTimeOffset localNow = now.ToLocalTime();
        bool isWeekendNow = localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        int currentHour = localNow.Hour;

        // Si le destinataire n'a pas encore de profil, on privilégie l'activité globale du candidat sur le type de jour courant
        if (recipient is null || recipient.TotalSamples == 0)
        {
            double[] candBuckets = isWeekendNow ? candidate.WeekendHours : candidate.WeekdayHours;
            double maxVal = candBuckets.Max();
            if (maxVal <= 0)
            {
                return candidate.TotalSamples > 0 ? 0.15 : 0.0;
            }
            return Math.Clamp((candBuckets[currentHour] / maxVal) * 0.6 + 0.2, 0.0, 1.0);
        }

        double weekdaySim = CosineSimilarity(candidate.WeekdayHours, recipient.WeekdayHours);
        double weekendSim = CosineSimilarity(candidate.WeekendHours, recipient.WeekendHours);

        // Pondération plus forte sur le type de jour courant (ouvré vs week-end)
        double dayWeightedSim = isWeekendNow
            ? (0.70 * weekendSim + 0.30 * weekdaySim)
            : (0.70 * weekdaySim + 0.30 * weekendSim);

        // Bonus si le candidat est habituellement actif à l'heure actuelle ou prochaine
        double[] currentBuckets = isWeekendNow ? candidate.WeekendHours : candidate.WeekdayHours;
        double currentMax = currentBuckets.Max();
        double currentWindowFactor = currentMax > 0
            ? Math.Max(currentBuckets[currentHour], currentBuckets[(currentHour + 1) % 24]) / currentMax
            : 0.0;

        return Math.Clamp(0.80 * dayWeightedSim + 0.20 * currentWindowFactor, 0.0, 1.0);
    }

    public static double CosineSimilarity(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.0;
        }

        double dot = 0.0;
        double normA = 0.0;
        double normB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= 1e-12 || normB <= 1e-12)
        {
            return 0.0;
        }

        return Math.Clamp(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)), 0.0, 1.0);
    }
}
