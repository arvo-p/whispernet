using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whispernet.Core;
using Whispernet.Transport.Iroh;

namespace Whispernet.App.Messaging;

/// <summary>
/// Service unifié de messagerie 1-on-1 avec accusé de réception obligatoire (ACK) :
/// 1) Transport direct QUIC (stream 0x03) si connexion active ou sonde hole-punching rapide,
///    avec reçus de livraison immédiats et indicateurs de frappe éphémères.
/// 2) Bascule automatique après 10s sans ACK (ou si destinataire injoignable) vers un bundle DTN
///    chiffré, estampillé d'un tag aveugle rotatif HMAC(pair_secret, epoch), remis aux ~3 meilleurs
///    pairs mutuels sélectionnés selon leur emploi du temps (jours ouvrés / week-ends) et PRoPHET.
/// 3) Déduplication transparente par UUID si les deux chemins réussissent.
/// </summary>
public sealed class DirectMessagingService : IDisposable
{
    public static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultEpochDuration = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultBundleTtl = TimeSpan.FromDays(7);
    public const int DefaultProbeTimeoutMs = 1500;
    public const int DefaultMaxRelayPeers = 3;

    private readonly IAccountService _accountService;
    private readonly AccountSession _session;
    private readonly IMeshTransport _transport;
    private readonly GroupMeshService _mesh;
    private readonly SqliteDirectMessageRepository _repo;
    private readonly Func<IEnumerable<GroupGovernanceState>> _governedGroupsProvider;

    private readonly ConcurrentDictionary<AuthorId, byte[]> _contactSpki = new();
    private readonly ConcurrentDictionary<AuthorId, NodeId> _contactNodes = new();
    private readonly ConcurrentDictionary<AuthorId, PeerActivityProfile> _activityProfiles = new();
    private readonly ConcurrentDictionary<(AuthorId Peer, AuthorId Target), (double P, DateTimeOffset Updated)> _prophet = new();
    private readonly ConcurrentDictionary<Guid, PendingAckState> _pendingAcks = new();
    private readonly ConcurrentDictionary<AuthorId, DateTimeOffset> _lastTypingSent = new();
    private readonly ConcurrentDictionary<NodeId, DateTimeOffset> _lastTagQuerySent = new();
    private readonly TimeSpan _ackTimeout;
    private readonly TimeSpan _epochDuration;
    private readonly int _probeTimeoutMs;
    private volatile bool _disposed;

    public event Action<DirectMessageRecord>? DirectMessageReceived;
    public event Action<DirectMessageRecord>? MessageQueued;
    public event Action<Guid, DirectMessageStatus, DateTimeOffset?>? MessageStatusChanged;
    public event Action<TypingIndicatorPacket>? TypingIndicatorReceived;
    public event Action<string>? LogEmitted;

    /// <summary>
    /// Optional policy predicate (e.g. "Restrict DMs to accepted friends only").
    /// When null or returning true, DMs are permitted.
    /// </summary>
    public Func<AuthorId, bool>? IsPeerAllowedForDm { get; set; }

    private sealed record PendingAckState(
        DirectMessageEnvelope Envelope,
        byte[] PairSecret,
        CancellationTokenSource Cts);

    public DirectMessagingService(
        IAccountService accountService,
        AccountSession session,
        IMeshTransport transport,
        GroupMeshService mesh,
        SqliteDirectMessageRepository repo,
        Func<IEnumerable<GroupGovernanceState>> governedGroupsProvider,
        TimeSpan? ackTimeout = null,
        TimeSpan? epochDuration = null,
        int probeTimeoutMs = DefaultProbeTimeoutMs)
    {
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _governedGroupsProvider = governedGroupsProvider ?? throw new ArgumentNullException(nameof(governedGroupsProvider));
        _ackTimeout = ackTimeout ?? DefaultAckTimeout;
        _epochDuration = epochDuration ?? DefaultEpochDuration;
        _probeTimeoutMs = probeTimeoutMs;

        _mesh.DirectMessageReceived += OnDirectMessageReceived;
        _mesh.DirectMessageAckReceived += OnDirectMessageAckReceived;
        _mesh.TypingIndicatorReceived += OnTypingIndicatorReceived;
        _mesh.DtnBundleReceived += OnDtnBundleReceived;
        _mesh.DtnTagQueryReceived += OnDtnTagQueryReceived;
    }

    public long GetEpoch(DateTimeOffset utc)
    {
        long epochSeconds = Math.Max(60L, (long)_epochDuration.TotalSeconds);
        return Math.DivRem(utc.ToUnixTimeSeconds(), epochSeconds, out _);
    }

    public static string ComputeBlindRoutingTagHex(ReadOnlySpan<byte> pairSecret, long epoch)
        => AccountCrypto.ComputeBlindRoutingTagHex(pairSecret, epoch);

    public async Task HydrateFromStorageAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PeerActivityProfile> profiles = await _repo.LoadActivityProfilesAsync(ct).ConfigureAwait(false);
        foreach (PeerActivityProfile p in profiles)
        {
            _activityProfiles[p.AuthorId] = p;
            if (p.LastKnownNodeId is NodeId nid && !string.IsNullOrWhiteSpace(nid.Value))
            {
                _contactNodes.TryAdd(p.AuthorId, nid);
            }
        }

        IReadOnlyList<(AuthorId PeerId, AuthorId TargetId, double PValue, DateTimeOffset UpdatedUtc)> prophetRows =
            await _repo.LoadProphetAsync(ct).ConfigureAwait(false);
        foreach ((AuthorId peer, AuthorId target, double pVal, DateTimeOffset updated) in prophetRows)
        {
            _prophet[(peer, target)] = (pVal, updated);
        }

        foreach (GroupGovernanceState gov in _governedGroupsProvider())
        {
            RegisterContactsFromGroup(gov, queryRelays: false);
        }

        _ = QueryAllKnownPeersForBlindTagsAsync();
    }

    public void RegisterContactsFromGroup(GroupGovernanceState govState, bool queryRelays = true)
    {
        ArgumentNullException.ThrowIfNull(govState);
        var discoveredNodes = new List<NodeId>();
        foreach ((AuthorId memberId, ModerationAction action) in govState.MemberStates)
        {
            if (memberId == _session.Profile.AuthorId)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(action.TargetPublicKeySpkiBase64))
            {
                try
                {
                    _contactSpki[memberId] = Convert.FromBase64String(action.TargetPublicKeySpkiBase64);
                }
                catch
                {
                }
            }

            if (action.TargetLastKnownNodeId is NodeId nid && !string.IsNullOrWhiteSpace(nid.Value))
            {
                _contactNodes[memberId] = nid;
                discoveredNodes.Add(nid);
            }
        }

        if (queryRelays && discoveredNodes.Count > 0)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (NodeId node in discoveredNodes.Distinct())
            {
                if (node != _transport.SelfId)
                {
                    _ = QueryPeerForBlindTaggedBundlesAsync(node, now, force: false);
                }
            }
        }
    }

    private async Task QueryAllKnownPeersForBlindTagsAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<NodeId> targets = _contactNodes.Values
            .Where(n => !string.IsNullOrWhiteSpace(n.Value) && n != _transport.SelfId)
            .Distinct()
            .ToList();

        foreach (NodeId target in targets)
        {
            await QueryPeerForBlindTaggedBundlesAsync(target, now, force: false).ConfigureAwait(false);
        }
    }

    public void RegisterContact(AuthorId authorId, string publicKeySpkiBase64, NodeId? nodeId = null)
    {
        if (!authorId.IsValid || authorId == _session.Profile.AuthorId)
        {
            return;
        }

        _contactSpki[authorId] = Convert.FromBase64String(publicKeySpkiBase64);
        if (nodeId is NodeId nid && !string.IsNullOrWhiteSpace(nid.Value))
        {
            _contactNodes[authorId] = nid;
        }
    }

    /// <summary>
    /// Enregistre une observation d'activité d'un pair (profil horaire ouvré/week-end + PRoPHET)
    /// et interroge ce pair par tags aveugles pour récupérer d'éventuels bundles DTN en attente.
    /// </summary>
    public void RecordPeerActivity(AuthorId? authorId, NodeId? nodeId, DateTimeOffset? observedAt = null)
    {
        DateTimeOffset now = observedAt ?? DateTimeOffset.UtcNow;

        if (authorId is AuthorId aid && aid.IsValid && aid != _session.Profile.AuthorId)
        {
            if (nodeId is NodeId nid && !string.IsNullOrWhiteSpace(nid.Value))
            {
                _contactNodes[aid] = nid;
            }

            PeerActivityProfile profile = _activityProfiles.GetOrAdd(aid, id => new PeerActivityProfile(id, nodeId));
            lock (profile)
            {
                PeerActivityTimetable.RecordObservation(profile, now, nodeId);
            }
            _ = _repo.UpsertActivityProfileAsync(profile);

            var key = (_session.Profile.AuthorId, aid);
            double prevP = _prophet.TryGetValue(key, out var existing) ? existing.P : 0.0;
            DateTimeOffset prevUpdated = existing.Updated == default ? now : existing.Updated;
            double nextP = ProphetRoutingEngine.UpdateEncounter(prevP, prevUpdated, now);
            _prophet[key] = (nextP, now);
            _ = _repo.UpsertProphetAsync(_session.Profile.AuthorId, aid, nextP, now);
        }

        if (nodeId is NodeId targetNode && targetNode != _transport.SelfId)
        {
            _ = QueryPeerForBlindTaggedBundlesAsync(targetNode, now, force: false);
        }
    }

    public IReadOnlyDictionary<AuthorId, PeerActivityProfile> GetActivityProfilesSnapshot()
        => new Dictionary<AuthorId, PeerActivityProfile>(_activityProfiles);

    public async Task<DirectMessageRecord> SendDirectMessageAsync(
        AuthorId recipientAuthorId,
        string content,
        string? recipientPublicKeySpkiBase64 = null,
        NodeId? recipientNodeId = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        if (IsPeerAllowedForDm is not null && !IsPeerAllowedForDm(recipientAuthorId))
        {
            throw new InvalidOperationException("Direct messages are restricted to accepted friends in your settings.");
        }

        if (!string.IsNullOrWhiteSpace(recipientPublicKeySpkiBase64))
        {
            RegisterContact(recipientAuthorId, recipientPublicKeySpkiBase64, recipientNodeId);
        }

        if (!_contactSpki.TryGetValue(recipientAuthorId, out byte[]? recipientSpki))
        {
            throw new InvalidOperationException($"Missing SPKI public key for recipient {recipientAuthorId}.");
        }

        NodeId? targetNode = recipientNodeId ?? (_contactNodes.TryGetValue(recipientAuthorId, out NodeId knownNode) ? knownNode : null);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long epoch = GetEpoch(now);
        Guid messageUuid = Guid.NewGuid();

        byte[] pairSecret = _accountService.DerivePairSecret(_session, recipientSpki);
        byte[] msgKey = AccountCrypto.DeriveDirectMessageKey(pairSecret);
        byte[] encryptedPayload;
        try
        {
            byte[] aad = BuildMessageAad(messageUuid, epoch);
            encryptedPayload = AccountCrypto.EncryptAesGcm(msgKey, Encoding.UTF8.GetBytes(content), aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(msgKey);
        }

        var unsignedEnv = new DirectMessageEnvelope(
            MessageUuid: messageUuid,
            SenderAuthorId: _session.Profile.AuthorId,
            RecipientAuthorId: recipientAuthorId,
            SenderPublicKeySpkiBase64: _session.Profile.PublicKeySpkiBase64,
            SenderNodeId: _transport.SelfId,
            SentAtUtc: now,
            EncryptedPayload: encryptedPayload,
            Epoch: epoch,
            SignatureBase64: string.Empty);

        byte[] sigBytes = _accountService.SignData(_session, BuildEnvelopeSignableBytes(unsignedEnv));
        DirectMessageEnvelope signedEnv = unsignedEnv with { SignatureBase64 = Convert.ToBase64String(sigBytes) };

        var record = new DirectMessageRecord(
            messageUuid,
            recipientAuthorId,
            IsOutgoing: true,
            content,
            DirectMessageStatus.Sending,
            now);

        await _repo.TryInsertMessageAsync(record, ct).ConfigureAwait(false);
        MessageQueued?.Invoke(record);
        MessageStatusChanged?.Invoke(messageUuid, DirectMessageStatus.Sending, null);

        if (targetNode is NodeId liveNode && !string.IsNullOrWhiteSpace(liveNode.Value))
        {
            // [TEST-ONLY] Same-daemon loopback for multi-account testing on one machine/daemon
            if (liveNode == _transport.SelfId)
            {
                await WriteTestLoopbackDmAsync(recipientAuthorId, signedEnv, ct).ConfigureAwait(false);
                DateTimeOffset deliveredAt = DateTimeOffset.UtcNow;
                await _repo.UpdateMessageStatusAsync(messageUuid, DirectMessageStatus.Delivered, deliveredAt, ct).ConfigureAwait(false);
                MessageStatusChanged?.Invoke(messageUuid, DirectMessageStatus.Delivered, deliveredAt);
                CryptographicOperations.ZeroMemory(pairSecret);
                return record with { Status = DirectMessageStatus.Delivered, DeliveredAtUtc = deliveredAt };
            }

            var cts = new CancellationTokenSource();
            var pending = new PendingAckState(signedEnv, (byte[])pairSecret.Clone(), cts);
            _pendingAcks[messageUuid] = pending;

            var packet = new GroupWirePacket(
                GroupWirePacketKind.DirectMessage,
                DirectMessage: signedEnv,
                StreamId: GroupMeshService.DirectMessageStreamId);

            // Lancer immédiatement le minuteur de bascule PRoPHET DTN à 10 secondes
            _ = WaitForAckOrFallbackToProphetAsync(messageUuid, pending);

            // Envoyer en parallèle sur QUIC (Iroh gère discovery + hole-punching en arrière-plan)
            _ = Task.Run(async () =>
            {
                try
                {
                    using var sendTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                    sendTimeoutCts.CancelAfter(_ackTimeout);
                    await _mesh.SendDirectPacketAsync(liveNode, packet, sendTimeoutCts.Token).ConfigureAwait(false);
                    LogEmitted?.Invoke($"[DM] Sent direct QUIC message {messageUuid:D} to {recipientAuthorId.Value[..8]}.. (awaiting ACK <= {_ackTimeout.TotalSeconds:0}s).");
                }
                catch (Exception ex)
                {
                    if (_pendingAcks.TryRemove(messageUuid, out PendingAckState? removed))
                    {
                        LogEmitted?.Invoke($"[DM] Direct QUIC send failed for {messageUuid:D}: {ex.Message} -> falling back to DTN.");
                        removed.Cts.Cancel();
                        try
                        {
                            await WrapAndDispatchDtnBundleAsync(removed.Envelope, removed.PairSecret, CancellationToken.None).ConfigureAwait(false);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(removed.PairSecret);
                            removed.Cts.Dispose();
                        }
                    }
                }
            }, CancellationToken.None);

            CryptographicOperations.ZeroMemory(pairSecret);
            return record;
        }

        // [TEST-ONLY] Same-machine loopback when recipient is another local account without a known live NodeId
        string? accountsDir = Directory.GetParent(_session.AccountDirectory)?.FullName;
        if (accountsDir is not null && Directory.Exists(Path.Combine(accountsDir, recipientAuthorId.Value)))
        {
            await WriteTestLoopbackDmAsync(recipientAuthorId, signedEnv, ct).ConfigureAwait(false);
            DateTimeOffset deliveredAt = DateTimeOffset.UtcNow;
            await _repo.UpdateMessageStatusAsync(messageUuid, DirectMessageStatus.Delivered, deliveredAt, ct).ConfigureAwait(false);
            MessageStatusChanged?.Invoke(messageUuid, DirectMessageStatus.Delivered, deliveredAt);
            CryptographicOperations.ZeroMemory(pairSecret);
            return record with { Status = DirectMessageStatus.Delivered, DeliveredAtUtc = deliveredAt };
        }

        // Destinataire injoignable -> bascule immédiate vers le relais DTN aveugle
        await WrapAndDispatchDtnBundleAsync(signedEnv, pairSecret, ct).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(pairSecret);
        return record with { Status = DirectMessageStatus.QueuedViaRelay };
    }

    // [TEST-ONLY] Local filesystem mailbox for same-daemon multi-account DM testing.
    private static string? GetTestLoopbackDmInboxDir(AccountSession session, AuthorId targetAuthorId)
    {
        string? accountsDir = Directory.GetParent(session.AccountDirectory)?.FullName;
        string? rootDir = accountsDir is not null ? Directory.GetParent(accountsDir)?.FullName : null;
        if (string.IsNullOrWhiteSpace(rootDir))
        {
            return null;
        }

        return Path.Combine(rootDir, "test-loopback-dm-inbox", targetAuthorId.Value);
    }

    private async Task WriteTestLoopbackDmAsync(AuthorId recipientAuthorId, DirectMessageEnvelope env, CancellationToken ct)
    {
        string? inboxDir = GetTestLoopbackDmInboxDir(_session, recipientAuthorId);
        if (string.IsNullOrWhiteSpace(inboxDir))
        {
            return;
        }

        Directory.CreateDirectory(inboxDir);
        string filePath = Path.Combine(inboxDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{env.MessageUuid:N}.json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(env);
        await File.WriteAllBytesAsync(filePath, bytes, ct).ConfigureAwait(false);
        LogEmitted?.Invoke($"[DM] [TEST-ONLY] Same-daemon loopback: wrote signed DM {env.MessageUuid:D} for {recipientAuthorId} to test-loopback-dm-inbox.");
    }

    public async Task ConsumeTestLoopbackInboxAsync()
    {
        string? inboxDir = GetTestLoopbackDmInboxDir(_session, _session.Profile.AuthorId);
        if (string.IsNullOrWhiteSpace(inboxDir) || !Directory.Exists(inboxDir))
        {
            return;
        }

        string[] files = Directory.GetFiles(inboxDir, "*.json").OrderBy(static f => f, StringComparer.Ordinal).ToArray();
        int consumed = 0;
        foreach (string file in files)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                DirectMessageEnvelope? env = JsonSerializer.Deserialize<DirectMessageEnvelope>(bytes);
                if (env is not null)
                {
                    await ProcessIncomingEnvelopeAsync(env).ConfigureAwait(false);
                    consumed++;
                }
            }
            catch
            {
                // Ignore malformed test loopback DM
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        if (consumed > 0)
        {
            LogEmitted?.Invoke($"[DM] [TEST-ONLY] Consumed {consumed} same-daemon loopback DM(s) from test-loopback-dm-inbox.");
        }
    }

    /// <summary>
    /// Envoie un indicateur de frappe éphémère uniquement si une connexion QUIC active existe avec le destinataire.
    /// </summary>
    public async Task SendTypingIndicatorAsync(AuthorId recipientAuthorId, bool isTyping, CancellationToken ct = default)
    {
        if (_disposed || !_contactNodes.TryGetValue(recipientAuthorId, out NodeId targetNode))
        {
            return;
        }

        if (!_transport.HasActiveConnection(targetNode))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (isTyping && _lastTypingSent.TryGetValue(recipientAuthorId, out DateTimeOffset prev) &&
            (now - prev) < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastTypingSent[recipientAuthorId] = now;
        var typing = new TypingIndicatorPacket(_session.Profile.AuthorId, recipientAuthorId, isTyping, now);
        var packet = new GroupWirePacket(
            GroupWirePacketKind.TypingIndicator,
            TypingIndicator: typing,
            StreamId: GroupMeshService.DirectMessageStreamId);

        try
        {
            await _mesh.SendDirectPacketAsync(targetNode, packet, ct).ConfigureAwait(false);
        }
        catch
        {
            // Éphémère : ignoré en cas d'erreur
        }
    }

    private async Task WaitForAckOrFallbackToProphetAsync(Guid messageUuid, PendingAckState pending)
    {
        try
        {
            await Task.Delay(_ackTimeout, pending.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // ACK reçu dans les temps
        }

        if (_pendingAcks.TryRemove(messageUuid, out _))
        {
            LogEmitted?.Invoke($"[DM] 10s ACK timeout expired for {messageUuid:D} -> wrapping exact same UUID into PRoPHET DTN bundle.");
            try
            {
                await WrapAndDispatchDtnBundleAsync(pending.Envelope, pending.PairSecret, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pending.PairSecret);
                pending.Cts.Dispose();
            }
        }
    }

    private async Task WrapAndDispatchDtnBundleAsync(
        DirectMessageEnvelope envelope,
        byte[] pairSecret,
        CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long epoch = envelope.Epoch;
        string blindTag = AccountCrypto.ComputeBlindRoutingTagHex(pairSecret, epoch);

        byte[] envJson = JsonSerializer.SerializeToUtf8Bytes(envelope);
        byte[] bundleKey = AccountCrypto.DeriveDirectMessageKey(pairSecret);
        byte[] encryptedBundleBytes;
        try
        {
            byte[] aad = BuildBundleAad(envelope.MessageUuid, blindTag, epoch);
            encryptedBundleBytes = AccountCrypto.EncryptAesGcm(bundleKey, envJson, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundleKey);
        }

        var bundle = new DtnBundle(
            BundleId: Guid.NewGuid(),
            MessageUuid: envelope.MessageUuid,
            BlindRoutingTagHex: blindTag,
            Epoch: epoch,
            CreatedAtUtc: now,
            ExpiresAtUtc: now + DefaultBundleTtl,
            HopCount: 0,
            MaxHops: 4,
            EncryptedEnvelopeBytes: encryptedBundleBytes);

        // Stocker localement aussi pour re-diffusion opportuniste
        await _repo.UpsertDtnBundleAsync(bundle, ct).ConfigureAwait(false);

        // Sélectionner les ~3 meilleurs pairs mutuels depuis l'emploi du temps & PRoPHET
        var prophetScoresToRecipient = new Dictionary<AuthorId, double>();
        foreach (((AuthorId peer, AuthorId target), (double pVal, DateTimeOffset updated)) in _prophet)
        {
            if (target == envelope.RecipientAuthorId)
            {
                prophetScoresToRecipient[peer] = ProphetRoutingEngine.AgePredictability(pVal, now - updated);
            }
        }

        IReadOnlyList<RelayCandidateScore> relays = ProphetRoutingEngine.SelectTopRelays(
            _session.Profile.AuthorId,
            envelope.RecipientAuthorId,
            _governedGroupsProvider(),
            _activityProfiles,
            prophetScoresToRecipient,
            now,
            DefaultMaxRelayPeers);

        var packet = new GroupWirePacket(
            GroupWirePacketKind.DtnBundle,
            DtnBundle: bundle,
            StreamId: GroupMeshService.DirectMessageStreamId);

        int handedCount = 0;
        foreach (RelayCandidateScore relay in relays)
        {
            try
            {
                await _mesh.SendDirectPacketAsync(relay.NodeId, packet, ct).ConfigureAwait(false);
                handedCount++;
                LogEmitted?.Invoke(
                    $"[DTN] Handed blind bundle {bundle.BundleId.ToString("D")[..8]} (tag={blindTag[..8]}..) to relay {relay.AuthorId.Value[..8]} (timetable={relay.TimetableScore:F2}, prophet={relay.ProphetPredictability:F2}).");
            }
            catch
            {
            }
        }

        await _repo.UpdateMessageStatusAsync(envelope.MessageUuid, DirectMessageStatus.QueuedViaRelay, ct: ct).ConfigureAwait(false);
        MessageStatusChanged?.Invoke(envelope.MessageUuid, DirectMessageStatus.QueuedViaRelay, null);
        LogEmitted?.Invoke($"[DM] Message {envelope.MessageUuid:D} queued via {handedCount} relay(s) [Queued via relay].");
    }

    private void OnDirectMessageReceived(DirectMessageEnvelope env)
    {
        if (_disposed || env.RecipientAuthorId != _session.Profile.AuthorId)
        {
            return;
        }

        _ = ProcessIncomingEnvelopeAsync(env);
    }

    private async Task ProcessIncomingEnvelopeAsync(DirectMessageEnvelope env)
    {
        try
        {
            byte[] senderSpki = Convert.FromBase64String(env.SenderPublicKeySpkiBase64);
            if (AccountCrypto.ComputeAuthorId(senderSpki) != env.SenderAuthorId)
            {
                return;
            }

            if (!_accountService.VerifySignature(senderSpki, BuildEnvelopeSignableBytes(env), Convert.FromBase64String(env.SignatureBase64)))
            {
                LogEmitted?.Invoke($"[DM] Rejected DirectMessage {env.MessageUuid:D}: invalid signature.");
                return;
            }

            if (IsPeerAllowedForDm is not null && !IsPeerAllowedForDm(env.SenderAuthorId))
            {
                LogEmitted?.Invoke($"[DM] Ignored DirectMessage {env.MessageUuid:D} from non-friend {env.SenderAuthorId} (friends-only DM restriction active).");
                return;
            }

            RegisterContact(env.SenderAuthorId, env.SenderPublicKeySpkiBase64, env.SenderNodeId);
            RecordPeerActivity(env.SenderAuthorId, env.SenderNodeId, DateTimeOffset.UtcNow);

            byte[] pairSecret = _accountService.DerivePairSecret(_session, senderSpki);
            byte[] msgKey = AccountCrypto.DeriveDirectMessageKey(pairSecret);
            string plaintext;
            try
            {
                byte[] aad = BuildMessageAad(env.MessageUuid, env.Epoch);
                byte[] plainBytes = AccountCrypto.DecryptAesGcm(msgKey, env.EncryptedPayload, aad);
                plaintext = Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(msgKey);
                CryptographicOperations.ZeroMemory(pairSecret);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var record = new DirectMessageRecord(
                env.MessageUuid,
                env.SenderAuthorId,
                IsOutgoing: false,
                plaintext,
                DirectMessageStatus.Delivered,
                env.SentAtUtc,
                now);

            // Déduplication transparente par UUID : TryInsertMessageAsync renvoie false si déjà reçu
            bool isNew = await _repo.TryInsertMessageAsync(record).ConfigureAwait(false);
            if (isNew)
            {
                DirectMessageReceived?.Invoke(record);
                LogEmitted?.Invoke($"[DM] Received & verified 1-on-1 message {env.MessageUuid:D} from {env.SenderAuthorId.Value[..8]}..");
            }
            else
            {
                LogEmitted?.Invoke($"[DM] Deduplicated duplicate message UUID {env.MessageUuid:D} from {env.SenderAuthorId.Value[..8]}..");
            }

            await SendAckAsync(env, now).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke($"[DM] Failed processing incoming message {env.MessageUuid:D}: {ex.Message}");
        }
    }

    private async Task SendAckAsync(DirectMessageEnvelope original, DateTimeOffset receivedAtUtc)
    {
        var unsignedAck = new DirectMessageAck(
            MessageUuid: original.MessageUuid,
            SenderAuthorId: _session.Profile.AuthorId,
            RecipientAuthorId: original.SenderAuthorId,
            SenderPublicKeySpkiBase64: _session.Profile.PublicKeySpkiBase64,
            ReceivedAtUtc: receivedAtUtc,
            SignatureBase64: string.Empty);

        byte[] sig = _accountService.SignData(_session, BuildAckSignableBytes(unsignedAck));
        DirectMessageAck signedAck = unsignedAck with { SignatureBase64 = Convert.ToBase64String(sig) };

        NodeId? targetNode = original.SenderNodeId ?? (_contactNodes.TryGetValue(original.SenderAuthorId, out NodeId known) ? known : null);
        if (targetNode is NodeId nid && !string.IsNullOrWhiteSpace(nid.Value))
        {
            try
            {
                var packet = new GroupWirePacket(
                    GroupWirePacketKind.DirectMessageAck,
                    DirectMessageAck: signedAck,
                    StreamId: GroupMeshService.DirectMessageStreamId);
                await _mesh.SendDirectPacketAsync(nid, packet).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort live ACK
            }
        }
    }

    private void OnDirectMessageAckReceived(DirectMessageAck ack)
    {
        if (_disposed || ack.RecipientAuthorId != _session.Profile.AuthorId)
        {
            return;
        }

        try
        {
            byte[] senderSpki = Convert.FromBase64String(ack.SenderPublicKeySpkiBase64);
            if (AccountCrypto.ComputeAuthorId(senderSpki) != ack.SenderAuthorId)
            {
                return;
            }

            if (!_accountService.VerifySignature(senderSpki, BuildAckSignableBytes(ack), Convert.FromBase64String(ack.SignatureBase64)))
            {
                return;
            }

            if (_pendingAcks.TryRemove(ack.MessageUuid, out PendingAckState? pending))
            {
                pending.Cts.Cancel();
                CryptographicOperations.ZeroMemory(pending.PairSecret);
                pending.Cts.Dispose();
            }

            _ = _repo.UpdateMessageStatusAsync(ack.MessageUuid, DirectMessageStatus.Delivered, ack.ReceivedAtUtc);
            _ = _repo.DeleteDtnBundleByMessageUuidAsync(ack.MessageUuid);
            MessageStatusChanged?.Invoke(ack.MessageUuid, DirectMessageStatus.Delivered, ack.ReceivedAtUtc);
            LogEmitted?.Invoke($"[DM] Received verified ACK for {ack.MessageUuid:D} from {ack.SenderAuthorId.Value[..8]}..");
        }
        catch
        {
        }
    }

    private void OnTypingIndicatorReceived(TypingIndicatorPacket typing)
    {
        if (_disposed || typing.RecipientAuthorId != _session.Profile.AuthorId)
        {
            return;
        }

        TypingIndicatorReceived?.Invoke(typing);
    }

    private void OnDtnBundleReceived(DtnBundle bundle)
    {
        if (_disposed || bundle.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return;
        }

        _ = ProcessIncomingDtnBundleAsync(bundle);
    }

    private async Task ProcessIncomingDtnBundleAsync(DtnBundle bundle)
    {
        // Tenter de déchiffrer si le tag aveugle correspond à l'un de nos contacts connus (epoch -1..+1)
        foreach ((AuthorId contactId, byte[] contactSpki) in _contactSpki)
        {
            byte[] pairSecret = _accountService.DerivePairSecret(_session, contactSpki);
            try
            {
                bool tagMatched = false;
                for (long e = bundle.Epoch - 1; e <= bundle.Epoch + 1; e++)
                {
                    if (AccountCrypto.ComputeBlindRoutingTagHex(pairSecret, e)
                        .Equals(bundle.BlindRoutingTagHex, StringComparison.OrdinalIgnoreCase))
                    {
                        tagMatched = true;
                        break;
                    }
                }

                if (!tagMatched)
                {
                    continue;
                }

                byte[] bundleKey = AccountCrypto.DeriveDirectMessageKey(pairSecret);
                try
                {
                    byte[] aad = BuildBundleAad(bundle.MessageUuid, bundle.BlindRoutingTagHex, bundle.Epoch);
                    byte[] envBytes = AccountCrypto.DecryptAesGcm(bundleKey, bundle.EncryptedEnvelopeBytes, aad);
                    DirectMessageEnvelope? env = JsonSerializer.Deserialize<DirectMessageEnvelope>(envBytes);
                    if (env is not null && env.MessageUuid == bundle.MessageUuid)
                    {
                        await ProcessIncomingEnvelopeAsync(env).ConfigureAwait(false);
                        await _repo.DeleteDtnBundleByMessageUuidAsync(bundle.MessageUuid).ConfigureAwait(false);
                        return;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bundleKey);
                }
            }
            catch
            {
                // Pas destiné à ce contact ou tag collision improbable
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pairSecret);
            }
        }

        // Sinon, agir en tant que relais aveugle : stocker le bundle si HopCount < MaxHops
        if (bundle.HopCount < bundle.MaxHops)
        {
            await _repo.UpsertDtnBundleAsync(bundle with { HopCount = bundle.HopCount + 1 }).ConfigureAwait(false);
            LogEmitted?.Invoke($"[DTN-RELAY] Stored blind bundle {bundle.BundleId.ToString("D")[..8]} (tag={bundle.BlindRoutingTagHex[..8]}.., ttl={bundle.ExpiresAtUtc:u}).");
        }
    }

    private void OnDtnTagQueryReceived(DtnTagQuery query)
    {
        if (_disposed || query.BlindTagsHex.Count == 0)
        {
            return;
        }

        _ = RespondToTagQueryAsync(query);
    }

    private async Task RespondToTagQueryAsync(DtnTagQuery query)
    {
        try
        {
            IReadOnlyList<DtnBundle> matches = await _repo
                .QueryDtnBundlesByBlindTagsAsync(query.BlindTagsHex, DateTimeOffset.UtcNow)
                .ConfigureAwait(false);

            foreach (DtnBundle bundle in matches)
            {
                var packet = new GroupWirePacket(
                    GroupWirePacketKind.DtnBundle,
                    DtnBundle: bundle,
                    StreamId: GroupMeshService.DirectMessageStreamId);
                await _mesh.SendDirectPacketAsync(query.RequesterNodeId, packet).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    public async Task QueryPeerForBlindTaggedBundlesAsync(NodeId peerNode, DateTimeOffset nowUtc, bool force = true)
    {
        if (_disposed || _contactSpki.IsEmpty || peerNode == _transport.SelfId)
        {
            return;
        }

        if (!force &&
            _lastTagQuerySent.TryGetValue(peerNode, out DateTimeOffset prevQuery) &&
            (nowUtc - prevQuery) < TimeSpan.FromSeconds(15))
        {
            return;
        }
        _lastTagQuerySent[peerNode] = nowUtc;

        IReadOnlyList<string> tags = ComputeExpectedBlindTags(nowUtc);
        if (tags.Count == 0)
        {
            return;
        }

        var query = new DtnTagQuery(_transport.SelfId, tags);
        var packet = new GroupWirePacket(
            GroupWirePacketKind.DtnTagQuery,
            DtnTagQuery: query,
            StreamId: GroupMeshService.DirectMessageStreamId);

        try
        {
            await _mesh.SendDirectPacketAsync(peerNode, packet).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public IReadOnlyList<string> ComputeExpectedBlindTags(DateTimeOffset nowUtc)
    {
        if (_contactSpki.IsEmpty)
        {
            return Array.Empty<string>();
        }

        long currentEpoch = GetEpoch(nowUtc);
        var tags = new List<string>();
        foreach ((_, byte[] spki) in _contactSpki)
        {
            byte[] pairSecret = _accountService.DerivePairSecret(_session, spki);
            try
            {
                for (long e = currentEpoch - 1; e <= currentEpoch + 1; e++)
                {
                    tags.Add(AccountCrypto.ComputeBlindRoutingTagHex(pairSecret, e));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pairSecret);
            }
        }
        return tags;
    }

    private static byte[] BuildMessageAad(Guid messageUuid, long epoch)
    {
        byte[] aad = new byte[16 + 8];
        messageUuid.TryWriteBytes(aad.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(16, 8), epoch);
        return aad;
    }

    private static byte[] BuildBundleAad(Guid messageUuid, string blindTagHex, long epoch)
    {
        byte[] tagBytes = Encoding.UTF8.GetBytes(blindTagHex);
        byte[] aad = new byte[16 + 8 + tagBytes.Length];
        messageUuid.TryWriteBytes(aad.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(16, 8), epoch);
        tagBytes.CopyTo(aad.AsSpan(24));
        return aad;
    }

    private static byte[] BuildEnvelopeSignableBytes(DirectMessageEnvelope env)
    {
        string header = $"{env.MessageUuid:D}|{env.SenderAuthorId.Value}|{env.RecipientAuthorId.Value}|{env.SentAtUtc.ToUnixTimeMilliseconds()}|{env.Epoch}|";
        byte[] headerBytes = Encoding.UTF8.GetBytes(header);
        byte[] result = new byte[headerBytes.Length + env.EncryptedPayload.Length];
        headerBytes.CopyTo(result, 0);
        env.EncryptedPayload.CopyTo(result, headerBytes.Length);
        return result;
    }

    private static byte[] BuildAckSignableBytes(DirectMessageAck ack)
    {
        string text = $"ACK|{ack.MessageUuid:D}|{ack.SenderAuthorId.Value}|{ack.RecipientAuthorId.Value}|{ack.ReceivedAtUtc.ToUnixTimeMilliseconds()}";
        return Encoding.UTF8.GetBytes(text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mesh.DirectMessageReceived -= OnDirectMessageReceived;
        _mesh.DirectMessageAckReceived -= OnDirectMessageAckReceived;
        _mesh.TypingIndicatorReceived -= OnTypingIndicatorReceived;
        _mesh.DtnBundleReceived -= OnDtnBundleReceived;
        _mesh.DtnTagQueryReceived -= OnDtnTagQueryReceived;

        foreach ((_, PendingAckState pending) in _pendingAcks)
        {
            pending.Cts.Cancel();
            CryptographicOperations.ZeroMemory(pending.PairSecret);
            pending.Cts.Dispose();
        }
        _pendingAcks.Clear();
    }
}
