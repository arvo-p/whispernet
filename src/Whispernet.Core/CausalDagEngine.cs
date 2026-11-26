using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Whispernet.Core;

/// <summary>
/// Contrat minimal d'un événement signé appartenant à un DAG causal (chat, flux épidémique, gouvernance, etc.).
/// </summary>
public interface ICausalEvent
{
    byte[] EventId { get; }
    string EventIdHex { get; }
    byte[] ScopeId { get; }
    byte[] AuthorPublicKey { get; }
    IReadOnlyList<byte[]> Parents { get; }
    ulong LogicalClock { get; }
    ulong TimestampMs { get; }
    byte[] Payload { get; }
    byte[] Signature { get; }

    /// <summary>
    /// Construit le bloc binaire canonique signé par la clé privée de l'auteur.
    /// </summary>
    byte[] BuildSignableBytes();
}

/// <summary>
/// État en mémoire d'un DAG causal pour une portée donnée (salon, flux, groupe).
/// </summary>
public class CausalDagState<TEvent> where TEvent : class, ICausalEvent
{
    public byte[] ScopeId { get; }
    public string ScopeIdHex { get; }

    /// <summary>Tous les événements reçus indexés par leur EventIdHex.</summary>
    public Dictionary<string, TEvent> EventsById { get; } = new(StringComparer.Ordinal);

    /// <summary>Ensemble des extrémités courantes du DAG (événements qui n'ont encore aucun enfant connu).</summary>
    public HashSet<string> HeadIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Plus grande valeur d'horloge de Lamport observée dans cette portée.</summary>
    public ulong MaxLogicalClock { get; internal set; }

    /// <summary>Historique trié de manière déterministe (parents -> logical_clock -> timestamp_ms -> event_id).</summary>
    public List<TEvent> SortedHistory { get; } = new();

    public CausalDagState(byte[] scopeId)
    {
        if (scopeId is null || scopeId.Length != CausalDagEngine.HashSizeBytes)
        {
            throw new ArgumentException("ScopeId must be a 32-byte SHA-256 identifier.", nameof(scopeId));
        }

        ScopeId = scopeId.ToArray();
        ScopeIdHex = Convert.ToHexStringLower(ScopeId);
    }
}

/// <summary>
/// Moteur Sans-I/O agnostique de validation, de tri topologique causal + horloge de Lamport
/// et d'extraction de tranches manquantes pour tout DAG d'<see cref="ICausalEvent"/>.
/// </summary>
public static class CausalDagEngine
{
    public const int HashSizeBytes = 32;
    public const int MaxParents = 15;
    public const int DefaultSyncBatchSize = 500;

    public delegate bool SignatureVerifier(
        ReadOnlySpan<byte> authorPublicKeySpki,
        ReadOnlySpan<byte> signableData,
        ReadOnlySpan<byte> signature);

    /// <summary>
    /// Calcule l'identifiant unique event_id (32 octets SHA-256) à partir de la liste ordonnée des parents et du payload.
    /// </summary>
    public static byte[] ComputeEventId(
        IReadOnlyList<byte[]> parents,
        ReadOnlySpan<byte> payload,
        int maxParents = MaxParents)
    {
        ArgumentNullException.ThrowIfNull(parents);
        if (parents.Count > maxParents)
        {
            throw new ArgumentException($"An event cannot have more than {maxParents} parents.", nameof(parents));
        }

        int totalLen = 4 + (parents.Count * HashSizeBytes) + 4 + payload.Length;
        byte[] buffer = new byte[totalLen];
        Span<byte> span = buffer;

        BinaryPrimitives.WriteInt32LittleEndian(span[..4], parents.Count);
        int offset = 4;
        foreach (byte[] parent in parents)
        {
            if (parent is null || parent.Length != HashSizeBytes)
            {
                throw new ArgumentException("Each parent event_id must be exactly 32 bytes.", nameof(parents));
            }

            parent.CopyTo(span.Slice(offset, HashSizeBytes));
            offset += HashSizeBytes;
        }

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), payload.Length);
        offset += 4;
        payload.CopyTo(span.Slice(offset));

        return SHA256.HashData(span);
    }

    /// <summary>
    /// Calcule la tranche topologique manquante pour un pair distant connaissant <paramref name="remoteHeadIds"/> :
    /// exclut les têtes distantes et tous leurs ancêtres, et retourne les événements restants
    /// dans l'ordre causal exact de <see cref="CausalDagState{TEvent}.SortedHistory"/>.
    /// </summary>
    public static IReadOnlyList<TEvent> ComputeMissingTopologicalSlice<TEvent>(
        CausalDagState<TEvent> state,
        IEnumerable<string> remoteHeadIds) where TEvent : class, ICausalEvent
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(remoteHeadIds);

        var knownByRemote = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();

        foreach (string headId in remoteHeadIds)
        {
            if (state.EventsById.ContainsKey(headId) && knownByRemote.Add(headId))
            {
                stack.Push(headId);
            }
        }

        while (stack.Count > 0)
        {
            string currentId = stack.Pop();
            if (!state.EventsById.TryGetValue(currentId, out TEvent? evt))
            {
                continue;
            }

            foreach (byte[] parentBytes in evt.Parents)
            {
                string parentHex = Convert.ToHexStringLower(parentBytes);
                if (state.EventsById.ContainsKey(parentHex) && knownByRemote.Add(parentHex))
                {
                    stack.Push(parentHex);
                }
            }
        }

        var slice = new List<TEvent>();
        foreach (TEvent evt in state.SortedHistory)
        {
            if (!knownByRemote.Contains(evt.EventIdHex))
            {
                slice.Add(evt);
            }
        }

        return slice;
    }

    /// <summary>
    /// Découpe une tranche topologique en lots (par défaut 500 événements par lot).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TEvent>> ChunkTopologicalSlice<TEvent>(
        IReadOnlyList<TEvent> slice,
        int batchSize = DefaultSyncBatchSize)
    {
        ArgumentNullException.ThrowIfNull(slice);
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        if (slice.Count == 0)
        {
            return Array.Empty<IReadOnlyList<TEvent>>();
        }

        var batches = new List<IReadOnlyList<TEvent>>((slice.Count + batchSize - 1) / batchSize);
        for (int i = 0; i < slice.Count; i += batchSize)
        {
            int count = Math.Min(batchSize, slice.Count - i);
            var batch = new List<TEvent>(count);
            for (int j = 0; j < count; j++)
            {
                batch.Add(slice[i + j]);
            }
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>
    /// Sélectionne jusqu'à <paramref name="maxParents"/> parents (les extrémités courantes du DAG)
    /// et calcule la prochaine horloge de Lamport.
    /// </summary>
    public static (IReadOnlyList<byte[]> Parents, ulong NextLogicalClock) PrepareNextEventCausalContext<TEvent>(
        CausalDagState<TEvent> state,
        int maxParents = MaxParents) where TEvent : class, ICausalEvent
    {
        ArgumentNullException.ThrowIfNull(state);

        List<TEvent> heads = state.HeadIds
            .Where(id => state.EventsById.ContainsKey(id))
            .Select(id => state.EventsById[id])
            .OrderByDescending(e => e.LogicalClock)
            .ThenByDescending(e => e.TimestampMs)
            .ThenBy(e => e.EventIdHex, StringComparer.Ordinal)
            .Take(maxParents)
            .ToList();

        List<byte[]> parentIds = heads.Select(h => h.EventId).ToList();
        ulong maxParentClock = heads.Count > 0 ? heads.Max(h => h.LogicalClock) : state.MaxLogicalClock;
        ulong nextClock = Math.Max(state.MaxLogicalClock, maxParentClock) + 1;

        return (parentIds, nextClock);
    }

    /// <summary>
    /// Valide l'intégrité cryptographique (event_id, limite de parents, signature) et insère l'événement
    /// dans le DAG en recalculant l'ordre causal déterministe de l'historique.
    /// </summary>
    public static bool TryIngestEvent<TEvent>(
        CausalDagState<TEvent> state,
        TEvent evt,
        SignatureVerifier verifySignature,
        out string? error) where TEvent : class, ICausalEvent
    {
        if (!TryValidateAndInsertEvent(state, evt, verifySignature, out bool added, out error))
        {
            return false;
        }

        if (added)
        {
            RebuildHeadsAndSortedHistory(state);
        }

        return true;
    }

    /// <summary>
    /// Valide et insère un lot complet d'événements en ne recalculant le tri topologique qu'une seule fois à la fin du lot.
    /// </summary>
    public static bool TryIngestBatch<TEvent>(
        CausalDagState<TEvent> state,
        IEnumerable<TEvent> events,
        SignatureVerifier verifySignature,
        out string? error) where TEvent : class, ICausalEvent
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(verifySignature);

        bool anyAdded = false;
        foreach (TEvent evt in events)
        {
            if (!TryValidateAndInsertEvent(state, evt, verifySignature, out bool added, out error))
            {
                if (anyAdded)
                {
                    RebuildHeadsAndSortedHistory(state);
                }
                return false;
            }
            anyAdded |= added;
        }

        if (anyAdded)
        {
            RebuildHeadsAndSortedHistory(state);
        }

        error = null;
        return true;
    }

    private static bool TryValidateAndInsertEvent<TEvent>(
        CausalDagState<TEvent> state,
        TEvent evt,
        SignatureVerifier verifySignature,
        out bool added,
        out string? error) where TEvent : class, ICausalEvent
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(verifySignature);
        added = false;

        if (evt.EventId is null || evt.EventId.Length != HashSizeBytes)
        {
            error = "Invalid event_id length (expected 32 bytes).";
            return false;
        }

        if (evt.ScopeId is null || !evt.ScopeId.AsSpan().SequenceEqual(state.ScopeId))
        {
            error = "Event scope_id does not match the target DAG scope.";
            return false;
        }

        if (evt.Parents is null || evt.Parents.Count > MaxParents)
        {
            error = $"Event exceeds the maximum allowed parent count ({MaxParents}).";
            return false;
        }

        var uniqueParents = new HashSet<string>(StringComparer.Ordinal);
        foreach (byte[] parent in evt.Parents)
        {
            if (parent is null || parent.Length != HashSizeBytes)
            {
                error = "Invalid parent event_id length (expected 32 bytes).";
                return false;
            }

            string pHex = Convert.ToHexStringLower(parent);
            if (!uniqueParents.Add(pHex))
            {
                error = "Duplicate parent event_id in parents list.";
                return false;
            }

            if (state.EventsById.TryGetValue(pHex, out TEvent? knownParent) &&
                evt.LogicalClock <= knownParent.LogicalClock)
            {
                error = $"Lamport logical_clock ({evt.LogicalClock}) must be greater than parent logical_clock ({knownParent.LogicalClock}).";
                return false;
            }
        }

        byte[] expectedEventId = ComputeEventId(evt.Parents, evt.Payload);
        if (!expectedEventId.AsSpan().SequenceEqual(evt.EventId))
        {
            error = "EventId does not match SHA-256(parents || payload).";
            return false;
        }

        byte[] signable = evt.BuildSignableBytes();
        if (!verifySignature(evt.AuthorPublicKey, signable, evt.Signature))
        {
            error = $"Invalid signature on {typeof(TEvent).Name}.";
            return false;
        }

        string evtHex = evt.EventIdHex;
        if (state.EventsById.ContainsKey(evtHex))
        {
            error = null;
            return true; // Idempotent si déjà reçu
        }

        // Vérifier qu'aucun enfant déjà reçu n'a un logical_clock <= à ce parent arrivé en retard
        foreach (TEvent existing in state.EventsById.Values)
        {
            foreach (byte[] p in existing.Parents)
            {
                if (p.AsSpan().SequenceEqual(evt.EventId) && existing.LogicalClock <= evt.LogicalClock)
                {
                    error = $"Late parent logical_clock ({evt.LogicalClock}) violates known child logical_clock ({existing.LogicalClock}).";
                    return false;
                }
            }
        }

        state.EventsById[evtHex] = evt;
        if (evt.LogicalClock > state.MaxLogicalClock)
        {
            state.MaxLogicalClock = evt.LogicalClock;
        }

        added = true;
        error = null;
        return true;
    }

    /// <summary>
    /// Recalcule les extrémités (Heads) et trie l'historique complet selon :
    /// 1. La causalité des parents (un parent précède toujours ses enfants),
    /// 2. logical_clock (horloge de Lamport croissante),
    /// 3. timestamp_ms (uniquement pour départager deux événements envoyés simultanément avec le même logical_clock),
    /// 4. event_id hexadécimal (bris d'égalité déterministe final).
    /// </summary>
    private static void RebuildHeadsAndSortedHistory<TEvent>(CausalDagState<TEvent> state)
        where TEvent : class, ICausalEvent
    {
        var referencedAsParent = new HashSet<string>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string id in state.EventsById.Keys)
        {
            inDegree[id] = 0;
            childrenOf[id] = new List<string>();
        }

        foreach ((string childId, TEvent childEvt) in state.EventsById)
        {
            foreach (byte[] parentBytes in childEvt.Parents)
            {
                string parentId = Convert.ToHexStringLower(parentBytes);
                referencedAsParent.Add(parentId);

                // Si le parent est déjà connu localement, ajouter l'arête causale parent -> enfant
                if (state.EventsById.ContainsKey(parentId))
                {
                    inDegree[childId]++;
                    childrenOf[parentId].Add(childId);
                }
            }
        }

        state.HeadIds.Clear();
        foreach (string id in state.EventsById.Keys)
        {
            if (!referencedAsParent.Contains(id))
            {
                state.HeadIds.Add(id);
            }
        }

        // File de priorité (Kahn topologique) ordonnée par (LogicalClock, TimestampMs, EventIdHex)
        var readyQueue = new PriorityQueue<TEvent, (ulong Clock, ulong TimestampMs, string EventIdHex)>(
            Comparer<(ulong Clock, ulong TimestampMs, string EventIdHex)>.Create(static (a, b) =>
            {
                int cmpClock = a.Clock.CompareTo(b.Clock);
                if (cmpClock != 0)
                {
                    return cmpClock;
                }

                int cmpTs = a.TimestampMs.CompareTo(b.TimestampMs);
                if (cmpTs != 0)
                {
                    return cmpTs;
                }

                return string.CompareOrdinal(a.EventIdHex, b.EventIdHex);
            }));

        foreach ((string id, int deg) in inDegree)
        {
            if (deg == 0)
            {
                TEvent e = state.EventsById[id];
                readyQueue.Enqueue(e, (e.LogicalClock, e.TimestampMs, e.EventIdHex));
            }
        }

        state.SortedHistory.Clear();
        while (readyQueue.Count > 0)
        {
            TEvent current = readyQueue.Dequeue();
            state.SortedHistory.Add(current);

            foreach (string childId in childrenOf[current.EventIdHex])
            {
                inDegree[childId]--;
                if (inDegree[childId] == 0)
                {
                    TEvent child = state.EventsById[childId];
                    readyQueue.Enqueue(child, (child.LogicalClock, child.TimestampMs, child.EventIdHex));
                }
            }
        }
    }
}
