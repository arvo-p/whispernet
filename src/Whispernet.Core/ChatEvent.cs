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
    byte[] Signature)
{
    public const int HashSizeBytes = 32;
    public const int MaxParents = 15;

    public string EventIdHex => Convert.ToHexStringLower(EventId);
    public string RoomIdHex => Convert.ToHexStringLower(RoomId);
    public AuthorId GetAuthorId() => new(Convert.ToHexStringLower(SHA256.HashData(AuthorPublicKey)));

    /// <summary>
    /// Calcule l'identifiant unique event_id (32 octets SHA-256) à partir de la liste ordonnée des parents et du payload.
    /// </summary>
    public static byte[] ComputeEventId(IReadOnlyList<byte[]> parents, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(parents);
        if (parents.Count > MaxParents)
        {
            throw new ArgumentException($"A ChatEvent cannot have more than {MaxParents} parents.", nameof(parents));
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
    /// Construit le bloc binaire canonique signé par la clé privée ECDSA de l'auteur.
    /// </summary>
    public static byte[] BuildSignableBytes(
        ReadOnlySpan<byte> eventId,
        ReadOnlySpan<byte> roomId,
        ReadOnlySpan<byte> authorPublicKey,
        IReadOnlyList<byte[]> parents,
        ulong logicalClock,
        ulong timestampMs,
        ReadOnlySpan<byte> payload)
    {
        int totalLen = HashSizeBytes + HashSizeBytes + 4 + authorPublicKey.Length
            + 4 + (parents.Count * HashSizeBytes) + 8 + 8 + 4 + payload.Length;
        byte[] buffer = new byte[totalLen];
        Span<byte> span = buffer;
        int offset = 0;

        eventId.CopyTo(span.Slice(offset, HashSizeBytes));
        offset += HashSizeBytes;

        roomId.CopyTo(span.Slice(offset, HashSizeBytes));
        offset += HashSizeBytes;

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
