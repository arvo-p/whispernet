using System.Collections.Concurrent;
using System.Text.Json;
using Whispernet.App.Messaging;
using Whispernet.Core;
using Whispernet.Daemon.Contracts;
using Whispernet.Transport.Iroh;

namespace Whispernet.Daemon.Core;

/// <summary>
/// Cœur multiplateforme du démon <c>whispernetd</c> :
/// gère un unique endpoint Iroh QUIC (<c>daemon_iroh.key</c>), le cache de pairs,
/// le profilage horaire (jours ouvrés / week-ends), le relais aveugle des bundles DTN
/// (même lorsque 0 utilisateur n'est connecté) et la diffusion vers les clients IPC locaux.
/// </summary>
public sealed class WhispernetDaemonHost : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _alpn;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<AuthorId, HashSet<string>> _watchedTagsByAuthor = new();
    private readonly ConcurrentDictionary<GroupId, HashSet<NodeId>> _subscribedGroups = new();
    private readonly ConcurrentDictionary<NodeId, (NodeRole Role, DateTimeOffset SeenAtUtc)> _onlinePeers = new();
    private readonly object _tagsLock = new();

    private IrohEndpointAdapter? _transport;
    private SqlitePeerCacheRepository? _peerCache;
    private SqliteDirectMessageRepository? _dmRepo;
    private CancellationTokenSource? _cts;
    private int _heldDtnBundleCount;

    public event Action<DaemonIncomingPacketDto>? IncomingPacketAvailable;
    public event Action<DaemonPeerSeenDto>? PeerSeenAvailable;
    public event Action<string>? LogEmitted;
    public event Action? ShutdownRequested;

    public void RequestShutdown()
    {
        LogEmitted?.Invoke("[whispernetd] Shutdown requested via IPC.");
        ShutdownRequested?.Invoke();
    }

    public WhispernetDaemonHost(string dataDirectory, string alpn = "/whispernet/direct/1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _alpn = alpn;
    }

    public NodeId SelfId => _transport?.SelfId ?? new NodeId(string.Empty);
    public string SelfTicket => _transport?.SelfTicket ?? string.Empty;

    public async Task StartAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dataDirectory);

        _peerCache = new SqlitePeerCacheRepository(WhispernetPaths.GetPeerCacheDbPath(_dataDirectory));
        await _peerCache.InitializeAsync(ct).ConfigureAwait(false);

        _dmRepo = new SqliteDirectMessageRepository(WhispernetPaths.GetDirectMessagesDbPath(_dataDirectory));
        await _dmRepo.InitializeAsync(ct).ConfigureAwait(false);

        IReadOnlyList<DtnBundle> activeBundles = await _dmRepo.LoadActiveDtnBundlesAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        _heldDtnBundleCount = activeBundles.Count;

        IReadOnlyList<CachedPeerEntry> cachedPeers = await _peerCache.LoadCachedPeersAsync(100, ct).ConfigureAwait(false);
        foreach (CachedPeerEntry p in cachedPeers)
        {
            _onlinePeers[p.NodeId] = (p.Role, p.LastSeenUtc);
        }

        string keyPath = Path.Combine(_dataDirectory, "daemon_iroh.key");
        string secretB32 = IrohEndpointAdapter.GenerateOrLoadSecretKeyBase32(keyPath);
        _transport = IrohEndpointAdapter.CreateAndBind(
            alpn: _alpn,
            secretKeyBase32: secretB32,
            enableRelay: true,
            onlineTimeoutMs: 3000);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transport.StartAcceptLoop(HandleIncomingRawPayload, _cts.Token);

        LogEmitted?.Invoke($"[whispernetd] Started Iroh endpoint SelfId={_transport.SelfId.Value} (heldBundles={_heldDtnBundleCount}).");
    }

    public Task<DaemonStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int recentPeers = _onlinePeers.Values.Count(v => (now - v.SeenAtUtc) <= TimeSpan.FromMinutes(15));
        long uptime = Math.Max(0L, (long)(now - _startedAtUtc).TotalSeconds);
        return Task.FromResult(new DaemonStatusDto(
            SelfId.Value,
            SelfTicket,
            recentPeers,
            _heldDtnBundleCount,
            uptime));
    }

    public async Task<IReadOnlyList<byte[]>> RegisterWatchTagsAsync(
        RegisterWatchTagsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_tagsLock)
        {
            HashSet<string> set = _watchedTagsByAuthor.GetOrAdd(
                request.AuthorId,
                static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (string tag in request.BlindTagsHex)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    set.Add(tag);
                }
            }
        }

        if (_dmRepo is null || request.BlindTagsHex.Count == 0)
        {
            return Array.Empty<byte[]>();
        }

        IReadOnlyList<DtnBundle> matches = await _dmRepo
            .QueryDtnBundlesByBlindTagsAsync(request.BlindTagsHex, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);

        var packets = new List<byte[]>(matches.Count);
        foreach (DtnBundle bundle in matches)
        {
            var wire = new GroupWirePacket(
                GroupWirePacketKind.DtnBundle,
                DtnBundle: bundle,
                StreamId: GroupMeshService.DirectMessageStreamId);
            packets.Add(JsonSerializer.SerializeToUtf8Bytes(wire));
        }

        return packets;
    }

    public async Task<bool> SendWirePacketAsync(SendWirePacketRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_transport is null)
        {
            return false;
        }

        // [TEST-ONLY] Same-daemon loopback: when a test account sends to its own daemon SelfId,
        // deliver directly to local IPC subscribers instead of dialing SelfId over Iroh QUIC.
        if (request.TargetNodeId == _transport.SelfId)
        {
            LogEmitted?.Invoke("[whispernetd] [TEST-ONLY] Looping back wire packet addressed to local SelfId.");
            IncomingPacketAvailable?.Invoke(new DaemonIncomingPacketDto(request.SerializedPacket, DateTimeOffset.UtcNow));
            return true;
        }

        await _transport.SendUniAsync(
            request.TargetNodeId,
            request.SerializedPacket,
            request.CachedTicket,
            request.AllowDhtLookup,
            ct).ConfigureAwait(false);
        return true;
    }

    public Task<bool> ConnectPeerAsync(NodeId targetNodeId, string? cachedTicket = null, bool allowDhtLookup = true, CancellationToken ct = default)
    {
        if (_transport is null)
        {
            return Task.FromResult(false);
        }

        return _transport.ConnectSmartAsync(targetNodeId, cachedTicket, allowDhtLookup, ct);
    }

    public Task<bool> SubscribeGroupAsync(SubscribeGroupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        HashSet<NodeId> set = _subscribedGroups.GetOrAdd(request.GroupId, static _ => new HashSet<NodeId>());
        lock (set)
        {
            foreach (NodeId peer in request.KnownPeers)
            {
                if (peer != SelfId && !string.IsNullOrWhiteSpace(peer.Value))
                {
                    set.Add(peer);
                }
            }
        }
        return Task.FromResult(true);
    }

    private void HandleIncomingRawPayload(byte[] rawPayload)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GroupWirePacket? packet = null;
        try
        {
            packet = JsonSerializer.Deserialize<GroupWirePacket>(rawPayload);
        }
        catch (JsonException)
        {
        }

        if (packet is not null)
        {
            switch (packet.Kind)
            {
                case GroupWirePacketKind.DtnTagQuery when packet.DtnTagQuery is not null:
                    RecordPeerSeen(packet.DtnTagQuery.RequesterNodeId, NodeRole.Peer, now);
                    _ = HandleDtnTagQueryAsync(packet.DtnTagQuery);
                    return;

                case GroupWirePacketKind.DtnBundle when packet.DtnBundle is not null:
                    _ = HandleIncomingDtnBundleAsync(packet.DtnBundle, rawPayload, now);
                    return;

                case GroupWirePacketKind.DirectMessage when packet.DirectMessage?.SenderNodeId is NodeId dmSender:
                    RecordPeerSeen(dmSender, NodeRole.Peer, now);
                    break;

                case GroupWirePacketKind.RosterSync when packet.SyncPayload?.SenderNodeId is NodeId syncSender:
                    RecordPeerSeen(syncSender, NodeRole.Peer, now);
                    break;

                case GroupWirePacketKind.DagSyncRequest when packet.DagSyncRequest is not null:
                    RecordPeerSeen(packet.DagSyncRequest.RequesterNodeId, NodeRole.Peer, now);
                    break;

                case GroupWirePacketKind.DagSyncBatch when packet.DagSyncBatch?.SenderNodeId is NodeId batchSender:
                    RecordPeerSeen(batchSender, NodeRole.Peer, now);
                    break;
            }
        }

        IncomingPacketAvailable?.Invoke(new DaemonIncomingPacketDto(rawPayload, now));
    }

    private async Task HandleIncomingDtnBundleAsync(DtnBundle bundle, byte[] rawPayload, DateTimeOffset now)
    {
        if (bundle.ExpiresAtUtc <= now)
        {
            return;
        }

        bool hasLocalWatcher;
        lock (_tagsLock)
        {
            hasLocalWatcher = _watchedTagsByAuthor.Values.Any(set => set.Contains(bundle.BlindRoutingTagHex));
        }

        if (hasLocalWatcher)
        {
            IncomingPacketAvailable?.Invoke(new DaemonIncomingPacketDto(rawPayload, now));
        }

        if (_dmRepo is not null && bundle.HopCount < bundle.MaxHops)
        {
            DtnBundle stored = bundle with { HopCount = bundle.HopCount + 1 };
            await _dmRepo.UpsertDtnBundleAsync(stored).ConfigureAwait(false);
            Interlocked.Increment(ref _heldDtnBundleCount);
            LogEmitted?.Invoke($"[whispernetd] Stored blind DTN bundle {bundle.BundleId.ToString("D")[..8]} (tag={bundle.BlindRoutingTagHex[..8]}..).");
        }
    }

    private async Task HandleDtnTagQueryAsync(DtnTagQuery query)
    {
        if (_dmRepo is null || _transport is null || query.BlindTagsHex.Count == 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<DtnBundle> matches = await _dmRepo
                .QueryDtnBundlesByBlindTagsAsync(query.BlindTagsHex, DateTimeOffset.UtcNow)
                .ConfigureAwait(false);

            foreach (DtnBundle bundle in matches)
            {
                var packet = new GroupWirePacket(
                    GroupWirePacketKind.DtnBundle,
                    DtnBundle: bundle,
                    StreamId: GroupMeshService.DirectMessageStreamId);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
                await _transport.SendUniAsync(query.RequesterNodeId, bytes, allowDhtLookup: true).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private void RecordPeerSeen(NodeId nodeId, NodeRole role, DateTimeOffset seenAtUtc)
    {
        if (string.IsNullOrWhiteSpace(nodeId.Value) || nodeId == SelfId)
        {
            return;
        }

        _onlinePeers[nodeId] = (role, seenAtUtc);
        PeerSeenAvailable?.Invoke(new DaemonPeerSeenDto(nodeId, role, seenAtUtc));

        if (_peerCache is not null)
        {
            var entry = new CachedPeerEntry(nodeId, role, null, seenAtUtc, 1, 0, 0, seenAtUtc);
            _ = _peerCache.ApplyCacheUpdateAsync(new[] { entry }, Array.Empty<NodeId>());
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _transport?.Dispose();
        _transport = null;
        return ValueTask.CompletedTask;
    }
}
