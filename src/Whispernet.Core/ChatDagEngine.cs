namespace Whispernet.Core;

/// <summary>
/// État en mémoire du DAG causal d'un salon de discussion.
/// </summary>
public sealed class ChatRoomDagState
{
    public byte[] RoomId { get; }
    public string RoomIdHex { get; }

    /// <summary>Tous les événements reçus indexés par leur EventIdHex.</summary>
    public Dictionary<string, ChatEvent> EventsById { get; } = new(StringComparer.Ordinal);

    /// <summary>Ensemble des extrémités courantes du DAG (messages qui n'ont encore aucun enfant connu).</summary>
    public HashSet<string> HeadIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Plus grande valeur d'horloge de Lamport observée dans ce salon.</summary>
    public ulong MaxLogicalClock { get; internal set; }

    /// <summary>Historique trié de manière déterministe (parents -> logical_clock -> timestamp_ms -> event_id).</summary>
    public List<ChatEvent> SortedHistory { get; } = new();

    public ChatRoomDagState(byte[] roomId)
    {
        if (roomId is null || roomId.Length != ChatEvent.HashSizeBytes)
        {
            throw new ArgumentException("RoomId must be a 32-byte SHA-256 hash of the group authority public key.", nameof(roomId));
        }

        RoomId = roomId.ToArray();
        RoomIdHex = Convert.ToHexStringLower(RoomId);
    }
}

/// <summary>
/// Moteur Sans-I/O de validation et de tri topologique causal + horloge de Lamport des <see cref="ChatEvent"/>.
/// </summary>
public static class ChatDagEngine
{
    public const int DefaultSyncBatchSize = 500;

    public delegate bool SignatureVerifier(
        ReadOnlySpan<byte> authorPublicKeySpki,
        ReadOnlySpan<byte> signableData,
        ReadOnlySpan<byte> signature);

    /// <summary>
    /// Calcule la tranche topologique manquante pour un pair distant connaissant <paramref name="remoteHeadIds"/> :
    /// exclut les têtes distantes et tous leurs ancêtres, et retourne les événements restants
    /// dans l'ordre causal exact de <see cref="ChatRoomDagState.SortedHistory"/>.
    /// </summary>
    public static IReadOnlyList<ChatEvent> ComputeMissingTopologicalSlice(
        ChatRoomDagState state,
        IEnumerable<string> remoteHeadIds)
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
            if (!state.EventsById.TryGetValue(currentId, out ChatEvent? evt))
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

        var slice = new List<ChatEvent>();
        foreach (ChatEvent evt in state.SortedHistory)
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
    public static IReadOnlyList<IReadOnlyList<ChatEvent>> ChunkTopologicalSlice(
        IReadOnlyList<ChatEvent> slice,
        int batchSize = DefaultSyncBatchSize)
    {
        ArgumentNullException.ThrowIfNull(slice);
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        if (slice.Count == 0)
        {
            return Array.Empty<IReadOnlyList<ChatEvent>>();
        }

        var batches = new List<IReadOnlyList<ChatEvent>>((slice.Count + batchSize - 1) / batchSize);
        for (int i = 0; i < slice.Count; i += batchSize)
        {
            int count = Math.Min(batchSize, slice.Count - i);
            var batch = new List<ChatEvent>(count);
            for (int j = 0; j < count; j++)
            {
                batch.Add(slice[i + j]);
            }
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>
    /// Sélectionne jusqu'à 15 parents (les extrémités courantes du DAG) et calcule la prochaine horloge de Lamport.
    /// </summary>
    public static (IReadOnlyList<byte[]> Parents, ulong NextLogicalClock) PrepareNextEventCausalContext(ChatRoomDagState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        List<ChatEvent> heads = state.HeadIds
            .Where(id => state.EventsById.ContainsKey(id))
            .Select(id => state.EventsById[id])
            .OrderByDescending(e => e.LogicalClock)
            .ThenByDescending(e => e.TimestampMs)
            .ThenBy(e => e.EventIdHex, StringComparer.Ordinal)
            .Take(ChatEvent.MaxParents)
            .ToList();

        List<byte[]> parentIds = heads.Select(h => h.EventId).ToList();
        ulong maxParentClock = heads.Count > 0 ? heads.Max(h => h.LogicalClock) : state.MaxLogicalClock;
        ulong nextClock = Math.Max(state.MaxLogicalClock, maxParentClock) + 1;

        return (parentIds, nextClock);
    }

    /// <summary>
    /// Valide l'intégrité cryptographique (event_id, limite de 15 parents, signature) et insère l'événement
    /// dans le DAG en recalculant l'ordre causal déterministe de l'historique.
    /// </summary>
    public static bool TryIngestEvent(
        ChatRoomDagState state,
        ChatEvent evt,
        SignatureVerifier verifySignature,
        out string? error)
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
    public static bool TryIngestBatch(
        ChatRoomDagState state,
        IEnumerable<ChatEvent> events,
        SignatureVerifier verifySignature,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(verifySignature);

        bool anyAdded = false;
        foreach (ChatEvent evt in events)
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

    private static bool TryValidateAndInsertEvent(
        ChatRoomDagState state,
        ChatEvent evt,
        SignatureVerifier verifySignature,
        out bool added,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(verifySignature);
        added = false;

        if (evt.EventId is null || evt.EventId.Length != ChatEvent.HashSizeBytes)
        {
            error = "Invalid event_id length (expected 32 bytes).";
            return false;
        }

        if (evt.RoomId is null || !evt.RoomId.AsSpan().SequenceEqual(state.RoomId))
        {
            error = "Event room_id does not match the target room.";
            return false;
        }

        if (evt.Parents is null || evt.Parents.Count > ChatEvent.MaxParents)
        {
            error = $"Event exceeds the maximum allowed parent count ({ChatEvent.MaxParents}).";
            return false;
        }

        var uniqueParents = new HashSet<string>(StringComparer.Ordinal);
        foreach (byte[] parent in evt.Parents)
        {
            if (parent is null || parent.Length != ChatEvent.HashSizeBytes)
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

            if (state.EventsById.TryGetValue(pHex, out ChatEvent? knownParent) &&
                evt.LogicalClock <= knownParent.LogicalClock)
            {
                error = $"Lamport logical_clock ({evt.LogicalClock}) must be greater than parent logical_clock ({knownParent.LogicalClock}).";
                return false;
            }
        }

        byte[] expectedEventId = ChatEvent.ComputeEventId(evt.Parents, evt.Payload);
        if (!expectedEventId.AsSpan().SequenceEqual(evt.EventId))
        {
            error = "EventId does not match SHA-256(parents || payload).";
            return false;
        }

        byte[] signable = ChatEvent.BuildSignableBytes(
            evt.EventId,
            evt.RoomId,
            evt.AuthorPublicKey,
            evt.Parents,
            evt.LogicalClock,
            evt.TimestampMs,
            evt.Payload);

        if (!verifySignature(evt.AuthorPublicKey, signable, evt.Signature))
        {
            error = "Invalid ECDSA signature on ChatEvent.";
            return false;
        }

        string evtHex = evt.EventIdHex;
        if (state.EventsById.ContainsKey(evtHex))
        {
            error = null;
            return true; // Idempotent si déjà reçu
        }

        // Vérifier qu'aucun enfant déjà reçu n'a un logical_clock <= à ce parent arrivé en retard
        foreach (ChatEvent existing in state.EventsById.Values)
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
    /// 3. timestamp_ms (uniquement pour départager deux messages envoyés simultanément avec le même logical_clock),
    /// 4. event_id hexadécimal (bris d'égalité déterministe final).
    /// </summary>
    private static void RebuildHeadsAndSortedHistory(ChatRoomDagState state)
    {
        var referencedAsParent = new HashSet<string>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string id in state.EventsById.Keys)
        {
            inDegree[id] = 0;
            childrenOf[id] = new List<string>();
        }

        foreach ((string childId, ChatEvent childEvt) in state.EventsById)
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
        var readyQueue = new PriorityQueue<ChatEvent, (ulong Clock, ulong TimestampMs, string EventIdHex)>(
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
                ChatEvent e = state.EventsById[id];
                readyQueue.Enqueue(e, (e.LogicalClock, e.TimestampMs, e.EventIdHex));
            }
        }

        state.SortedHistory.Clear();
        while (readyQueue.Count > 0)
        {
            ChatEvent current = readyQueue.Dequeue();
            state.SortedHistory.Add(current);

            foreach (string childId in childrenOf[current.EventIdHex])
            {
                inDegree[childId]--;
                if (inDegree[childId] == 0)
                {
                    ChatEvent child = state.EventsById[childId];
                    readyQueue.Enqueue(child, (child.LogicalClock, child.TimestampMs, child.EventIdHex));
                }
            }
        }
    }
}
