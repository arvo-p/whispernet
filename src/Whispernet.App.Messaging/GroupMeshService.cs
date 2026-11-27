using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Whispernet.Core;
using Whispernet.Transport.Iroh;

namespace Whispernet.App.Messaging;

/// <summary>
/// Message applicatif échangé dans une conversation ou un groupe.
/// </summary>
public sealed record DirectChatMessage(
    string ConversationId,
    NodeId SenderId,
    string SenderName,
    string Content,
    DateTimeOffset SentAtUtc,
    AuthorId? AuthorId = null);

/// <summary>
/// Charge utile d'échange anti-entropie d'un groupe : manifeste courant + registre complet des membres.
/// </summary>
public sealed record GroupRosterSyncPayload(
    GroupManifest Manifest,
    IReadOnlyList<ModerationAction> MemberActions,
    NodeId? SenderNodeId = null);

public enum GroupWirePacketKind
{
    None = 0,
    Chat = 1,
    JoinRequest = 2,
    RosterSync = 3,
    ModerationAction = 4,
    DagSyncRequest = 5,
    DagSyncBatch = 6
}

public sealed record ChatDagSyncRequest(
    GroupId GroupId,
    NodeId RequesterNodeId,
    IReadOnlyList<string> KnownHeadIds);

public sealed record ChatDagSyncBatch(
    GroupId GroupId,
    int BatchIndex,
    int TotalBatches,
    IReadOnlyList<ChatEvent> Events,
    NodeId? SenderNodeId = null);

/// <summary>
/// Enveloppe réseau multiplexée sur Iroh QUIC pour les messages de chat, demandes d'admission et synchro de groupe.
/// StreamId 0x02 est réservé à la synchronisation topologique par lots du DAG.
/// </summary>
public sealed record GroupWirePacket(
    GroupWirePacketKind Kind,
    DirectChatMessage? Chat = null,
    JoinApprovalRequest? JoinRequest = null,
    GroupRosterSyncPayload? SyncPayload = null,
    ModerationAction? ModerationAction = null,
    ChatDagSyncRequest? DagSyncRequest = null,
    ChatDagSyncBatch? DagSyncBatch = null,
    byte StreamId = 0x01);

/// <summary>
/// Service unifié de messagerie de groupe avec topologie adaptative :
/// maillage 1-on-1 direct jusqu'à 16 pairs, puis bascule automatique vers Plumtree
/// (<c>ceil(ln(N)) + 2</c> connexions actives) avec hystérésis (repli sous 12 pairs),
/// gouvernance CRDT, échange de la liste des membres et comptage de références QUIC multi-groupes.
/// </summary>
public sealed class GroupMeshService
{
    public const byte DagSyncStreamId = 0x02;

    private readonly IrohEndpointAdapter _transport;
    private readonly IPeerCacheRepository? _peerCache;
    private readonly ConcurrentDictionary<string, NodeState> _conversations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GroupGovernanceState> _groupGovernance = new(StringComparer.Ordinal);

    public event Action<DirectChatMessage>? MessageReceived;
    public event Action<JoinApprovalRequest>? JoinRequestReceived;
    public event Action<GroupRosterSyncPayload>? RosterSyncReceived;
    public event Action<ModerationAction>? ModerationActionReceived;
    public event Action<ChatDagSyncRequest>? DagSyncRequestReceived;
    public event Action<ChatDagSyncBatch>? DagSyncBatchReceived;

    public GroupMeshService(IrohEndpointAdapter transport, IPeerCacheRepository? peerCache = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _peerCache = peerCache;
    }

    /// <summary>
    /// Enregistre ou met à jour la liste des participants d'une conversation ou d'un groupe
    /// en utilisant <see cref="HyParViewConfig.AdaptiveGroupDefaults"/>.
    /// </summary>
    public void RegisterConversation(string conversationId, IEnumerable<NodeId> memberIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var distinctMembers = memberIds
            .Where(id => id != _transport.SelfId)
            .Distinct()
            .ToHashSet();

        var nodeState = new NodeState(_transport.SelfId, NodeRole.Peer, HyParViewConfig.AdaptiveGroupDefaults);
        foreach (NodeId member in distinctMembers)
        {
            nodeState.HyParView.ActiveView.Add(member);
        }

        var actions = new List<OutAction>();
        HyParViewEngine.TrimActiveViewToEffectiveCapacity(nodeState.HyParView, actions, Random.Shared);
        foreach (NodeId activePeer in nodeState.HyParView.ActiveView)
        {
            nodeState.Plumtree.EagerPeers.Add(activePeer);
        }

        _conversations[conversationId] = nodeState;
    }

    /// <summary>
    /// Enregistre un groupe gouverné (manifeste + liste d'admis CRDT) et initialise son overlay adaptatif.
    /// </summary>
    public void RegisterGroup(GroupGovernanceState governanceState, IEnumerable<NodeId>? bootstrapPeers = null)
    {
        ArgumentNullException.ThrowIfNull(governanceState);
        string key = governanceState.GroupId.Value;
        _groupGovernance[key] = governanceState;

        var members = new HashSet<NodeId>(governanceState.GetRoomMemberEndpoints(_transport.SelfId));
        if (_conversations.TryGetValue(key, out NodeState? existingState))
        {
            foreach (NodeId existingPeer in existingState.HyParView.ActiveView)
            {
                members.Add(existingPeer);
            }
        }
        if (bootstrapPeers is not null)
        {
            foreach (NodeId peer in bootstrapPeers)
            {
                members.Add(peer);
            }
        }

        RegisterConversation(key, members);
    }

    public bool TryGetConversationState(string conversationId, out NodeState? state)
        => _conversations.TryGetValue(conversationId, out state);

    public bool TryGetGroupGovernanceState(GroupId groupId, out GroupGovernanceState? state)
        => _groupGovernance.TryGetValue(groupId.Value, out state);

    /// <summary>
    /// Retourne true uniquement si le pair n'est présent dans la ActiveView d'aucun autre groupe actif
    /// (évite de fermer une connexion QUIC partagée à l'intersection de plusieurs groupes).
    /// </summary>
    public bool ShouldClosePhysicalConnection(NodeId peer, string leavingGroupId)
    {
        foreach ((string groupId, NodeState state) in _conversations)
        {
            if (groupId.Equals(leavingGroupId, StringComparison.Ordinal))
            {
                continue;
            }

            if (state.HyParView.ActiveView.Contains(peer))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Construit le résumé anti-entropie d'un groupe donné (synchronisation strictement par groupe).
    /// </summary>
    public GroupStateDigest BuildGroupDigest(GroupId groupId)
    {
        if (!_groupGovernance.TryGetValue(groupId.Value, out GroupGovernanceState? govState))
        {
            throw new KeyNotFoundException($"Group {groupId} is not registered.");
        }

        return govState.GetDigest();
    }

    /// <summary>
    /// Construit le paquet de synchronisation complet (manifeste + liste des membres admis/modérés).
    /// </summary>
    public GroupRosterSyncPayload BuildSyncPayload(GroupId groupId)
    {
        if (!_groupGovernance.TryGetValue(groupId.Value, out GroupGovernanceState? govState))
        {
            throw new KeyNotFoundException($"Group {groupId} is not registered.");
        }

        return new GroupRosterSyncPayload(
            govState.CurrentManifest,
            govState.GovernanceLog.Values.ToList(),
            _transport.SelfId);
    }

    /// <summary>
    /// Applique un paquet de synchronisation anti-entropie entrant et met à jour l'overlay du groupe.
    /// </summary>
    public bool ApplySyncPayload(
        GroupRosterSyncPayload payload,
        GroupGovernanceEngine.SignatureVerifier verifySignature,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);

        GroupId groupId = payload.Manifest.GroupId;
        if (!_groupGovernance.TryGetValue(groupId.Value, out GroupGovernanceState? govState))
        {
            if (!GroupGovernanceEngine.TryValidateManifest(payload.Manifest, verifySignature, out error))
            {
                return false;
            }
            govState = new GroupGovernanceState(payload.Manifest);
            _groupGovernance[groupId.Value] = govState;
        }
        else if (payload.Manifest.Version > govState.CurrentManifest.Version)
        {
            if (!GroupGovernanceEngine.TryApplyManifest(govState, payload.Manifest, verifySignature, out error))
            {
                return false;
            }
        }

        if (!govState.CurrentManifest.IsTerminated)
        {
            foreach (ModerationAction action in payload.MemberActions.OrderBy(static a => a.LamportClock))
            {
                if (!GroupGovernanceEngine.TryApplyModerationAction(govState, action, verifySignature, out _, out error))
                {
                    return false;
                }
            }

            GroupGovernanceEngine.CompactGovernanceLog(govState);
        }

        IEnumerable<NodeId>? bootstrap = null;
        if (payload.SenderNodeId is NodeId senderNode)
        {
            bootstrap = new[] { senderNode };
            foreach (AuthorId memberAuthor in govState.MemberStates.Keys)
            {
                if (!govState.KnownAuthorNodes.ContainsKey(memberAuthor) &&
                    govState.GetMemberStatus(memberAuthor) is MemberStatus.Admitted or MemberStatus.Silenced)
                {
                    govState.KnownAuthorNodes[memberAuthor] = senderNode;
                }
            }
        }
        RegisterGroup(govState, bootstrap);
        error = null;
        return true;
    }

    /// <summary>
    /// Retourne les cibles de reconnexion pour un groupe : amis en ligne en priorité,
    /// puis repli automatique sur les autres membres admis du salon (non-amis) si 0 ami n'est disponible.
    /// </summary>
    public IReadOnlyList<NodeId> GetReconnectTargets(GroupId groupId, IEnumerable<NodeId>? onlineFriends = null)
    {
        List<NodeId> friends = onlineFriends?
            .Where(id => id != _transport.SelfId)
            .Distinct()
            .ToList() ?? new List<NodeId>();

        if (friends.Count > 0)
        {
            return friends;
        }

        if (_groupGovernance.TryGetValue(groupId.Value, out GroupGovernanceState? govState))
        {
            return govState.GetRoomMemberEndpoints(_transport.SelfId);
        }

        return Array.Empty<NodeId>();
    }

    /// <summary>
    /// Vérifie si un message entrant respecte les règles de gouvernance (Admitted, non Silenced, non expiré).
    /// </summary>
    public bool CanAcceptGroupPost(GroupId groupId, AuthorId authorId, DateTimeOffset sentAtUtc, DateTimeOffset nowUtc)
    {
        if (!_groupGovernance.TryGetValue(groupId.Value, out GroupGovernanceState? govState))
        {
            return false;
        }

        if (!GroupGovernanceEngine.CanAuthorPost(govState, authorId))
        {
            return false;
        }

        if (govState.CurrentManifest.EphemeralMessageTtl is TimeSpan ttl && (nowUtc - sentAtUtc) > ttl)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Envoie un message aux voisins actifs de la conversation (tous les pairs en Full Mesh &lt;= 16,
    /// ou <c>ceil(ln(N)) + 2</c> pairs actifs en mode Plumtree) via Mainline DHT / cache SQLite.
    /// </summary>
    public async Task<IReadOnlyDictionary<NodeId, bool>> SendMessageAsync(
        string conversationId,
        string senderName,
        string content,
        CancellationToken ct = default,
        AuthorId? senderAuthorId = null)
    {
        if (!_conversations.TryGetValue(conversationId, out NodeState? nodeState) ||
            nodeState.HyParView.ActiveView.Count == 0)
        {
            throw new InvalidOperationException($"Unknown conversation '{conversationId}' or no active peers.");
        }

        if (_groupGovernance.TryGetValue(conversationId, out GroupGovernanceState? govState) &&
            senderAuthorId is AuthorId authorId &&
            !GroupGovernanceEngine.CanAuthorPost(govState, authorId))
        {
            throw new UnauthorizedAccessException($"Author {authorId} is not allowed to post in group {conversationId}.");
        }

        List<NodeId> targets = nodeState.HyParView.ActiveView.ToList();

        var msg = new DirectChatMessage(
            ConversationId: conversationId,
            SenderId: _transport.SelfId,
            SenderName: senderName,
            Content: content,
            SentAtUtc: DateTimeOffset.UtcNow,
            AuthorId: senderAuthorId);

        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
        return await SendPayloadToTargetsAsync(targets, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Diffuse un <see cref="ChatEvent"/> signé (encapsulé en lot d'un élément sur le flux 0x02)
    /// à tous les pairs actifs du groupe.
    /// </summary>
    public async Task<IReadOnlyDictionary<NodeId, bool>> BroadcastChatEventAsync(
        GroupId groupId,
        ChatEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!_conversations.TryGetValue(groupId.Value, out NodeState? nodeState) ||
            nodeState.HyParView.ActiveView.Count == 0)
        {
            return new Dictionary<NodeId, bool>();
        }

        if (_groupGovernance.TryGetValue(groupId.Value, out GroupGovernanceState? govState))
        {
            AuthorId authorId = evt.GetAuthorId();
            if (!GroupGovernanceEngine.CanAuthorPost(govState, authorId))
            {
                throw new UnauthorizedAccessException($"Author {authorId} is not allowed to post in group {groupId}.");
            }
        }

        var batch = new ChatDagSyncBatch(groupId, 0, 1, new[] { evt }, _transport.SelfId);
        var packet = new GroupWirePacket(
            GroupWirePacketKind.DagSyncBatch,
            DagSyncBatch: batch,
            StreamId: DagSyncStreamId);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet);

        List<NodeId> targets = nodeState.HyParView.ActiveView.ToList();
        return await SendPayloadToTargetsAsync(targets, payload, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<NodeId, bool>> SendPayloadToTargetsAsync(
        IReadOnlyList<NodeId> targets,
        byte[] payload,
        CancellationToken ct)
    {
        // Charger les éventuels tickets déjà en cache SQLite pour accélérer la connexion avant le fallback DHT
        Dictionary<NodeId, CachedPeerEntry> cachedLookup = new();
        if (_peerCache is not null)
        {
            var cached = await _peerCache.LoadCachedPeersAsync(100, ct).ConfigureAwait(false);
            foreach (CachedPeerEntry entry in cached)
            {
                cachedLookup[entry.NodeId] = entry;
            }
        }

        var results = new ConcurrentDictionary<NodeId, bool>();
        var sendTasks = targets.Select(async memberId =>
        {
            cachedLookup.TryGetValue(memberId, out CachedPeerEntry? cachedEntry);
            try
            {
                await _transport.SendUniAsync(
                    target: memberId,
                    payload: payload,
                    cachedTicket: cachedEntry?.EndpointTicket,
                    allowDhtLookup: true,
                    ct: ct).ConfigureAwait(false);

                results[memberId] = true;

                if (_peerCache is not null)
                {
                    var updated = cachedEntry is not null
                        ? cachedEntry with
                        {
                            LastSeenUtc = DateTimeOffset.UtcNow,
                            SuccessCount = cachedEntry.SuccessCount + 1,
                            ConsecutiveFailures = 0,
                            LastAttemptUtc = DateTimeOffset.UtcNow
                        }
                        : new CachedPeerEntry(memberId, NodeRole.Peer, null, DateTimeOffset.UtcNow, SuccessCount: 1);

                    await _peerCache.ApplyCacheUpdateAsync(new[] { updated }, Array.Empty<NodeId>(), ct).ConfigureAwait(false);
                }
            }
            catch
            {
                results[memberId] = false;
            }
        });

        await Task.WhenAll(sendTasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Envoie une demande d'admission (<see cref="JoinApprovalRequest"/>) à un pair ou modérateur via Iroh QUIC.
    /// </summary>
    public async Task SendJoinRequestAsync(
        NodeId targetPeer,
        JoinApprovalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var packet = new GroupWirePacket(GroupWirePacketKind.JoinRequest, JoinRequest: request);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet);
        await _transport.SendUniAsync(targetPeer, payload, allowDhtLookup: true, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Envoie l'état complet d'un groupe (<see cref="GroupRosterSyncPayload"/>) à un pair via Iroh QUIC.
    /// </summary>
    public async Task SendSyncPayloadAsync(
        NodeId targetPeer,
        GroupRosterSyncPayload syncPayload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(syncPayload);
        var packet = new GroupWirePacket(GroupWirePacketKind.RosterSync, SyncPayload: syncPayload);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet);
        await _transport.SendUniAsync(targetPeer, payload, allowDhtLookup: true, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Diffuse le manifeste et l'état courant du groupe (ex: mise à jour des règles ou fermeture définitive)
    /// à tous les pairs actifs du groupe.
    /// </summary>
    public async Task BroadcastSyncPayloadAsync(
        GroupId groupId,
        CancellationToken ct = default)
    {
        if (!_conversations.TryGetValue(groupId.Value, out NodeState? nodeState))
        {
            return;
        }

        GroupRosterSyncPayload syncPayload = BuildSyncPayload(groupId);
        var packet = new GroupWirePacket(GroupWirePacketKind.RosterSync, SyncPayload: syncPayload);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet);

        IEnumerable<Task> tasks = nodeState.HyParView.ActiveView.Select(peer =>
            _transport.SendUniAsync(peer, payload, allowDhtLookup: true, ct: ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Diffuse une décision de modération signée aux pairs actifs du groupe.
    /// </summary>
    public async Task BroadcastModerationActionAsync(
        GroupId groupId,
        ModerationAction action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_conversations.TryGetValue(groupId.Value, out NodeState? nodeState))
        {
            return;
        }

        var packet = new GroupWirePacket(GroupWirePacketKind.ModerationAction, ModerationAction: action);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet);

        IEnumerable<Task> tasks = nodeState.HyParView.ActiveView.Select(peer =>
            _transport.SendUniAsync(peer, payload, allowDhtLookup: true, ct: ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Envoie uniquement les têtes courantes (<see cref="ChatRoomDagState.HeadIds"/>) sur le flux 0x02
    /// pour demander la tranche topologique manquante en lots (sans jamais récupérer les parents un par un).
    /// </summary>
    public async Task SendDagSyncRequestAsync(
        NodeId targetPeer,
        GroupId groupId,
        ChatRoomDagState localDag,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localDag);
        var req = new ChatDagSyncRequest(groupId, _transport.SelfId, localDag.HeadIds.ToList());
        var packet = new GroupWirePacket(
            GroupWirePacketKind.DagSyncRequest,
            DagSyncRequest: req,
            StreamId: DagSyncStreamId);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet);
        await _transport.SendUniAsync(targetPeer, payload, allowDhtLookup: true, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Calcule la tranche topologique manquante entre les têtes distantes et les têtes locales,
    /// puis la diffuse par lots (500 événements par lot par défaut) sur le flux 0x02.
    /// </summary>
    public async Task<int> StreamTopologicalSliceAsync(
        NodeId targetPeer,
        GroupId groupId,
        ChatRoomDagState localDag,
        IReadOnlyList<string> remoteHeadIds,
        int batchSize = CausalDagEngine.DefaultSyncBatchSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localDag);
        IReadOnlyList<ChatEvent> slice = CausalDagEngine.ComputeMissingTopologicalSlice(localDag, remoteHeadIds);
        IReadOnlyList<IReadOnlyList<ChatEvent>> batches = CausalDagEngine.ChunkTopologicalSlice(slice, batchSize);

        for (int i = 0; i < batches.Count; i++)
        {
            var batch = new ChatDagSyncBatch(groupId, i, batches.Count, batches[i], _transport.SelfId);
            var packet = new GroupWirePacket(
                GroupWirePacketKind.DagSyncBatch,
                DagSyncBatch: batch,
                StreamId: DagSyncStreamId);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(packet);
            await _transport.SendUniAsync(targetPeer, payload, allowDhtLookup: true, ct: ct).ConfigureAwait(false);
        }

        return batches.Count;
    }

    /// <summary>
    /// Traite un payload entrant reçu par la couche transport pour cette messagerie de groupe.
    /// </summary>
    public void HandleIncomingPayload(byte[] payload)
    {
        GroupWirePacket? packet = null;
        try
        {
            packet = JsonSerializer.Deserialize<GroupWirePacket>(payload);
        }
        catch (JsonException)
        {
            // Fallback vers DirectChatMessage brut
        }

        if (packet is not null && packet.Kind != GroupWirePacketKind.None)
        {
            switch (packet.Kind)
            {
                case GroupWirePacketKind.Chat when packet.Chat is not null:
                    DispatchChatMessage(packet.Chat);
                    return;

                case GroupWirePacketKind.JoinRequest when packet.JoinRequest is not null:
                    JoinRequestReceived?.Invoke(packet.JoinRequest);
                    return;

                case GroupWirePacketKind.RosterSync when packet.SyncPayload is not null:
                    RosterSyncReceived?.Invoke(packet.SyncPayload);
                    return;

                case GroupWirePacketKind.ModerationAction when packet.ModerationAction is not null:
                    ModerationActionReceived?.Invoke(packet.ModerationAction);
                    return;

                case GroupWirePacketKind.DagSyncRequest when packet.DagSyncRequest is not null:
                    if (_groupGovernance.TryGetValue(packet.DagSyncRequest.GroupId.Value, out GroupGovernanceState? reqGov))
                    {
                        RegisterGroup(reqGov, new[] { packet.DagSyncRequest.RequesterNodeId });
                    }
                    DagSyncRequestReceived?.Invoke(packet.DagSyncRequest);
                    return;

                case GroupWirePacketKind.DagSyncBatch when packet.DagSyncBatch is not null:
                    if (packet.DagSyncBatch.SenderNodeId is NodeId batchSender &&
                        _groupGovernance.TryGetValue(packet.DagSyncBatch.GroupId.Value, out GroupGovernanceState? batchGov))
                    {
                        RegisterGroup(batchGov, new[] { batchSender });
                    }
                    DagSyncBatchReceived?.Invoke(packet.DagSyncBatch);
                    return;
            }
        }

        DirectChatMessage? msg = JsonSerializer.Deserialize<DirectChatMessage>(payload);
        if (msg is not null)
        {
            DispatchChatMessage(msg);
        }
    }

    private void DispatchChatMessage(DirectChatMessage msg)
    {
        if (_groupGovernance.TryGetValue(msg.ConversationId, out GroupGovernanceState? govState))
        {
            if (msg.AuthorId is not AuthorId authorId ||
                !CanAcceptGroupPost(govState.GroupId, authorId, msg.SentAtUtc, DateTimeOffset.UtcNow))
            {
                return;
            }
        }

        MessageReceived?.Invoke(msg);
    }
}
