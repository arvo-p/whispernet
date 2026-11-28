using System.Text.Json.Serialization;

namespace Whispernet.Core;

/// <summary>
/// État de livraison d'un message 1-on-1.
/// </summary>
public enum DirectMessageStatus
{
    Sending = 0,
    Delivered = 1,
    QueuedViaRelay = 2,
    Failed = 3
}

/// <summary>
/// Enveloppe chiffrée et signée d'un message 1-on-1 (transport direct QUIC ou encapsulée dans un bundle DTN).
/// </summary>
public sealed record DirectMessageEnvelope(
    Guid MessageUuid,
    AuthorId SenderAuthorId,
    AuthorId RecipientAuthorId,
    string SenderPublicKeySpkiBase64,
    NodeId? SenderNodeId,
    DateTimeOffset SentAtUtc,
    byte[] EncryptedPayload,
    long Epoch,
    string SignatureBase64);

/// <summary>
/// Accusé de réception signé pour un message 1-on-1 identifié par son UUID.
/// </summary>
public sealed record DirectMessageAck(
    Guid MessageUuid,
    AuthorId SenderAuthorId,
    AuthorId RecipientAuthorId,
    string SenderPublicKeySpkiBase64,
    DateTimeOffset ReceivedAtUtc,
    string SignatureBase64);

/// <summary>
/// Indicateur de frappe éphémère transmis uniquement sur un stream QUIC actif (jamais persisté ni routé en DTN).
/// </summary>
public sealed record TypingIndicatorPacket(
    AuthorId SenderAuthorId,
    AuthorId RecipientAuthorId,
    bool IsTyping,
    DateTimeOffset SentAtUtc);

/// <summary>
/// Bundle DTN chiffré et estampillé d'un tag de routage aveugle rotatif (<c>HMAC(pair_secret, epoch)</c>).
/// </summary>
public sealed record DtnBundle(
    Guid BundleId,
    Guid MessageUuid,
    string BlindRoutingTagHex,
    long Epoch,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int HopCount,
    int MaxHops,
    byte[] EncryptedEnvelopeBytes);

/// <summary>
/// Requête d'interrogation par tags aveugles envoyée aux relais lors d'une reconnexion.
/// </summary>
public sealed record DtnTagQuery(
    NodeId RequesterNodeId,
    IReadOnlyList<string> BlindTagsHex);

/// <summary>
/// Représentation persistée/applicative d'un message 1-on-1 dans un fil de discussion.
/// </summary>
public sealed record DirectMessageRecord(
    Guid MessageUuid,
    AuthorId PeerAuthorId,
    bool IsOutgoing,
    string Content,
    DirectMessageStatus Status,
    DateTimeOffset SentAtUtc,
    DateTimeOffset? DeliveredAtUtc = null);
