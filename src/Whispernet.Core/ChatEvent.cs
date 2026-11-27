using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Whispernet.Core;

/// <summary>
/// Événement signé d'un salon de discussion formant un DAG causal (jusqu'à 15 parents).
/// </summary>
public sealed record ChatEvent(
    byte[] EventId,
    byte[] RoomId,
    byte[] AuthorPublicKey,
    IReadOnlyList<byte[]> Parents,
    ulong LogicalClock,
    ulong TimestampMs,
    byte[] Payload,
    byte[] Signature,
    byte[]? ChannelId = null) : ICausalEvent
{
    public const int HashSizeBytes = CausalDagEngine.HashSizeBytes;
    public const int MaxParents = CausalDagEngine.MaxParents;

    public string EventIdHex => Convert.ToHexStringLower(EventId);
    public string RoomIdHex => Convert.ToHexStringLower(RoomId);

    /// <summary>
    /// Identifiant de canal effectif (32 octets) : <see cref="ChannelId"/> s'il est renseigné,
    /// sinon le hash du canal <c>#general</c> par défaut du groupe.
    /// </summary>
    public byte[] EffectiveChannelId =>
        ChannelId is { Length: HashSizeBytes }
            ? ChannelId
            : Core.ChannelId.DefaultGeneralFor(new GroupId(RoomIdHex)).ToBytes();

    public string EffectiveChannelIdHex => Convert.ToHexStringLower(EffectiveChannelId);

    byte[] ICausalEvent.ScopeId => ChannelId is { Length: HashSizeBytes } ? ChannelId : RoomId;

    public AuthorId GetAuthorId() => new(Convert.ToHexStringLower(SHA256.HashData(AuthorPublicKey)));

    public byte[] BuildSignableBytes()
        => BuildSignableBytes(EventId, RoomId, AuthorPublicKey, Parents, LogicalClock, TimestampMs, Payload, ChannelId);

    /// <summary>
    /// Calcule l'identifiant unique event_id (32 octets SHA-256) à partir de la liste ordonnée des parents et du payload.
    /// </summary>
    public static byte[] ComputeEventId(IReadOnlyList<byte[]> parents, ReadOnlySpan<byte> payload)
        => CausalDagEngine.ComputeEventId(parents, payload, MaxParents);

    /// <summary>
    /// Construit le bloc binaire canonique signé par la clé privée ECDSA de l'auteur.
    /// </summary>
    public static byte[] BuildSignableBytes(
        ReadOnlySpan<byte> eventId,
        ReadOnlySpan<byte> roomId,
        ReadOnlySpan<byte> authorPublicKey,
        IReadOnlyList<byte[]> parents,
        ulong logicalClock,
        ulong timestampMs,
        ReadOnlySpan<byte> payload,
        byte[]? channelId = null)
    {
        int channelLen = channelId is { Length: HashSizeBytes } ? HashSizeBytes : 0;
        int totalLen = HashSizeBytes + HashSizeBytes + channelLen + 4 + authorPublicKey.Length
            + 4 + (parents.Count * HashSizeBytes) + 8 + 8 + 4 + payload.Length;
        byte[] buffer = new byte[totalLen];
        Span<byte> span = buffer;
        int offset = 0;

        eventId.CopyTo(span.Slice(offset, HashSizeBytes));
        offset += HashSizeBytes;

        roomId.CopyTo(span.Slice(offset, HashSizeBytes));
        offset += HashSizeBytes;

        if (channelLen > 0)
        {
            channelId.AsSpan().CopyTo(span.Slice(offset, HashSizeBytes));
            offset += HashSizeBytes;
        }

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), authorPublicKey.Length);
        offset += 4;
        authorPublicKey.CopyTo(span.Slice(offset, authorPublicKey.Length));
        offset += authorPublicKey.Length;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), parents.Count);
        offset += 4;
        foreach (byte[] p in parents)
        {
            p.CopyTo(span.Slice(offset, HashSizeBytes));
            offset += HashSizeBytes;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset, 8), logicalClock);
        offset += 8;

        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset, 8), timestampMs);
        offset += 8;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), payload.Length);
        offset += 4;
        payload.CopyTo(span.Slice(offset, payload.Length));

        return buffer;
    }
}

/// <summary>
/// État en mémoire du DAG causal d'un salon de discussion.
/// </summary>
public sealed class ChatRoomDagState : CausalDagState<ChatEvent>
{
    public byte[] RoomId => ScopeId;
    public string RoomIdHex => ScopeIdHex;

    public ChatRoomDagState(byte[] roomId) : base(roomId)
    {
    }
}
