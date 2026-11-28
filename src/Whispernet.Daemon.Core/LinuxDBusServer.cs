using System.Text.Json;
using Tmds.DBus.Protocol;
using Whispernet.Core;
using Whispernet.Daemon.Contracts;

namespace Whispernet.Daemon.Core;

/// <summary>
/// Serveur IPC D-Bus sous Linux (<c>org.whispernet.Daemon</c> sur <c>/org/whispernet/Daemon</c>)
/// utilisant <c>Tmds.DBus.Protocol</c> sans aucun socket brut géré par Whispernet.
/// </summary>
public sealed class LinuxDBusServer : IAsyncDisposable
{
    private readonly WhispernetDaemonHost _host;
    private readonly string _busAddress;
    private DBusConnection? _connection;

    public LinuxDBusServer(WhispernetDaemonHost host, string? busAddress = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _busAddress = string.IsNullOrWhiteSpace(busAddress)
            ? (DBusAddress.Session ?? throw new InvalidOperationException("D-Bus session address is not available."))
            : busAddress;

        _host.IncomingPacketAvailable += OnIncomingPacket;
        _host.PeerSeenAvailable += OnPeerSeen;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var conn = new DBusConnection(_busAddress);
        await conn.ConnectAsync().ConfigureAwait(false);
        conn.AddMethodHandler(new DaemonMethodHandler(conn, _host));
        await conn.RequestNameAsync(DaemonIpcConstants.DBusServiceName).ConfigureAwait(false);
        _connection = conn;
    }

    private void OnIncomingPacket(DaemonIncomingPacketDto dto)
    {
        DBusConnection? conn = _connection;
        if (conn is null)
        {
            return;
        }

        try
        {
            using MessageWriter writer = conn.GetMessageWriter();
            writer.WriteSignalHeader(
                path: DaemonIpcConstants.DBusObjectPath,
                @interface: DaemonIpcConstants.DBusInterfaceName,
                member: "IncomingPacket",
                signature: "ayx");
            writer.WriteArray(dto.SerializedPacket);
            writer.WriteInt64(dto.ReceivedAtUtc.ToUnixTimeMilliseconds());
            conn.TrySendMessage(writer.CreateMessage());
        }
        catch
        {
        }
    }

    private void OnPeerSeen(DaemonPeerSeenDto dto)
    {
        DBusConnection? conn = _connection;
        if (conn is null)
        {
            return;
        }

        try
        {
            using MessageWriter writer = conn.GetMessageWriter();
            writer.WriteSignalHeader(
                path: DaemonIpcConstants.DBusObjectPath,
                @interface: DaemonIpcConstants.DBusInterfaceName,
                member: "PeerSeen",
                signature: "six");
            writer.WriteString(dto.NodeId.Value);
            writer.WriteInt32((int)dto.Role);
            writer.WriteInt64(dto.SeenAtUtc.ToUnixTimeMilliseconds());
            conn.TrySendMessage(writer.CreateMessage());
        }
        catch
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _host.IncomingPacketAvailable -= OnIncomingPacket;
        _host.PeerSeenAvailable -= OnPeerSeen;
        _connection?.Dispose();
        _connection = null;
        return ValueTask.CompletedTask;
    }

    private sealed class DaemonMethodHandler : IPathMethodHandler
    {
        private readonly DBusConnection _conn;
        private readonly WhispernetDaemonHost _host;

        public DaemonMethodHandler(DBusConnection conn, WhispernetDaemonHost host)
        {
            _conn = conn;
            _host = host;
        }

        public string Path => DaemonIpcConstants.DBusObjectPath;

        public bool HandlesChildPaths => false;

        public async ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.InterfaceAsString != DaemonIpcConstants.DBusInterfaceName)
            {
                return;
            }

            switch (context.Request.MemberAsString)
            {
                case "GetStatus":
                {
                    DaemonStatusDto status = await _host.GetStatusAsync().ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(status);
                    using MessageWriter writer = context.CreateReplyWriter("s");
                    writer.WriteString(json);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                case "RegisterWatchTags":
                {
                    Reader reader = context.Request.GetBodyReader();
                    string authorId = reader.ReadString();
                    string[] tags = reader.ReadArrayOfString();
                    IReadOnlyList<byte[]> matched = await _host
                        .RegisterWatchTagsAsync(new RegisterWatchTagsRequest(new AuthorId(authorId), tags))
                        .ConfigureAwait(false);

                    using MessageWriter writer = context.CreateReplyWriter("aay");
                    ArrayStart outer = writer.WriteArrayStart(DBusType.Array);
                    foreach (byte[] pkt in matched)
                    {
                        writer.WriteArray(pkt);
                    }
                    writer.WriteArrayEnd(outer);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                case "SendWirePacket":
                {
                    Reader reader = context.Request.GetBodyReader();
                    string targetNodeId = reader.ReadString();
                    byte[] payload = reader.ReadArrayOfByte();
                    string cachedTicket = reader.ReadString();
                    bool allowDht = reader.ReadBool();

                    bool ok = await _host.SendWirePacketAsync(new SendWirePacketRequest(
                        new NodeId(targetNodeId),
                        payload,
                        string.IsNullOrWhiteSpace(cachedTicket) ? null : cachedTicket,
                        allowDht)).ConfigureAwait(false);

                    using MessageWriter writer = context.CreateReplyWriter("b");
                    writer.WriteBool(ok);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                case "SubscribeGroup":
                {
                    Reader reader = context.Request.GetBodyReader();
                    string groupId = reader.ReadString();
                    string[] peers = reader.ReadArrayOfString();
                    bool ok = await _host.SubscribeGroupAsync(new SubscribeGroupRequest(
                        new GroupId(groupId),
                        peers.Select(static p => new NodeId(p)).ToList())).ConfigureAwait(false);

                    using MessageWriter writer = context.CreateReplyWriter("b");
                    writer.WriteBool(ok);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                case "ConnectPeer":
                {
                    Reader reader = context.Request.GetBodyReader();
                    string targetNodeId = reader.ReadString();
                    string cachedTicket = reader.ReadString();
                    bool allowDht = reader.ReadBool();

                    bool ok = await _host.ConnectPeerAsync(
                        new NodeId(targetNodeId),
                        string.IsNullOrWhiteSpace(cachedTicket) ? null : cachedTicket,
                        allowDht).ConfigureAwait(false);

                    using MessageWriter writer = context.CreateReplyWriter("b");
                    writer.WriteBool(ok);
                    context.Reply(writer.CreateMessage());
                    break;
                }
            }
        }
    }
}
