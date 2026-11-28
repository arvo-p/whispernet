namespace Whispernet.Core;

/// <summary>
/// Abstraction de transport P2P utilisée par <c>GroupMeshService</c> et <c>DirectMessagingService</c>.
/// Implémentée soit directement par <c>IrohEndpointAdapter</c>, soit par <c>DaemonMeshTransport</c>
/// connecté au démon d'arrière-plan <c>whispernetd</c>.
/// </summary>
public interface IMeshTransport : IDisposable
{
    NodeId SelfId { get; }
    string SelfTicket { get; }

    bool HasActiveConnection(NodeId target);

    Task<bool> ConnectSmartAsync(
        NodeId target,
        string? cachedTicket = null,
        bool allowDhtLookup = true,
        CancellationToken ct = default);

    Task SendUniAsync(
        NodeId target,
        byte[] payload,
        string? cachedTicket = null,
        bool allowDhtLookup = true,
        CancellationToken ct = default);

    void CloseConnection(NodeId target);

    void StartAcceptLoop(Action<byte[]> onPayloadReceived, CancellationToken ct = default);
}
