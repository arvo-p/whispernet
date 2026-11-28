using System.Security.Cryptography;
using System.Text;

namespace Whispernet.Core;

/// <summary>
/// Relationship status between the local account and a peer in the friend graph.
/// </summary>
public enum FriendStatus
{
    None = 0,
    PendingOutgoing = 1,
    PendingIncoming = 2,
    Accepted = 3,
    Rejected = 4,
    Removed = 5
}

/// <summary>
/// Persistent and in-memory entry representing a peer in the local friend graph.
/// </summary>
public sealed record FriendEntry(
    AuthorId PeerAuthorId,
    string PeerPublicKeySpkiBase64,
    string DisplayName,
    NodeId? LastKnownNodeId,
    FriendStatus Status,
    ulong LogicalClock,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Signed friend request sent from one author to another.
/// </summary>
public sealed record FriendRequestPacket(
    Guid PacketUuid,
    AuthorId SenderAuthorId,
    AuthorId RecipientAuthorId,
    string SenderPublicKeySpkiBase64,
    string SenderDisplayName,
    NodeId? SenderNodeId,
    ulong LogicalClock,
    DateTimeOffset SentAtUtc,
    string SignatureBase64)
{
    public byte[] BuildSignableBytes()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write("WHISPERNET_FRIEND_REQUEST_V1");
        bw.Write(PacketUuid.ToByteArray());
        bw.Write(SenderAuthorId.Value);
        bw.Write(RecipientAuthorId.Value);
        bw.Write(SenderPublicKeySpkiBase64);
        bw.Write(SenderDisplayName);
        bw.Write(SenderNodeId?.Value ?? string.Empty);
        bw.Write(LogicalClock);
        bw.Write(SentAtUtc.ToUnixTimeMilliseconds());
        bw.Flush();
        return ms.ToArray();
    }

    public string ComputePacketId()
        => Convert.ToHexStringLower(SHA256.HashData(BuildSignableBytes()));
}

/// <summary>
/// Signed accept or reject response to a friend request.
/// </summary>
public sealed record FriendResponsePacket(
    Guid PacketUuid,
    AuthorId SenderAuthorId,
    AuthorId RecipientAuthorId,
    string SenderPublicKeySpkiBase64,
    string SenderDisplayName,
    NodeId? SenderNodeId,
    bool Accepted,
    ulong LogicalClock,
    DateTimeOffset RespondedAtUtc,
    string SignatureBase64)
{
    public byte[] BuildSignableBytes()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write("WHISPERNET_FRIEND_RESPONSE_V1");
        bw.Write(PacketUuid.ToByteArray());
        bw.Write(SenderAuthorId.Value);
        bw.Write(RecipientAuthorId.Value);
        bw.Write(SenderPublicKeySpkiBase64);
        bw.Write(SenderDisplayName);
        bw.Write(SenderNodeId?.Value ?? string.Empty);
        bw.Write(Accepted);
        bw.Write(LogicalClock);
        bw.Write(RespondedAtUtc.ToUnixTimeMilliseconds());
        bw.Flush();
        return ms.ToArray();
    }

    public string ComputePacketId()
        => Convert.ToHexStringLower(SHA256.HashData(BuildSignableBytes()));
}

/// <summary>
/// Signed removal notice sent when a user removes a friend.
/// </summary>
public sealed record FriendRemovePacket(
    Guid PacketUuid,
    AuthorId SenderAuthorId,
    AuthorId RecipientAuthorId,
    string SenderPublicKeySpkiBase64,
    ulong LogicalClock,
    DateTimeOffset RemovedAtUtc,
    string SignatureBase64)
{
    public byte[] BuildSignableBytes()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write("WHISPERNET_FRIEND_REMOVE_V1");
        bw.Write(PacketUuid.ToByteArray());
        bw.Write(SenderAuthorId.Value);
        bw.Write(RecipientAuthorId.Value);
        bw.Write(SenderPublicKeySpkiBase64);
        bw.Write(LogicalClock);
        bw.Write(RemovedAtUtc.ToUnixTimeMilliseconds());
        bw.Flush();
        return ms.ToArray();
    }

    public string ComputePacketId()
        => Convert.ToHexStringLower(SHA256.HashData(BuildSignableBytes()));
}

/// <summary>
/// Out-of-band friend invitation payload encoded inside <c>whispernet://friend/...</c> links.
/// </summary>
public sealed record FriendInvitationData(
    AuthorId AuthorId,
    string PublicKeySpkiBase64,
    string DisplayName,
    NodeId BootstrapNodeId);
