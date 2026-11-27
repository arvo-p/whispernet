using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Données contenues dans un lien d'invitation <c>whispernet://invite/...</c>.
/// </summary>
public sealed record GroupInvitationData(
    GroupId GroupId,
    NodeId BootstrapNodeId,
    string Title,
    bool IsPasswordProtected,
    InvitePreApprovalTicket? PreApprovalTicket = null);

/// <summary>
/// Données persistées d'un groupe dans le coffre chiffré du compte (<c>vault/groups/&lt;groupId&gt;.json</c>).
/// </summary>
internal sealed record PersistedGroupVaultEntry(
    GroupId GroupId,
    string? OwnerPrivateKeyPkcs8Base64,
    GroupManifest CurrentManifest,
    List<ModerationAction> MemberActions,
    Dictionary<AuthorId, NodeId>? KnownAuthorNodes = null);

/// <summary>
/// Service applicatif gérant la création de groupes (paire ECDSA P-256 dédiée par groupe),
/// la persistance chiffrée dans le coffre, la vérification des mots de passe d'admission,
/// la file des demandes en attente et la signature des actions de modération.
/// </summary>
public sealed class GroupGovernanceService
{
    private const int JoinKdfIterations = 600_000;
    private const int JoinSaltSizeBytes = 16;
    private const int JoinVerifierSizeBytes = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IAccountService _accountService;
    private readonly ConcurrentDictionary<GroupId, ConcurrentDictionary<AuthorId, JoinApprovalRequest>> _pendingRequests = new();

    public event Action<GroupId>? PendingRequestsChanged;
    public event Action<GroupId>? GovernanceStateChanged;

    public GroupGovernanceService(IAccountService accountService){
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
    }

    /// <summary>
    /// Crée un nouveau groupe avec sa propre paire ECDSA P-256 dédiée (seul l'administrateur en possède la clé privée),
    /// admet le créateur et persiste l'état dans le coffre chiffré du compte.
    /// </summary>
    public async Task<GroupGovernanceState> CreateGroupAsync(
        AccountSession ownerSession,
        string title,
        string? joinPassword = null,
        TimeSpan? ephemeralMessageTtl = null,
        NodeId? creatorNodeId = null,
        CancellationToken cancellationToken = default){

        ArgumentNullException.ThrowIfNull(ownerSession);
        if (string.IsNullOrWhiteSpace(title)){
            throw new ArgumentException("Group title cannot be empty.", nameof(title));
        }

        using var groupAuthorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] groupPubKeySpki = groupAuthorityKey.ExportSubjectPublicKeyInfo();
        byte[] groupPrivKeyPkcs8 = groupAuthorityKey.ExportPkcs8PrivateKey();

        try{
            GroupId groupId = GroupId.FromOwnerPublicKeySpki(groupPubKeySpki);
            bool hasPassword = !string.IsNullOrEmpty(joinPassword);
            string? saltBase64 = null;
            string? verifierBase64 = null;

            if (hasPassword){
                byte[] salt = RandomNumberGenerator.GetBytes(JoinSaltSizeBytes);
                byte[] verifier = DerivePasswordVerifier(joinPassword!, salt);
                saltBase64 = Convert.ToBase64String(salt);
                verifierBase64 = Convert.ToBase64String(verifier);
            }

            // Inscrire l'AuthorId personnel du créateur comme modérateur initial
            // tout en gardant GroupId indépendant de sa clé personnelle.
            var initialModerators = new Dictionary<AuthorId, string>{
                [ownerSession.Profile.AuthorId] = ownerSession.Profile.PublicKeySpkiBase64
            };

            var unsignedManifest = new GroupManifest(
                GroupId: groupId,
                OwnerPublicKeySpkiBase64: Convert.ToBase64String(groupPubKeySpki),
                Version: 1,
                Title: title.Trim(),
                IsPasswordProtected: hasPassword,
                JoinPasswordSaltBase64: saltBase64,
                JoinPasswordVerifierBase64: verifierBase64,
                EphemeralMessageTtl: ephemeralMessageTtl,
                IsTerminated: false,
                ModeratorPublicKeysSpkiBase64: initialModerators,
                IssuedAtUtc: DateTimeOffset.UtcNow,
                OwnerSignatureBase64: string.Empty);

            byte[] manifestSig = groupAuthorityKey.SignData(unsignedManifest.BuildSignableBytes(), HashAlgorithmName.SHA256);
            GroupManifest signedManifest = unsignedManifest with
            {
                OwnerSignatureBase64 = Convert.ToBase64String(manifestSig)
            };

            var state = new GroupGovernanceState(signedManifest);

            // Admettre le créateur dans le registre CRDT initial
            ModerationAction admitCreator = SignModerationAction(
                ownerSession,
                state,
                ownerSession.Profile.AuthorId,
                ownerSession.Profile.PublicKeySpkiBase64,
                MemberStatus.Admitted,
                "Group creator",
                creatorNodeId);

            if (!GroupGovernanceEngine.TryApplyModerationAction(
                    state,
                    admitCreator,
                    _accountService.VerifySignature,
                    out _,
                    out string? error))
            {
                throw new InvalidOperationException($"Failed to admit group creator: {error}");
            }

            await SaveGroupToVaultAsync(
                ownerSession,
                state,
                Convert.ToBase64String(groupPrivKeyPkcs8),
                cancellationToken).ConfigureAwait(false);

            GovernanceStateChanged?.Invoke(groupId);
            return state;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(groupPrivKeyPkcs8);
        }
    }

    /// <summary>
    /// Met à jour les règles d'un groupe (titre, modérateurs, mot de passe, TTL ou arrêt définitif)
    /// en signant une nouvelle version du <see cref="GroupManifest"/> avec la clé privée du groupe stockée dans le coffre.
    /// </summary>
    public async Task<GroupManifest> UpdateGroupManifestAsync(
        AccountSession ownerSession,
        GroupGovernanceState state,
        string? newTitle = null,
        IReadOnlyDictionary<AuthorId, string>? newModerators = null,
        TimeSpan? newEphemeralTtl = null,
        bool? terminateGroup = null,
        string? newJoinPassword = null,
        bool clearJoinPassword = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownerSession);
        ArgumentNullException.ThrowIfNull(state);

        PersistedGroupVaultEntry entry = await LoadVaultEntryAsync(ownerSession, state.GroupId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(entry.OwnerPrivateKeyPkcs8Base64))
        {
            throw new UnauthorizedAccessException("Current account does not hold the group authority private key.");
        }

        byte[] privBytes = Convert.FromBase64String(entry.OwnerPrivateKeyPkcs8Base64);
        using var groupKey = ECDsa.Create();
        try
        {
            groupKey.ImportPkcs8PrivateKey(privBytes, out _);

            GroupManifest cur = state.CurrentManifest;
            bool isProtected = cur.IsPasswordProtected;
            string? saltB64 = cur.JoinPasswordSaltBase64;
            string? verifierB64 = cur.JoinPasswordVerifierBase64;

            if (clearJoinPassword)
            {
                isProtected = false;
                saltB64 = null;
                verifierB64 = null;
            }
            else if (!string.IsNullOrEmpty(newJoinPassword))
            {
                byte[] salt = RandomNumberGenerator.GetBytes(JoinSaltSizeBytes);
                byte[] verifier = DerivePasswordVerifier(newJoinPassword, salt);
                isProtected = true;
                saltB64 = Convert.ToBase64String(salt);
                verifierB64 = Convert.ToBase64String(verifier);
            }

            GroupManifest unsigned = cur with
            {
                Version = cur.Version + 1,
                Title = string.IsNullOrWhiteSpace(newTitle) ? cur.Title : newTitle.Trim(),
                ModeratorPublicKeysSpkiBase64 = newModerators ?? cur.ModeratorPublicKeysSpkiBase64,
                EphemeralMessageTtl = newEphemeralTtl ?? cur.EphemeralMessageTtl,
                IsTerminated = terminateGroup ?? cur.IsTerminated,
                IsPasswordProtected = isProtected,
                JoinPasswordSaltBase64 = saltB64,
                JoinPasswordVerifierBase64 = verifierB64,
                IssuedAtUtc = DateTimeOffset.UtcNow,
                OwnerSignatureBase64 = string.Empty
            };

            byte[] sig = groupKey.SignData(unsigned.BuildSignableBytes(), HashAlgorithmName.SHA256);
            GroupManifest signed = unsigned with { OwnerSignatureBase64 = Convert.ToBase64String(sig) };

            if (!GroupGovernanceEngine.TryApplyManifest(state, signed, _accountService.VerifySignature, out string? error))
            {
                throw new InvalidOperationException($"Manifest update rejected: {error}");
            }

            await SaveGroupToVaultAsync(ownerSession, state, entry.OwnerPrivateKeyPkcs8Base64, cancellationToken).ConfigureAwait(false);
            GovernanceStateChanged?.Invoke(state.GroupId);
            return signed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privBytes);
        }
    }

    public Task<GroupManifest> TerminateGroupAsync(
        AccountSession ownerSession,
        GroupGovernanceState state,
        CancellationToken cancellationToken = default)
        => UpdateGroupManifestAsync(ownerSession, state, terminateGroup: true, cancellationToken: cancellationToken);

    /// <summary>
    /// Signe une action d'admission ou de modération avec la session du modérateur ou créateur.
    /// </summary>
    public ModerationAction SignModerationAction(
        AccountSession signerSession,
        GroupGovernanceState state,
        AuthorId targetAuthorId,
        string targetPublicKeySpkiBase64,
        MemberStatus newStatus,
        string reason,
        NodeId? targetLastKnownNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(signerSession);
        ArgumentNullException.ThrowIfNull(state);

        (IReadOnlyList<string> parents, ulong nextClock) = state.PrepareNextGovernanceContext();
        var unsigned = new ModerationAction(
            GroupId: state.GroupId,
            ManifestVersion: state.CurrentManifest.Version,
            LamportClock: nextClock,
            TargetAuthorId: targetAuthorId,
            TargetPublicKeySpkiBase64: targetPublicKeySpkiBase64,
            TargetLastKnownNodeId: targetLastKnownNodeId,
            NewStatus: newStatus,
            SignerAuthorId: signerSession.Profile.AuthorId,
            Reason: reason ?? string.Empty,
            IssuedAtUtc: DateTimeOffset.UtcNow,
            SignatureBase64: string.Empty,
            Parents: parents,
            Kind: GovernanceActionKind.MemberModeration);

        byte[] sig = _accountService.SignData(signerSession, unsigned.BuildSignableBytes());
        return unsigned with { SignatureBase64 = Convert.ToBase64String(sig) };
    }

    /// <summary>
    /// Crée un lien d'invitation <c>whispernet://invite/...</c> (réservé aux modérateurs actifs),
    /// avec possibilité de pré-approuver le destinataire avant envoi.
    /// </summary>
    public string CreateInvitationLink(
        AccountSession moderatorSession,
        GroupGovernanceState state,
        NodeId bootstrapNodeId,
        bool preApprove = false,
        AuthorId? targetAuthorId = null,
        TimeSpan? preApprovalTtl = null)
    {
        ArgumentNullException.ThrowIfNull(moderatorSession);
        ArgumentNullException.ThrowIfNull(state);

        if (!state.ActiveModerators.ContainsKey(moderatorSession.Profile.AuthorId))
        {
            throw new UnauthorizedAccessException("Only active group moderators can create invitation links.");
        }

        if (state.CurrentManifest.IsTerminated)
        {
            throw new InvalidOperationException("Cannot create an invitation link for a terminated group.");
        }

        InvitePreApprovalTicket? ticket = null;
        if (preApprove)
        {
            var unsignedTicket = new InvitePreApprovalTicket(
                GroupId: state.GroupId,
                TicketId: Guid.NewGuid().ToString("N"),
                TargetAuthorId: targetAuthorId,
                ExpiresAtUtc: DateTimeOffset.UtcNow.Add(preApprovalTtl ?? TimeSpan.FromHours(24)),
                IssuerAuthorId: moderatorSession.Profile.AuthorId,
                SignatureBase64: string.Empty);

            byte[] sig = _accountService.SignData(moderatorSession, unsignedTicket.BuildSignableBytes());
            ticket = unsignedTicket with { SignatureBase64 = Convert.ToBase64String(sig) };
        }

        var data = new GroupInvitationData(
            GroupId: state.GroupId,
            BootstrapNodeId: bootstrapNodeId,
            Title: state.CurrentManifest.Title,
            IsPasswordProtected: state.CurrentManifest.IsPasswordProtected,
            PreApprovalTicket: ticket);

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data);
        string encoded = ToBase64Url(jsonBytes);
        return $"whispernet://invite/{encoded}";
    }

    public static GroupInvitationData ParseInvitationLink(string inviteLink)
    {
        if (string.IsNullOrWhiteSpace(inviteLink))
        {
            throw new ArgumentException("Invitation link cannot be empty.", nameof(inviteLink));
        }

        const string prefix = "whispernet://invite/";
        string trimmed = inviteLink.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Invitation link must start with 'whispernet://invite/'.");
        }

        string token = trimmed[prefix.Length..];
        byte[] jsonBytes = FromBase64Url(token);
        return JsonSerializer.Deserialize<GroupInvitationData>(jsonBytes)
            ?? throw new FormatException("Invalid invitation link payload.");
    }

    /// <summary>
    /// Crée une demande d'admission signée par le pair souhaitant rejoindre un groupe.
    /// </summary>
    public JoinApprovalRequest CreateJoinApprovalRequest(
        AccountSession requesterSession,
        GroupId groupId,
        NodeId requesterNodeId,
        string? joinPassword = null,
        InvitePreApprovalTicket? preApprovalTicket = null)
    {
        ArgumentNullException.ThrowIfNull(requesterSession);

        var unsigned = new JoinApprovalRequest(
            GroupId: groupId,
            RequesterAuthorId: requesterSession.Profile.AuthorId,
            RequesterUsername: requesterSession.Profile.Username,
            RequesterPublicKeySpkiBase64: requesterSession.Profile.PublicKeySpkiBase64,
            RequesterNodeId: requesterNodeId,
            JoinPassword: joinPassword,
            RequestedAtUtc: DateTimeOffset.UtcNow,
            SignatureBase64: string.Empty,
            PreApprovalTicket: preApprovalTicket);

        byte[] sig = _accountService.SignData(requesterSession, unsigned.BuildSignableBytes());
        return unsigned with { SignatureBase64 = Convert.ToBase64String(sig) };
    }

    /// <summary>
    /// Valide une demande d'admission entrante (signature, cohérence AuthorId, mot de passe optionnel).
    /// Si un jeton de pré-approbation valide est fourni, l'auteur est admis automatiquement ;
    /// sinon la demande est mise en file d'attente pour les modérateurs.
    /// </summary>
    public bool TryProcessJoinApprovalRequest(
        GroupGovernanceState state,
        JoinApprovalRequest request,
        out string? error,
        out ModerationAction? autoAdmittedAction,
        AccountSession? autoAdmitModeratorSession = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        autoAdmittedAction = null;

        if (request.GroupId != state.GroupId)
        {
            error = "JoinApprovalRequest GroupId does not match this group.";
            return false;
        }

        if (state.CurrentManifest.IsTerminated)
        {
            error = "Group is terminated.";
            return false;
        }

        byte[] pubKey;
        byte[] sig;
        try
        {
            pubKey = Convert.FromBase64String(request.RequesterPublicKeySpkiBase64);
            sig = Convert.FromBase64String(request.SignatureBase64);
        }
        catch (FormatException)
        {
            error = "Invalid Base64 in JoinApprovalRequest.";
            return false;
        }

        var derivedAuthorId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(pubKey)));
        if (derivedAuthorId != request.RequesterAuthorId)
        {
            error = "RequesterPublicKey does not match RequesterAuthorId.";
            return false;
        }

        if (!_accountService.VerifySignature(pubKey, request.BuildSignableBytes(), sig))
        {
            error = "Invalid signature on JoinApprovalRequest.";
            return false;
        }

        MemberStatus currentStatus = state.GetMemberStatus(request.RequesterAuthorId);
        if (currentStatus == MemberStatus.Banned)
        {
            error = "Requester is banned from this group.";
            return false;
        }

        if (state.CurrentManifest.IsPasswordProtected && !VerifyJoinPassword(state.CurrentManifest, request.JoinPassword))
        {
            error = "Invalid group join password.";
            return false;
        }

        if (currentStatus is MemberStatus.Admitted or MemberStatus.Silenced)
        {
            state.KnownAuthorNodes[request.RequesterAuthorId] = request.RequesterNodeId;
            error = null;
            return true;
        }

        if (request.PreApprovalTicket is InvitePreApprovalTicket ticket &&
            autoAdmitModeratorSession is not null &&
            VerifyPreApprovalTicket(state, ticket, request.RequesterAuthorId))
        {
            autoAdmittedAction = SetMemberStatus(
                autoAdmitModeratorSession,
                state,
                request.RequesterAuthorId,
                request.RequesterPublicKeySpkiBase64,
                MemberStatus.Admitted,
                $"Pre-approved via invite {ticket.TicketId[..Math.Min(8, ticket.TicketId.Length)]}",
                request.RequesterNodeId);
            error = null;
            return true;
        }

        ConcurrentDictionary<AuthorId, JoinApprovalRequest> queue = _pendingRequests.GetOrAdd(
            state.GroupId,
            static _ => new ConcurrentDictionary<AuthorId, JoinApprovalRequest>());
        queue[request.RequesterAuthorId] = request;
        PendingRequestsChanged?.Invoke(state.GroupId);

        error = null;
        return true;
    }

    public bool TryProcessJoinApprovalRequest(
        GroupGovernanceState state,
        JoinApprovalRequest request,
        out string? error)
        => TryProcessJoinApprovalRequest(state, request, out error, out _, null);

    private bool VerifyPreApprovalTicket(
        GroupGovernanceState state,
        InvitePreApprovalTicket ticket,
        AuthorId requesterAuthorId)
    {
        if (ticket.GroupId != state.GroupId || ticket.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return false;
        }

        if (ticket.TargetAuthorId is AuthorId target && target != requesterAuthorId)
        {
            return false;
        }

        if (!state.ActiveModerators.TryGetValue(ticket.IssuerAuthorId, out string? modPubKeyB64))
        {
            return false;
        }

        try
        {
            byte[] modPubKey = Convert.FromBase64String(modPubKeyB64);
            byte[] sig = Convert.FromBase64String(ticket.SignatureBase64);
            return _accountService.VerifySignature(modPubKey, ticket.BuildSignableBytes(), sig);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        int mod = padded.Length % 4;
        if (mod != 0)
        {
            padded = padded.PadRight(padded.Length + (4 - mod), '=');
        }
        return Convert.FromBase64String(padded);
    }

    public IReadOnlyList<JoinApprovalRequest> GetPendingJoinRequests(GroupId groupId)
    {
        if (!_pendingRequests.TryGetValue(groupId, out ConcurrentDictionary<AuthorId, JoinApprovalRequest>? queue))
        {
            return Array.Empty<JoinApprovalRequest>();
        }

        return queue.Values
            .OrderBy(static r => r.RequestedAtUtc)
            .ToList();
    }

    /// <summary>
    /// Accepte un pair en attente (statut <see cref="MemberStatus.Admitted"/>) et retourne l'action signée à diffuser.
    /// </summary>
    public ModerationAction AcceptPendingMember(
        AccountSession moderatorSession,
        GroupGovernanceState state,
        AuthorId requesterAuthorId)
    {
        if (!_pendingRequests.TryGetValue(state.GroupId, out var queue) ||
            !queue.TryRemove(requesterAuthorId, out JoinApprovalRequest? req))
        {
            throw new KeyNotFoundException($"No pending join request for AuthorId {requesterAuthorId}.");
        }

        ModerationAction admit = SignModerationAction(
            moderatorSession,
            state,
            req.RequesterAuthorId,
            req.RequesterPublicKeySpkiBase64,
            MemberStatus.Admitted,
            "Accepted by moderator",
            req.RequesterNodeId);

        if (!GroupGovernanceEngine.TryApplyModerationAction(
                state,
                admit,
                _accountService.VerifySignature,
                out _,
                out string? error))
        {
            throw new InvalidOperationException($"Failed to admit member: {error}");
        }

        GroupGovernanceEngine.CompactGovernanceLog(state);
        PendingRequestsChanged?.Invoke(state.GroupId);
        GovernanceStateChanged?.Invoke(state.GroupId);
        return admit;
    }

    public bool IgnorePendingMember(GroupId groupId, AuthorId requesterAuthorId)
    {
        if (_pendingRequests.TryGetValue(groupId, out var queue) && queue.TryRemove(requesterAuthorId, out _))
        {
            PendingRequestsChanged?.Invoke(groupId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Applique directement un changement de statut (<see cref="MemberStatus.Admitted"/>,
    /// <see cref="MemberStatus.Silenced"/> ou <see cref="MemberStatus.Banned"/>) signé par un modérateur.
    /// </summary>
    public ModerationAction SetMemberStatus(
        AccountSession moderatorSession,
        GroupGovernanceState state,
        AuthorId targetAuthorId,
        string targetPublicKeySpkiBase64,
        MemberStatus newStatus,
        string reason,
        NodeId? targetLastKnownNodeId = null)
    {
        ModerationAction action = SignModerationAction(
            moderatorSession,
            state,
            targetAuthorId,
            targetPublicKeySpkiBase64,
            newStatus,
            reason,
            targetLastKnownNodeId);

        if (!GroupGovernanceEngine.TryApplyModerationAction(
                state,
                action,
                _accountService.VerifySignature,
                out _,
                out string? error))
        {
            throw new InvalidOperationException($"Moderation action rejected: {error}");
        }

        GroupGovernanceEngine.CompactGovernanceLog(state);
        GovernanceStateChanged?.Invoke(state.GroupId);
        return action;
    }

    public static bool VerifyJoinPassword(GroupManifest manifest, string? password)
    {
        if (!manifest.IsPasswordProtected)
        {
            return true;
        }

        if (string.IsNullOrEmpty(password) ||
            string.IsNullOrWhiteSpace(manifest.JoinPasswordSaltBase64) ||
            string.IsNullOrWhiteSpace(manifest.JoinPasswordVerifierBase64))
        {
            return false;
        }

        byte[] salt = Convert.FromBase64String(manifest.JoinPasswordSaltBase64);
        byte[] expected = Convert.FromBase64String(manifest.JoinPasswordVerifierBase64);
        byte[] actual = DerivePasswordVerifier(password, salt);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public async Task SaveGroupToVaultAsync(
        AccountSession session,
        GroupGovernanceState state,
        string? ownerPrivateKeyPkcs8Base64 = null,
        CancellationToken cancellationToken = default)
    {
        string? effectiveOwnerKey = ownerPrivateKeyPkcs8Base64;
        if (string.IsNullOrWhiteSpace(effectiveOwnerKey))
        {
            try
            {
                PersistedGroupVaultEntry existing = await LoadVaultEntryAsync(session, state.GroupId, cancellationToken).ConfigureAwait(false);
                effectiveOwnerKey = existing.OwnerPrivateKeyPkcs8Base64;
            }
            catch
            {
                // Pas d'entrée existante ou membre non-owner
            }
        }

        var entry = new PersistedGroupVaultEntry(
            GroupId: state.GroupId,
            OwnerPrivateKeyPkcs8Base64: effectiveOwnerKey,
            CurrentManifest: state.CurrentManifest,
            MemberActions: state.GovernanceLog.Values.ToList(),
            KnownAuthorNodes: new Dictionary<AuthorId, NodeId>(state.KnownAuthorNodes));

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        await _accountService.WriteVaultFileAsync(
            session,
            GetGroupVaultRelativePath(state.GroupId),
            jsonBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GroupGovernanceState> LoadGroupFromVaultAsync(
        AccountSession session,
        GroupId groupId,
        CancellationToken cancellationToken = default)
    {
        PersistedGroupVaultEntry entry = await LoadVaultEntryAsync(session, groupId, cancellationToken).ConfigureAwait(false);
        var state = new GroupGovernanceState(entry.CurrentManifest);
        foreach (ModerationAction action in entry.MemberActions.OrderBy(static a => a.LamportClock))
        {
            GroupGovernanceEngine.TryApplyModerationAction(state, action, _accountService.VerifySignature, out _, out _);
        }

        GroupGovernanceEngine.CompactGovernanceLog(state);
        if (entry.KnownAuthorNodes is not null)
        {
            foreach ((AuthorId authorId, NodeId nodeId) in entry.KnownAuthorNodes)
            {
                if (!state.KnownAuthorNodes.ContainsKey(authorId) &&
                    state.GetMemberStatus(authorId) is MemberStatus.Admitted or MemberStatus.Silenced)
                {
                    state.KnownAuthorNodes[authorId] = nodeId;
                }
            }
        }
        return state;
    }

    /// <summary>
    /// Charge tous les groupes enregistrés dans <c>vault/groups/*.json</c> pour la session déverrouillée.
    /// </summary>
    public async Task<IReadOnlyList<GroupGovernanceState>> LoadAllGroupsFromVaultAsync(
        AccountSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        string groupsDir = Path.Combine(session.AccountDirectory, "vault", "groups");
        if (!Directory.Exists(groupsDir))
        {
            return Array.Empty<GroupGovernanceState>();
        }

        var loaded = new List<GroupGovernanceState>();
        foreach (string file in Directory.EnumerateFiles(groupsDir, "*.json"))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            var groupId = new GroupId(stem);
            if (!groupId.IsValid)
            {
                continue;
            }

            try
            {
                GroupGovernanceState state = await LoadGroupFromVaultAsync(session, groupId, cancellationToken).ConfigureAwait(false);
                loaded.Add(state);
            }
            catch
            {
                // Ignorer un fichier de groupe corrompu
            }
        }

        return loaded
            .OrderBy(static g => g.CurrentManifest.IssuedAtUtc)
            .ThenBy(static g => g.GroupId.Value, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<PersistedGroupVaultEntry> LoadVaultEntryAsync(
        AccountSession session,
        GroupId groupId,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await _accountService.ReadVaultFileAsync(
            session,
            GetGroupVaultRelativePath(groupId),
            cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<PersistedGroupVaultEntry>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"Corrupted group vault file for {groupId}.");
    }

    private static string GetGroupVaultRelativePath(GroupId groupId) => $"groups/{groupId.Value}.json";

    private static byte[] DerivePasswordVerifier(string password, ReadOnlySpan<byte> salt)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                JoinKdfIterations,
                HashAlgorithmName.SHA256,
                JoinVerifierSizeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
