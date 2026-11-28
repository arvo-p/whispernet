using System.Buffers.Binary;
using System.Text.Json;
using Whispernet.Core;

namespace Whispernet.Daemon.Contracts;

/// <summary>
/// Constantes IPC du démon Whispernet (D-Bus sous Linux, Named Pipe ACL sous Windows).
/// </summary>
public static class DaemonIpcConstants
{
    public const string DBusServiceName = "org.whispernet.Daemon";
    public const string DBusObjectPath = "/org/whispernet/Daemon";
    public const string DBusInterfaceName = "org.whispernet.Daemon";
    public const string WindowsPipeName = "whispernetd";
}

public sealed record DaemonStatusDto(
    string SelfNodeId,
    string SelfTicket,
    int OnlinePeerCount,
    int HeldDtnBundleCount,
    long UptimeSeconds);

public sealed record RegisterWatchTagsRequest(
    AuthorId AuthorId,
    IReadOnlyList<string> BlindTagsHex);

public sealed record SendWirePacketRequest(
    NodeId TargetNodeId,
    byte[] SerializedPacket,
    string? CachedTicket = null,
    bool AllowDhtLookup = true);

public sealed record SubscribeGroupRequest(
    GroupId GroupId,
    IReadOnlyList<NodeId> KnownPeers);

public sealed record DaemonIncomingPacketDto(
    byte[] SerializedPacket,
    DateTimeOffset ReceivedAtUtc);

public sealed record DaemonPeerSeenDto(
    NodeId NodeId,
    NodeRole Role,
    DateTimeOffset SeenAtUtc);

public enum DaemonIpcMessageKind
{
    None = 0,
    GetStatusRequest = 1,
    GetStatusResponse = 2,
    RegisterWatchTagsRequest = 3,
    RegisterWatchTagsResponse = 4,
    SendWirePacketRequest = 5,
    SendWirePacketResponse = 6,
    SubscribeGroupRequest = 7,
    SubscribeGroupResponse = 8,
    ConnectPeerRequest = 9,
    ConnectPeerResponse = 10,
    IncomingPacketEvent = 11,
    PeerSeenEvent = 12,
    ShutdownRequest = 13,
    ShutdownResponse = 14
}

/// <summary>
/// Enveloppe générique utilisée sur le Named Pipe Windows (et les flux IPC binaires).
/// </summary>
public sealed record DaemonIpcEnvelope(
    DaemonIpcMessageKind Kind,
    Guid RequestId,
    bool Success = true,
    string? Error = null,
    DaemonStatusDto? Status = null,
    RegisterWatchTagsRequest? RegisterWatchTags = null,
    IReadOnlyList<byte[]>? MatchedBundles = null,
    SendWirePacketRequest? SendWirePacket = null,
    SubscribeGroupRequest? SubscribeGroup = null,
    NodeId? ConnectTargetNodeId = null,
    DaemonIncomingPacketDto? IncomingPacket = null,
    DaemonPeerSeenDto? PeerSeen = null);

public static class DaemonIpcFraming
{
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, DaemonIpcEnvelope envelope, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<DaemonIpcEnvelope?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int read = await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len <= 0 || len > MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid IPC frame length: {len}");
        }

        byte[] payload = new byte[len];
        int bodyRead = await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        if (bodyRead < len)
        {
            return null;
        }

        return JsonSerializer.Deserialize<DaemonIpcEnvelope>(payload);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return total;
            }
            total += n;
        }
        return total;
    }
}
