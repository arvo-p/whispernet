using System.Buffers.Binary;
using System.Text;

namespace Whispernet.Core;

/// <summary>
/// Statut d'un auteur dans la liste unifiée d'admis et de modération d'un groupe.
/// </summary>
public enum MemberStatus
{
    Unknown = 0,  // Absent du registre : ne peut ni publier ni recevoir
    Admitted = 1, // Peut publier et recevoir
    Silenced = 2, // Ne peut pas publier ; peut recevoir
    Banned = 3    // Ne peut ni publier ni recevoir ; déconnecté de l'overlay
}

/// <summary>
/// Type d'événement dans le DAG de gouvernance d'un groupe.
/// </summary>
public enum GovernanceActionKind
{
    MemberModeration = 0, // Admitted, Silenced, Banned
    PromoteModerator = 1, // Ajout d'un modérateur (signé par l'administrateur)
    DemoteModerator = 2   // Révocation d'un modérateur (signé par l'administrateur)
}

/// <summary>
/// Règles et métadonnées d'un groupe signées exclusivement par la clé privée dédiée du groupe.
/// </summary>
public sealed record GroupManifest(
    GroupId GroupId,
    string OwnerPublicKeySpkiBase64,
    ulong Version,
    string Title,
    bool IsPasswordProtected,
    string? JoinPasswordSaltBase64,
    string? JoinPasswordVerifierBase64,
    TimeSpan? EphemeralMessageTtl,
    bool IsTerminated,
    IReadOnlyDictionary<AuthorId, string> ModeratorPublicKeysSpkiBase64,
    DateTimeOffset IssuedAtUtc,
    string OwnerSignatureBase64,
    ulong CheckpointLamportClock = 0,
    IReadOnlyDictionary<AuthorId, MemberStatus>? CheckpointMemberStates = null,
    IReadOnlyList<GroupChannel>? Channels = null)
{
    /// <summary>
    /// Retourne la liste effective des canaux du groupe (contenant au minimum <c>#general</c> par défaut).
    /// </summary>
    public IReadOnlyList<GroupChannel> GetEffectiveChannels()
    {
        if (Channels is { Count: > 0 })
        {
            return Channels;
        }

        return new[]
        {
            new GroupChannel(ChannelId.DefaultGeneralFor(GroupId), ChannelId.DefaultGeneralName, IssuedAtUtc)
        };
    }

    public bool HasChannel(ChannelId channelId)
        => GetEffectiveChannels().Any(c => c.ChannelId == channelId);

    public GroupChannel? FindChannel(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return null;
        }

        string trimmed = nameOrId.Trim().TrimStart('#').Trim();
        IReadOnlyList<GroupChannel> list = GetEffectiveChannels();
        return list.FirstOrDefault(c =>
            c.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
            c.ChannelId.Value.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Construit la représentation binaire canonique signée par la clé privée du groupe.
    /// </summary>
    public byte[] BuildSignableBytes()
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        WriteLengthPrefixedString(bw, GroupId.Value);
        WriteLengthPrefixedString(bw, OwnerPublicKeySpkiBase64);
        bw.Write(Version);
        WriteLengthPrefixedString(bw, Title);
        bw.Write(IsPasswordProtected);
        WriteLengthPrefixedString(bw, JoinPasswordSaltBase64 ?? string.Empty);
        WriteLengthPrefixedString(bw, JoinPasswordVerifierBase64 ?? string.Empty);
        bw.Write(EphemeralMessageTtl?.Ticks ?? -1L);
        bw.Write(IsTerminated);

        var sortedMods = ModeratorPublicKeysSpkiBase64
            .OrderBy(static kv => kv.Key.Value, StringComparer.Ordinal)
            .ToList();
        bw.Write(sortedMods.Count);
        foreach ((AuthorId modId, string pubKey) in sortedMods)
        {
            WriteLengthPrefixedString(bw, modId.Value);
            WriteLengthPrefixedString(bw, pubKey);
        }

        bw.Write(IssuedAtUtc.UtcTicks);
        bw.Write(CheckpointLamportClock);
        if (CheckpointMemberStates is null)
        {
            bw.Write(-1);
        }
        else
        {
            var sortedSnap = CheckpointMemberStates
                .OrderBy(static kv => kv.Key.Value, StringComparer.Ordinal)
                .ToList();
            bw.Write(sortedSnap.Count);
            foreach ((AuthorId mId, MemberStatus st) in sortedSnap)
            {
                WriteLengthPrefixedString(bw, mId.Value);
                bw.Write((int)st);
            }
        }

        if (Channels is not null)
        {
            bw.Write(Channels.Count);
            foreach (GroupChannel ch in Channels)
            {
                WriteLengthPrefixedString(bw, ch.ChannelId.Value);
                WriteLengthPrefixedString(bw, ch.Name);
                bw.Write(ch.CreatedAtUtc.UtcTicks);
            }
        }

        bw.Flush();
        return ms.ToArray();
    }

    internal static void WriteLengthPrefixedString(BinaryWriter bw, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }
}

/// <summary>
/// Événement du DAG de gouvernance (modération, promotion ou révocation de modérateur)
/// ordonné causalement par <see cref="Parents"/> et <see cref="LamportClock"/>.
/// </summary>
public sealed record ModerationAction(
    GroupId GroupId,
    ulong ManifestVersion,
    ulong LamportClock,
    AuthorId TargetAuthorId,
    string TargetPublicKeySpkiBase64,
    NodeId? TargetLastKnownNodeId,
    MemberStatus NewStatus,
    AuthorId SignerAuthorId,
    string Reason,
    DateTimeOffset IssuedAtUtc,
    string SignatureBase64,
    IReadOnlyList<string>? Parents = null,
    GovernanceActionKind Kind = GovernanceActionKind.MemberModeration)
{
    public const int MaxParents = 15;

    /// <summary>Identifiant déterministe SHA-256 hexadécimal de cette décision.</summary>
    public string ComputeActionId()
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BuildSignableBytes()));

    /// <summary>
    /// Construit la représentation binaire canonique signée par le modérateur ou l'administrateur.
    /// </summary>
    public byte[] BuildSignableBytes()
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        GroupManifest.WriteLengthPrefixedString(bw, GroupId.Value);
        bw.Write(ManifestVersion);
        bw.Write(LamportClock);
        bw.Write((int)Kind);

        IReadOnlyList<string> parents = Parents ?? Array.Empty<string>();
        bw.Write(parents.Count);
        foreach (string p in parents)
        {
            GroupManifest.WriteLengthPrefixedString(bw, p);
        }

        GroupManifest.WriteLengthPrefixedString(bw, TargetAuthorId.Value);
        GroupManifest.WriteLengthPrefixedString(bw, TargetPublicKeySpkiBase64);
        GroupManifest.WriteLengthPrefixedString(bw, TargetLastKnownNodeId?.Value ?? string.Empty);
        bw.Write((int)NewStatus);
        GroupManifest.WriteLengthPrefixedString(bw, SignerAuthorId.Value);
        GroupManifest.WriteLengthPrefixedString(bw, Reason);
        bw.Write(IssuedAtUtc.UtcTicks);
        bw.Flush();
        return ms.ToArray();
    }
}

/// <summary>
/// Résumé léger échangé lors de la synchronisation anti-entropie d'un groupe.
/// </summary>
public sealed record GroupStateDigest(
    GroupId GroupId,
    ulong ManifestVersion,
    string ModerationStateHash);

/// <summary>
/// Jeton de pré-approbation signé par un modérateur et embarqué dans un lien d'invitation.
/// </summary>
public sealed record InvitePreApprovalTicket(
    GroupId GroupId,
    string TicketId,
    AuthorId? TargetAuthorId,
    DateTimeOffset ExpiresAtUtc,
    AuthorId IssuerAuthorId,
    string SignatureBase64)
{
    public byte[] BuildSignableBytes()
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        GroupManifest.WriteLengthPrefixedString(bw, GroupId.Value);
        GroupManifest.WriteLengthPrefixedString(bw, TicketId);
        GroupManifest.WriteLengthPrefixedString(bw, TargetAuthorId?.Value ?? string.Empty);
        bw.Write(ExpiresAtUtc.UtcTicks);
        GroupManifest.WriteLengthPrefixedString(bw, IssuerAuthorId.Value);
        bw.Flush();
        return ms.ToArray();
    }
}

/// <summary>
/// Demande d'admission signée par un pair souhaitant rejoindre un groupe.
/// </summary>
public sealed record JoinApprovalRequest(
    GroupId GroupId,
    AuthorId RequesterAuthorId,
    string RequesterUsername,
    string RequesterPublicKeySpkiBase64,
    NodeId RequesterNodeId,
    string? JoinPassword,
    DateTimeOffset RequestedAtUtc,
    string SignatureBase64,
    InvitePreApprovalTicket? PreApprovalTicket = null)
{
    public byte[] BuildSignableBytes()
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        GroupManifest.WriteLengthPrefixedString(bw, GroupId.Value);
        GroupManifest.WriteLengthPrefixedString(bw, RequesterAuthorId.Value);
        GroupManifest.WriteLengthPrefixedString(bw, RequesterUsername);
        GroupManifest.WriteLengthPrefixedString(bw, RequesterPublicKeySpkiBase64);
        GroupManifest.WriteLengthPrefixedString(bw, RequesterNodeId.Value);
        GroupManifest.WriteLengthPrefixedString(bw, JoinPassword ?? string.Empty);
        bw.Write(RequestedAtUtc.UtcTicks);
        GroupManifest.WriteLengthPrefixedString(bw, PreApprovalTicket?.TicketId ?? string.Empty);
        bw.Flush();
        return ms.ToArray();
    }
}
