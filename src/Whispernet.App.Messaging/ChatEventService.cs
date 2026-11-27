using System.Text;
using Whispernet.Core;

namespace Whispernet.App.Messaging;

/// <summary>
/// Service applicatif reliant la signature ECDSA P-256 des comptes (<see cref="AccountSession"/>)
/// au moteur de DAG causal (<see cref="CausalDagEngine"/>).
/// </summary>
public sealed class ChatEventService{
    private readonly IAccountService _accountService;

    public ChatEventService(IAccountService accountService){
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
    }

    /// <summary>
    /// Crée et signe un nouveau <see cref="ChatEvent"/> à partir des parents et de l'horloge de Lamport fournis.
    /// </summary>
    public ChatEvent CreateSignedEvent(
        AccountSession session, byte[] roomId,
        IReadOnlyList<byte[]> parents,
        ulong logicalClock, ulong timestampMs,
        string content,
        byte[]? channelId = null){

        ArgumentNullException.ThrowIfNull(session);
        byte[] payload = Encoding.UTF8.GetBytes(content);
        byte[] authorPubKey = Convert.FromBase64String(session.Profile.PublicKeySpkiBase64);
        byte[] eventId = ChatEvent.ComputeEventId(parents, payload);
        byte[] signable = ChatEvent.BuildSignableBytes(
            eventId,
            roomId,
            authorPubKey,
            parents,
            logicalClock,
            timestampMs,
            payload,
            channelId);
        byte[] signature = _accountService.SignData(session, signable);

        return new ChatEvent(
            EventId: eventId,
            RoomId: roomId,
            AuthorPublicKey: authorPubKey,
            Parents: parents,
            LogicalClock: logicalClock,
            TimestampMs: timestampMs,
            Payload: payload,
            Signature: signature,
            ChannelId: channelId);
    }

    /// <summary>
    /// Vérifie et insère un <see cref="ChatEvent"/> dans le DAG du salon en retriant l'historique.
    /// </summary>
    public bool TryIngestEvent(ChatRoomDagState roomState, ChatEvent evt, out string? error){
        return CausalDagEngine.TryIngestEvent( roomState, evt,
            _accountService.VerifySignature, out error);
    }

    public bool TryIngestBatch(ChatRoomDagState roomState, IEnumerable<ChatEvent> events, out string? error) {
        return CausalDagEngine.TryIngestBatch( roomState, events,
            _accountService.VerifySignature, out error);
    }
}
