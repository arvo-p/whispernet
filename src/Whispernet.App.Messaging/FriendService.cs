using System.Text.Json;
using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Application-agnostic friend directory and organic mesh provider (used by Whispernet GUI, DMs, and future apps such as App.Feed).
/// </summary>
public interface IFriendDirectory
{
    bool RestrictDmsToFriends { get; }
    bool IsFriend(AuthorId peerAuthorId);
    FriendStatus GetStatus(AuthorId peerAuthorId);
    IReadOnlyList<FriendEntry> GetAcceptedFriends();
    IReadOnlyList<FriendEntry> GetIncomingRequests();
    IReadOnlyList<FriendEntry> GetOutgoingRequests();
    IReadOnlyList<NodeId> GetFriendNodeEndpoints(NodeId selfNodeId);

    event Action? FriendsChanged;
    event Action? FriendRequestsChanged;
}

public sealed record PendingOutboundFriendPacket(
    AuthorId RecipientAuthorId,
    NodeId? TargetNodeId,
    GroupWirePacket Packet,
    DateTimeOffset QueuedAtUtc);

public sealed record PersistedFriendVault(
    ulong MaxLogicalClock,
    bool RestrictDmsToFriends,
    IReadOnlyList<FriendEntry> Entries,
    IReadOnlyList<PendingOutboundFriendPacket>? PendingOutbound = null);

/// <summary>
/// Manages persistent friend state, signed friend request/accept/reject/remove protocol,
/// out-of-band <c>whispernet://friend/...</c> links, and organic friend mesh synchronization.
/// </summary>
public sealed class FriendService : IFriendDirectory
{
    public const string FriendVaultRelativePath = "friends.json";
    private const string FriendLinkPrefix = "whispernet://friend/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly IAccountService _accountService;
    private readonly object _sync = new();
    private readonly List<PendingOutboundFriendPacket> _pendingOutbound = new();

    private FriendGraphState? _state;
    private AccountSession? _session;
    private IMeshTransport? _transport;
    private GroupMeshService? _meshService;
    private DirectMessagingService? _dmService;
    private bool _restrictDmsToFriends;

    public bool RestrictDmsToFriends
    {
        get
        {
            lock (_sync)
            {
                return _restrictDmsToFriends;
            }
        }
    }

    public event Action? FriendsChanged;
    public event Action? FriendRequestsChanged;
    public event Action<string>? LogEmitted;

    public FriendService(IAccountService accountService)
    {
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
    }

    public void AttachMesh(
        IMeshTransport? transport,
        GroupMeshService? meshService,
        DirectMessagingService? dmService)
    {
        lock (_sync)
        {
            _transport = transport;
            _meshService = meshService;
            _dmService = dmService;
            if (_dmService is not null)
            {
                _dmService.IsPeerAllowedForDm = IsAuthorAllowedForDm;
            }
        }

        SyncOrganicMeshAndContacts();
    }

    public async Task LoadFromVaultAsync(AccountSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        FriendGraphState loadedState = new(session.Profile.AuthorId);
        bool restrictDms = false;
        var pending = new List<PendingOutboundFriendPacket>();

        try
        {
            byte[] bytes = await _accountService.ReadVaultFileAsync(session, FriendVaultRelativePath, ct).ConfigureAwait(false);
            PersistedFriendVault? persisted = JsonSerializer.Deserialize<PersistedFriendVault>(bytes, JsonOptions);
            if (persisted is not null)
            {
                loadedState.MaxLogicalClock = persisted.MaxLogicalClock;
                restrictDms = persisted.RestrictDmsToFriends;
                foreach (FriendEntry entry in persisted.Entries)
                {
                    loadedState.Entries[entry.PeerAuthorId] = entry;
                }
                if (persisted.PendingOutbound is not null)
                {
                    pending.AddRange(persisted.PendingOutbound);
                }
            }
        }
        catch (FileNotFoundException)
        {
            // First run for this account: start with an empty friend graph
        }

        lock (_sync)
        {
            _session = session;
            _state = loadedState;
            _restrictDmsToFriends = restrictDms;
            _pendingOutbound.Clear();
            _pendingOutbound.AddRange(pending);
            if (_dmService is not null)
            {
                _dmService.IsPeerAllowedForDm = IsAuthorAllowedForDm;
            }
        }

        SyncOrganicMeshAndContacts();
        await ConsumeTestLoopbackInboxAsync(session, ct).ConfigureAwait(false);
        FriendsChanged?.Invoke();
        FriendRequestsChanged?.Invoke();
    }

    // [TEST-ONLY] Local filesystem mailbox for same-machine / same-daemon multi-account testing.
    private static string? GetTestLoopbackInboxDir(AccountSession session, AuthorId targetAuthorId)
    {
        string? accountsDir = Directory.GetParent(session.AccountDirectory)?.FullName;
        string? rootDir = accountsDir is not null ? Directory.GetParent(accountsDir)?.FullName : null;
        if (string.IsNullOrWhiteSpace(rootDir))
        {
            return null;
        }

        return Path.Combine(rootDir, "test-loopback-friend-inbox", targetAuthorId.Value);
    }

    public Task ConsumeTestLoopbackInboxAsync(CancellationToken ct = default)
    {
        AccountSession? session;
        lock (_sync)
        {
            session = _session;
        }
        return session is null ? Task.CompletedTask : ConsumeTestLoopbackInboxAsync(session, ct);
    }

    private async Task ConsumeTestLoopbackInboxAsync(AccountSession session, CancellationToken ct)
    {
        string? inboxDir = GetTestLoopbackInboxDir(session, session.Profile.AuthorId);
        if (string.IsNullOrWhiteSpace(inboxDir) || !Directory.Exists(inboxDir))
        {
            return;
        }

        string[] files = Directory.GetFiles(inboxDir, "*.json").OrderBy(static f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            return;
        }

        int consumed = 0;
        foreach (string file in files)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                GroupWirePacket? packet = JsonSerializer.Deserialize<GroupWirePacket>(bytes);
                if (packet is not null)
                {
                    switch (packet.Kind)
                    {
                        case GroupWirePacketKind.FriendRequest when packet.FriendRequest is not null:
                            await HandleIncomingFriendRequestAsync(packet.FriendRequest, ct).ConfigureAwait(false);
                            consumed++;
                            break;
                        case GroupWirePacketKind.FriendResponse when packet.FriendResponse is not null:
                            await HandleIncomingFriendResponseAsync(packet.FriendResponse, ct).ConfigureAwait(false);
                            consumed++;
                            break;
                        case GroupWirePacketKind.FriendRemove when packet.FriendRemove is not null:
                            await HandleIncomingFriendRemoveAsync(packet.FriendRemove, ct).ConfigureAwait(false);
                            consumed++;
                            break;
                    }
                }
            }
            catch
            {
                // Ignore malformed test loopback file
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        if (consumed > 0)
        {
            LogEmitted?.Invoke($"[Friend] [TEST-ONLY] Consumed {consumed} same-daemon loopback packet(s) from test-loopback-friend-inbox.");
        }
    }

    public bool IsAuthorAllowedForDm(AuthorId peerAuthorId)
    {
        lock (_sync)
        {
            if (!_restrictDmsToFriends)
            {
                return true;
            }

            return _state?.IsFriend(peerAuthorId) == true;
        }
    }

    public bool IsFriend(AuthorId peerAuthorId)
    {
        lock (_sync)
        {
            return _state?.IsFriend(peerAuthorId) == true;
        }
    }

    public FriendStatus GetStatus(AuthorId peerAuthorId)
    {
        lock (_sync)
        {
            return _state?.GetStatus(peerAuthorId) ?? FriendStatus.None;
        }
    }

    public IReadOnlyList<FriendEntry> GetAcceptedFriends()
    {
        lock (_sync)
        {
            return _state?.GetAcceptedFriends() ?? Array.Empty<FriendEntry>();
        }
    }

    public IReadOnlyList<FriendEntry> GetIncomingRequests()
    {
        lock (_sync)
        {
            return _state?.GetIncomingRequests() ?? Array.Empty<FriendEntry>();
        }
    }

    public IReadOnlyList<FriendEntry> GetOutgoingRequests()
    {
        lock (_sync)
        {
            return _state?.GetOutgoingRequests() ?? Array.Empty<FriendEntry>();
        }
    }

    public IReadOnlyList<NodeId> GetFriendNodeEndpoints(NodeId selfNodeId)
    {
        lock (_sync)
        {
            return _state?.GetFriendEndpoints(selfNodeId) ?? Array.Empty<NodeId>();
        }
    }

    public async Task SetRestrictDmsToFriendsAsync(bool restrict, CancellationToken ct = default)
    {
        AccountSession? session;
        lock (_sync)
        {
            _restrictDmsToFriends = restrict;
            session = _session;
        }

        if (session is not null)
        {
            await SaveToVaultAsync(session, ct).ConfigureAwait(false);
        }

        FriendsChanged?.Invoke();
    }

    public string CreateFriendLink(AccountSession session, NodeId selfNodeId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var data = new FriendInvitationData(
            AuthorId: session.Profile.AuthorId,
            PublicKeySpkiBase64: session.Profile.PublicKeySpkiBase64,
            DisplayName: session.Profile.Username,
            BootstrapNodeId: selfNodeId);

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        return $"{FriendLinkPrefix}{ToBase64Url(jsonBytes)}";
    }

    public static FriendInvitationData ParseFriendLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new ArgumentException("Friend link cannot be empty.", nameof(link));
        }

        string trimmed = link.Trim();
        if (!trimmed.StartsWith(FriendLinkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Friend link must start with 'whispernet://friend/'.");
        }

        string payload = trimmed[FriendLinkPrefix.Length..];
        byte[] jsonBytes = FromBase64Url(payload);
        FriendInvitationData data = JsonSerializer.Deserialize<FriendInvitationData>(jsonBytes, JsonOptions)
            ?? throw new InvalidDataException("Invalid friend link payload.");

        if (!FriendGraphEngine.TryValidateAuthorSpki(data.AuthorId, data.PublicKeySpkiBase64, out _, out string? err))
        {
            throw new InvalidDataException($"Invalid friend link identity: {err}");
        }

        return data;
    }

    public async Task<FriendEntry> SendFriendRequestAsync(
        AuthorId recipientAuthorId,
        string recipientPublicKeySpkiBase64,
        string recipientDisplayName,
        NodeId? recipientNodeId = null,
        CancellationToken ct = default)
    {
        AccountSession session;
        FriendGraphState state;
        IMeshTransport? transport;
        lock (_sync)
        {
            session = _session ?? throw new InvalidOperationException("No active unlocked account session.");
            state = _state ?? throw new InvalidOperationException("Friend graph not loaded.");
            transport = _transport;
        }

        if (recipientAuthorId == session.Profile.AuthorId)
        {
            throw new InvalidOperationException("You cannot add yourself as a friend.");
        }

        if (!FriendGraphEngine.TryValidateAuthorSpki(recipientAuthorId, recipientPublicKeySpkiBase64, out _, out string? err))
        {
            throw new InvalidOperationException($"Invalid recipient public key: {err}");
        }

        FriendRequestPacket signedPacket;
        FriendEntry entry;
        lock (_sync)
        {
            FriendStatus curStatus = state.GetStatus(recipientAuthorId);
            if (curStatus == FriendStatus.Accepted)
            {
                return state.Entries[recipientAuthorId];
            }

            // If we already have a PendingIncoming from them, accept it instead!
            if (curStatus == FriendStatus.PendingIncoming)
            {
                // Fall through to accept below
                goto AcceptExisting;
            }

            ulong clock = FriendGraphEngine.NextClock(state, recipientAuthorId);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var unsigned = new FriendRequestPacket(
                PacketUuid: Guid.NewGuid(),
                SenderAuthorId: session.Profile.AuthorId,
                RecipientAuthorId: recipientAuthorId,
                SenderPublicKeySpkiBase64: session.Profile.PublicKeySpkiBase64,
                SenderDisplayName: session.Profile.Username,
                SenderNodeId: transport?.SelfId,
                LogicalClock: clock,
                SentAtUtc: now,
                SignatureBase64: string.Empty);

            byte[] sig = _accountService.SignData(session, unsigned.BuildSignableBytes());
            signedPacket = unsigned with { SignatureBase64 = Convert.ToBase64String(sig) };

            entry = new FriendEntry(
                PeerAuthorId: recipientAuthorId,
                PeerPublicKeySpkiBase64: recipientPublicKeySpkiBase64,
                DisplayName: string.IsNullOrWhiteSpace(recipientDisplayName) ? recipientAuthorId.Value : recipientDisplayName.Trim(),
                LastKnownNodeId: recipientNodeId,
                Status: FriendStatus.PendingOutgoing,
                LogicalClock: clock,
                UpdatedAtUtc: now);

            state.Entries[recipientAuthorId] = entry;
            goto Dispatch;
        }

    AcceptExisting:
        return await AcceptFriendRequestAsync(recipientAuthorId, ct).ConfigureAwait(false);

    Dispatch:
        var wirePacket = new GroupWirePacket(
            GroupWirePacketKind.FriendRequest,
            StreamId: GroupMeshService.FriendStreamId,
            FriendRequest: signedPacket);

        await SendOrQueuePacketAsync(session, recipientAuthorId, recipientNodeId, wirePacket, ct).ConfigureAwait(false);
        await SaveToVaultAsync(session, ct).ConfigureAwait(false);
        FriendRequestsChanged?.Invoke();
        LogEmitted?.Invoke($"[Friend] Sent friend request to {entry.DisplayName} ({recipientAuthorId}).");
        return entry;
    }

    public async Task<FriendEntry> AcceptFriendRequestAsync(AuthorId peerAuthorId, CancellationToken ct = default)
        => await RespondToRequestAsync(peerAuthorId, accepted: true, ct).ConfigureAwait(false);

    public async Task<FriendEntry> RejectFriendRequestAsync(AuthorId peerAuthorId, CancellationToken ct = default)
        => await RespondToRequestAsync(peerAuthorId, accepted: false, ct).ConfigureAwait(false);

    private async Task<FriendEntry> RespondToRequestAsync(AuthorId peerAuthorId, bool accepted, CancellationToken ct)
    {
        AccountSession session;
        FriendGraphState state;
        IMeshTransport? transport;
        FriendResponsePacket signedPacket;
        FriendEntry updated;
        NodeId? targetNode;

        lock (_sync)
        {
            session = _session ?? throw new InvalidOperationException("No active unlocked account session.");
            state = _state ?? throw new InvalidOperationException("Friend graph not loaded.");
            transport = _transport;

            if (!state.Entries.TryGetValue(peerAuthorId, out FriendEntry? existing) ||
                existing.Status != FriendStatus.PendingIncoming)
            {
                throw new InvalidOperationException($"No pending incoming friend request from {peerAuthorId}.");
            }

            ulong clock = FriendGraphEngine.NextClock(state, peerAuthorId);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var unsigned = new FriendResponsePacket(
                PacketUuid: Guid.NewGuid(),
                SenderAuthorId: session.Profile.AuthorId,
                RecipientAuthorId: peerAuthorId,
                SenderPublicKeySpkiBase64: session.Profile.PublicKeySpkiBase64,
                SenderDisplayName: session.Profile.Username,
                SenderNodeId: transport?.SelfId,
                Accepted: accepted,
                LogicalClock: clock,
                RespondedAtUtc: now,
                SignatureBase64: string.Empty);

            byte[] sig = _accountService.SignData(session, unsigned.BuildSignableBytes());
            signedPacket = unsigned with { SignatureBase64 = Convert.ToBase64String(sig) };

            updated = existing with
            {
                Status = accepted ? FriendStatus.Accepted : FriendStatus.Rejected,
                LogicalClock = clock,
                UpdatedAtUtc = now
            };
            state.Entries[peerAuthorId] = updated;
            targetNode = existing.LastKnownNodeId;
        }

        var wirePacket = new GroupWirePacket(
            GroupWirePacketKind.FriendResponse,
            StreamId: GroupMeshService.FriendStreamId,
            FriendResponse: signedPacket);

        await SendOrQueuePacketAsync(session, peerAuthorId, targetNode, wirePacket, ct).ConfigureAwait(false);
        await SaveToVaultAsync(session, ct).ConfigureAwait(false);
        SyncOrganicMeshAndContacts();
        FriendsChanged?.Invoke();
        FriendRequestsChanged?.Invoke();
        LogEmitted?.Invoke($"[Friend] {(accepted ? "Accepted" : "Rejected")} friend request from {updated.DisplayName}.");
        return updated;
    }

    public async Task RemoveFriendAsync(AuthorId peerAuthorId, CancellationToken ct = default)
    {
        AccountSession session;
        FriendGraphState state;
        FriendRemovePacket? signedPacket = null;
        NodeId? targetNode = null;
        string displayName = peerAuthorId.Value;

        lock (_sync)
        {
            session = _session ?? throw new InvalidOperationException("No active unlocked account session.");
            state = _state ?? throw new InvalidOperationException("Friend graph not loaded.");

            if (!state.Entries.TryGetValue(peerAuthorId, out FriendEntry? existing))
            {
                return;
            }

            displayName = existing.DisplayName;
            targetNode = existing.LastKnownNodeId;
            ulong clock = FriendGraphEngine.NextClock(state, peerAuthorId);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            var unsigned = new FriendRemovePacket(
                PacketUuid: Guid.NewGuid(),
                SenderAuthorId: session.Profile.AuthorId,
                RecipientAuthorId: peerAuthorId,
                SenderPublicKeySpkiBase64: session.Profile.PublicKeySpkiBase64,
                LogicalClock: clock,
                RemovedAtUtc: now,
                SignatureBase64: string.Empty);

            byte[] sig = _accountService.SignData(session, unsigned.BuildSignableBytes());
            signedPacket = unsigned with { SignatureBase64 = Convert.ToBase64String(sig) };

            state.Entries[peerAuthorId] = existing with
            {
                Status = FriendStatus.Removed,
                LogicalClock = clock,
                UpdatedAtUtc = now
            };
        }

        if (signedPacket is not null)
        {
            var wirePacket = new GroupWirePacket(
                GroupWirePacketKind.FriendRemove,
                StreamId: GroupMeshService.FriendStreamId,
                FriendRemove: signedPacket);

            await SendOrQueuePacketAsync(session, peerAuthorId, targetNode, wirePacket, ct).ConfigureAwait(false);
        }

        await SaveToVaultAsync(session, ct).ConfigureAwait(false);
        SyncOrganicMeshAndContacts();
        FriendsChanged?.Invoke();
        FriendRequestsChanged?.Invoke();
        LogEmitted?.Invoke($"[Friend] Removed {displayName} from friends.");
    }

    public async Task HandleIncomingFriendRequestAsync(FriendRequestPacket packet, CancellationToken ct = default)
    {
        AccountSession? session;
        FriendGraphState? state;
        bool autoAccepted;

        lock (_sync)
        {
            session = _session;
            state = _state;
            if (session is null || state is null)
            {
                return;
            }

            if (!FriendGraphEngine.TryApplyIncomingRequest(state, packet, _accountService.VerifySignature, out autoAccepted, out string? error))
            {
                LogEmitted?.Invoke($"[Friend] Ignored incoming FriendRequest: {error}");
                return;
            }
        }

        await SaveToVaultAsync(session, ct).ConfigureAwait(false);
        SyncOrganicMeshAndContacts();
        if (autoAccepted)
        {
            FriendsChanged?.Invoke();
        }
        FriendRequestsChanged?.Invoke();
        LogEmitted?.Invoke(autoAccepted
            ? $"[Friend] Mutual friend request with {packet.SenderDisplayName} auto-accepted."
            : $"[Friend] Received friend request from {packet.SenderDisplayName}.");
    }

    public async Task HandleIncomingFriendResponseAsync(FriendResponsePacket packet, CancellationToken ct = default)
    {
        AccountSession? session;
        FriendGraphState? state;

        lock (_sync)
        {
            session = _session;
            state = _state;
            if (session is null || state is null)
            {
                return;
            }

            if (!FriendGraphEngine.TryApplyIncomingResponse(state, packet, _accountService.VerifySignature, out string? error))
            {
                LogEmitted?.Invoke($"[Friend] Ignored incoming FriendResponse: {error}");
                return;
            }
        }

        await SaveToVaultAsync(session, ct).ConfigureAwait(false);
        SyncOrganicMeshAndContacts();
        FriendsChanged?.Invoke();
        FriendRequestsChanged?.Invoke();
        LogEmitted?.Invoke($"[Friend] {packet.SenderDisplayName} {(packet.Accepted ? "accepted" : "rejected")} your friend request.");
    }

    public async Task HandleIncomingFriendRemoveAsync(FriendRemovePacket packet, CancellationToken ct = default)
    {
        AccountSession? session;
        FriendGraphState? state;

        lock (_sync)
        {
            session = _session;
            state = _state;
            if (session is null || state is null)
            {
                return;
            }

            if (!FriendGraphEngine.TryApplyIncomingRemove(state, packet, _accountService.VerifySignature, out string? error))
            {
                LogEmitted?.Invoke($"[Friend] Ignored incoming FriendRemove: {error}");
                return;
            }
        }

        await SaveToVaultAsync(session, ct).ConfigureAwait(false);
        SyncOrganicMeshAndContacts();
        FriendsChanged?.Invoke();
        FriendRequestsChanged?.Invoke();
        LogEmitted?.Invoke($"[Friend] {packet.SenderAuthorId} removed friendship.");
    }

    public async Task FlushPendingOutboundAsync(NodeId? connectedNodeId = null, CancellationToken ct = default)
    {
        AccountSession? session;
        IMeshTransport? transport;
        List<PendingOutboundFriendPacket> snapshot;

        lock (_sync)
        {
            session = _session;
            transport = _transport;
            if (session is null || transport is null || _pendingOutbound.Count == 0)
            {
                return;
            }

            snapshot = _pendingOutbound
                .Where(p => connectedNodeId is null || p.TargetNodeId == connectedNodeId)
                .ToList();
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        var delivered = new HashSet<PendingOutboundFriendPacket>();
        foreach (PendingOutboundFriendPacket item in snapshot)
        {
            NodeId? target = item.TargetNodeId ?? ResolveKnownNodeId(item.RecipientAuthorId);
            if (target is not NodeId nodeId)
            {
                continue;
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(item.Packet);
            try
            {
                await transport.SendUniAsync(nodeId, bytes, ct: ct).ConfigureAwait(false);
                delivered.Add(item);
            }
            catch
            {
                // Peer still offline; keep queued
            }
        }

        if (delivered.Count > 0)
        {
            lock (_sync)
            {
                _pendingOutbound.RemoveAll(p => delivered.Contains(p));
            }
            await SaveToVaultAsync(session, ct).ConfigureAwait(false);
        }
    }

    private NodeId? ResolveKnownNodeId(AuthorId authorId)
    {
        lock (_sync)
        {
            if (_state is not null &&
                _state.Entries.TryGetValue(authorId, out FriendEntry? entry) &&
                entry.LastKnownNodeId is NodeId nid)
            {
                return nid;
            }
            return null;
        }
    }

    private async Task SendOrQueuePacketAsync(
        AccountSession session,
        AuthorId recipientAuthorId,
        NodeId? targetNodeId,
        GroupWirePacket packet,
        CancellationToken ct)
    {
        IMeshTransport? transport;
        lock (_sync)
        {
            transport = _transport;
        }

        bool sent = false;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet);

        // [TEST-ONLY] When recipient is a local account on this same machine/daemon (either SelfId or local account folder),
        // also drop the signed packet into test-loopback-friend-inbox so switching accounts in one GUI receives it.
        bool isLocalLoopbackTarget = transport is not null && targetNodeId == transport.SelfId;
        if (!isLocalLoopbackTarget && targetNodeId is null)
        {
            string? accountsDir = Directory.GetParent(session.AccountDirectory)?.FullName;
            isLocalLoopbackTarget = accountsDir is not null && Directory.Exists(Path.Combine(accountsDir, recipientAuthorId.Value));
        }

        if (isLocalLoopbackTarget)
        {
            string? inboxDir = GetTestLoopbackInboxDir(session, recipientAuthorId);
            if (!string.IsNullOrWhiteSpace(inboxDir))
            {
                Directory.CreateDirectory(inboxDir);
                string filePath = Path.Combine(inboxDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}.json");
                await File.WriteAllBytesAsync(filePath, bytes, ct).ConfigureAwait(false);
                LogEmitted?.Invoke($"[Friend] [TEST-ONLY] Same-daemon loopback: wrote signed packet for {recipientAuthorId} to test-loopback-friend-inbox.");
                sent = true;
            }
        }

        if (transport is not null && targetNodeId is NodeId nodeId)
        {
            try
            {
                await transport.SendUniAsync(nodeId, bytes, ct: ct).ConfigureAwait(false);
                sent = true;
            }
            catch
            {
                // Keep sent=true if [TEST-ONLY] loopback wrote it; otherwise queue for retry
            }
        }

        if (!sent)
        {
            lock (_sync)
            {
                _pendingOutbound.Add(new PendingOutboundFriendPacket(
                    recipientAuthorId,
                    targetNodeId,
                    packet,
                    DateTimeOffset.UtcNow));
            }
        }
    }

    private void SyncOrganicMeshAndContacts()
    {
        FriendGraphState? state;
        IMeshTransport? transport;
        GroupMeshService? meshService;
        DirectMessagingService? dmService;

        lock (_sync)
        {
            state = _state;
            transport = _transport;
            meshService = _meshService;
            dmService = _dmService;
        }

        if (state is null)
        {
            return;
        }

        IReadOnlyList<FriendEntry> friends = state.GetAcceptedFriends();
        if (dmService is not null)
        {
            foreach (FriendEntry f in friends)
            {
                dmService.RegisterContact(f.PeerAuthorId, f.PeerPublicKeySpkiBase64, f.LastKnownNodeId);
            }
        }

        if (meshService is not null && transport is not null)
        {
            meshService.RegisterOrganicFriendMesh(state.GetFriendEndpoints(transport.SelfId));
        }
    }

    private async Task SaveToVaultAsync(AccountSession session, CancellationToken ct)
    {
        PersistedFriendVault snapshot;
        lock (_sync)
        {
            FriendGraphState state = _state ?? new FriendGraphState(session.Profile.AuthorId);
            snapshot = new PersistedFriendVault(
                MaxLogicalClock: state.MaxLogicalClock,
                RestrictDmsToFriends: _restrictDmsToFriends,
                Entries: state.Entries.Values.ToList(),
                PendingOutbound: _pendingOutbound.ToList());
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        await _accountService.WriteVaultFileAsync(session, FriendVaultRelativePath, bytes, ct).ConfigureAwait(false);
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
