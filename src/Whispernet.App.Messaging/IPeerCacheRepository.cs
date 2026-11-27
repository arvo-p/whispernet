using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Contrat de persistance du cache de pairs (Peer Cache) permettant à un nœud
/// de conserver ses pairs connus (SQLite) et de se reconnecter au réseau après un redémarrage.
/// </summary>
public interface IPeerCacheRepository{
    /// <summary>
    /// Initialise le schéma de la table <c>peer_cache</c> dans la base SQLite locale.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Charge les pairs du cache triés par ordre strict de priorité :
    /// 1. Points de Meeting 24/24 (<see cref="NodeRole.MeetingPoint"/>),
    /// 2. Pairs frais et fidèles (<c>ConsecutiveFailures == 0</c>, <c>LastSeenUtc</c> le plus récent d'abord, <c>SuccessCount</c> décroissant),
    /// 3. Pairs en probation (<c>ConsecutiveFailures &gt; 0</c>),
    /// 4. Pairs les plus anciens au sens de <c>LastSeenUtc</c> en dernier (politique LRU).
    /// </summary>
    Task<IReadOnlyList<CachedPeerEntry>> LoadCachedPeersAsync(int maxLimit = 100, CancellationToken ct = default);

    /// <summary>
    /// Applique une mise à jour émise par <see cref="OutAction.PersistPeerCache"/> (upserts + suppressions).
    /// </summary>
    Task ApplyCacheUpdateAsync(
        IEnumerable<CachedPeerEntry> upsertedEntries,
        IEnumerable<NodeId> evictedNodes,
        CancellationToken ct = default);

    /// <summary>
    /// Purge les pairs standards (<c>role != MeetingPoint</c>) qui ont dépassé le seuil d'échecs consécutifs
    /// ou la durée maximale d'inactivité (<paramref name="maxAge"/>).
    /// </summary>
    Task<int> PruneExpiredAndFailedPeersAsync(
        DateTimeOffset nowUtc,
        TimeSpan maxAge,
        int maxConsecutiveFailures = 5,
        CancellationToken ct = default);
}
