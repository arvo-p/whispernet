using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Tmds.DBus.Protocol;
using Whispernet.Core;
using Whispernet.Daemon.Contracts;

namespace Whispernet.Daemon.Client;

/// <summary>
/// Client IPC multiplateforme implémentant <see cref="IMeshTransport"/> pour connecter
/// <c>Whispernet.App.Messaging</c> au démon d'arrière-plan <c>whispernetd</c> :
/// - Linux : D-Bus (<c>org.whispernet.Daemon</c> via <c>Tmds.DBus.Protocol</c>)
/// - Windows : Named Pipe (<c>\\.\pipe\whispernetd</c>)
/// </summary>
public sealed class DaemonMeshTransport : IMeshTransport
{
    private readonly string? _dbusAddress;
    private readonly string _windowsPipeName;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<DaemonIpcEnvelope>> _pendingPipeRequests = new();

    private DBusConnection? _dbusConn;
    private IDisposable? _incomingSub;
    private IDisposable? _peerSeenSub;
    private NamedPipeClientStream? _pipeStream;
    private CancellationTokenSource? _pipeReadCts;
    private Action<byte[]>? _onPayloadReceived;
    private volatile bool _disposed;

    public NodeId SelfId { get; private set; } = new(string.Empty);
    public string SelfTicket { get; private set; } = string.Empty;

    public event Action<DaemonPeerSeenDto>? PeerSeen;

    public DaemonMeshTransport(
        string? dbusAddress = null,
        string windowsPipeName = DaemonIpcConstants.WindowsPipeName)
    {
        _dbusAddress = dbusAddress;
        _windowsPipeName = windowsPipeName;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", _windowsPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, ct).ConfigureAwait(false);
            _pipeStream = pipe;
            _pipeReadCts = new CancellationTokenSource();
            _ = PipeReadLoopAsync(pipe, _pipeReadCts.Token);
        }
        else
        {
            string addr = string.IsNullOrWhiteSpace(_dbusAddress)
                ? (DBusAddress.Session ?? throw new InvalidOperationException("D-Bus session address is not available."))
                : _dbusAddress;
            var conn = new DBusConnection(addr);
            await conn.ConnectAsync().ConfigureAwait(false);
            _dbusConn = conn;

            _incomingSub = await conn.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = DaemonIpcConstants.DBusServiceName,
                    Path = DaemonIpcConstants.DBusObjectPath,
                    Interface = DaemonIpcConstants.DBusInterfaceName,
                    Member = "IncomingPacket"
                },
                (Message msg, object? _) =>
                {
                    Reader reader = msg.GetBodyReader();
                    byte[] payload = reader.ReadArrayOfByte();
                    return payload;
                },
                (Exception? ex, byte[] payload, object? _, object? __) =>
                {
                    if (ex is null && payload.Length > 0)
                    {
                        _onPayloadReceived?.Invoke(payload);
                    }
                },
                ObserverFlags.None).ConfigureAwait(false);

            _peerSeenSub = await conn.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = DaemonIpcConstants.DBusServiceName,
                    Path = DaemonIpcConstants.DBusObjectPath,
                    Interface = DaemonIpcConstants.DBusInterfaceName,
                    Member = "PeerSeen"
                },
                (Message msg, object? _) =>
                {
                    Reader reader = msg.GetBodyReader();
                    string nid = reader.ReadString();
                    int role = reader.ReadInt32();
                    long unixMs = reader.ReadInt64();
                    return new DaemonPeerSeenDto(new NodeId(nid), (NodeRole)role, DateTimeOffset.FromUnixTimeMilliseconds(unixMs));
                },
                (Exception? ex, DaemonPeerSeenDto dto, object? _, object? __) =>
                {
                    if (ex is null)
                    {
                        PeerSeen?.Invoke(dto);
                    }
                },
                ObserverFlags.None).ConfigureAwait(false);
        }

        DaemonStatusDto status = await GetStatusAsync(ct).ConfigureAwait(false);
        SelfId = new NodeId(status.SelfNodeId);
        SelfTicket = status.SelfTicket;
    }

    public async Task<DaemonStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        if (_pipeStream is not null)
        {
            DaemonIpcEnvelope resp = await SendPipeRequestAsync(
                new DaemonIpcEnvelope(DaemonIpcMessageKind.GetStatusRequest, Guid.NewGuid()), ct).ConfigureAwait(false);
            return resp.Status ?? throw new InvalidOperationException(resp.Error ?? "Empty daemon status.");
        }

        DBusConnection conn = _dbusConn ?? throw new InvalidOperationException("Daemon D-Bus connection is not open.");
        MessageBuffer msg = BuildDBusCall(conn, "GetStatus", null, static _ => { });
        return await conn.CallMethodAsync(msg, static (Message reply, object? _) =>
        {
            string json = reply.GetBodyReader().ReadString();
            return JsonSerializer.Deserialize<DaemonStatusDto>(json)!;
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<byte[]>> RegisterWatchTagsAsync(
        AuthorId authorId,
        IReadOnlyList<string> blindTagsHex,
        CancellationToken ct = default)
    {
        if (_pipeStream is not null)
        {
            DaemonIpcEnvelope resp = await SendPipeRequestAsync(
                new DaemonIpcEnvelope(
                    DaemonIpcMessageKind.RegisterWatchTagsRequest,
                    Guid.NewGuid(),
                    RegisterWatchTags: new RegisterWatchTagsRequest(authorId, blindTagsHex)),
                ct).ConfigureAwait(false);
            return resp.MatchedBundles ?? Array.Empty<byte[]>();
        }

        DBusConnection conn = _dbusConn ?? throw new InvalidOperationException("Daemon D-Bus connection is not open.");
        MessageBuffer msg = BuildDBusCall(conn, "RegisterWatchTags", "sas", writer =>
        {
            writer.WriteString(authorId.Value);
            writer.WriteArray(blindTagsHex.ToArray());
        });

        IReadOnlyList<byte[]> bundles = await conn.CallMethodAsync(msg, static (Message reply, object? _) =>
        {
            Reader reader = reply.GetBodyReader();
            ArrayEnd outer = reader.ReadArrayStart(DBusType.Array);
            var list = new List<byte[]>();
            while (reader.HasNext(outer))
            {
                list.Add(reader.ReadArrayOfByte());
            }
            return (IReadOnlyList<byte[]>)list;
        }).ConfigureAwait(false);

        foreach (byte[] pkt in bundles)
        {
            _onPayloadReceived?.Invoke(pkt);
        }

        return bundles;
    }

    public async Task SubscribeGroupAsync(GroupId groupId, IEnumerable<NodeId> knownPeers, CancellationToken ct = default)
    {
        List<NodeId> list = knownPeers.ToList();
        if (_pipeStream is not null)
        {
            await SendPipeRequestAsync(
                new DaemonIpcEnvelope(
                    DaemonIpcMessageKind.SubscribeGroupRequest,
                    Guid.NewGuid(),
                    SubscribeGroup: new SubscribeGroupRequest(groupId, list)),
                ct).ConfigureAwait(false);
            return;
        }

        DBusConnection conn = _dbusConn ?? throw new InvalidOperationException("Daemon D-Bus connection is not open.");
        MessageBuffer msg = BuildDBusCall(conn, "SubscribeGroup", "sas", writer =>
        {
            writer.WriteString(groupId.Value);
            writer.WriteArray(list.Select(static p => p.Value).ToArray());
        });
        await conn.CallMethodAsync(msg).ConfigureAwait(false);
    }

    public bool HasActiveConnection(NodeId target) => false;

    public async Task<bool> ConnectSmartAsync(
        NodeId target,
        string? cachedTicket = null,
        bool allowDhtLookup = true,
        CancellationToken ct = default)
    {
        if (_pipeStream is not null)
        {
            DaemonIpcEnvelope resp = await SendPipeRequestAsync(
                new DaemonIpcEnvelope(DaemonIpcMessageKind.ConnectPeerRequest, Guid.NewGuid(), ConnectTargetNodeId: target),
                ct).ConfigureAwait(false);
            return resp.Success;
        }

        DBusConnection conn = _dbusConn ?? throw new InvalidOperationException("Daemon D-Bus connection is not open.");
        MessageBuffer msg = BuildDBusCall(conn, "ConnectPeer", "ssb", writer =>
        {
            writer.WriteString(target.Value);
            writer.WriteString(cachedTicket ?? string.Empty);
            writer.WriteBool(allowDhtLookup);
        });
        return await conn.CallMethodAsync(msg, static (Message reply, object? _) => reply.GetBodyReader().ReadBool()).ConfigureAwait(false);
    }

    public async Task SendUniAsync(
        NodeId target,
        byte[] payload,
        string? cachedTicket = null,
        bool allowDhtLookup = true,
        CancellationToken ct = default)
    {
        if (_pipeStream is not null)
        {
            DaemonIpcEnvelope resp = await SendPipeRequestAsync(
                new DaemonIpcEnvelope(
                    DaemonIpcMessageKind.SendWirePacketRequest,
                    Guid.NewGuid(),
                    SendWirePacket: new SendWirePacketRequest(target, payload, cachedTicket, allowDhtLookup)),
                ct).ConfigureAwait(false);
            if (!resp.Success)
            {
                throw new InvalidOperationException(resp.Error ?? "Daemon SendWirePacket failed.");
            }
            return;
        }

        DBusConnection conn = _dbusConn ?? throw new InvalidOperationException("Daemon D-Bus connection is not open.");
        MessageBuffer sendMsg = BuildDBusCall(conn, "SendWirePacket", "saysb", writer =>
        {
            writer.WriteString(target.Value);
            writer.WriteArray(payload);
            writer.WriteString(cachedTicket ?? string.Empty);
            writer.WriteBool(allowDhtLookup);
        });
        bool ok = await conn.CallMethodAsync(sendMsg, static (Message reply, object? _) => reply.GetBodyReader().ReadBool()).ConfigureAwait(false);
        if (!ok)
        {
            throw new InvalidOperationException($"Daemon SendWirePacket to {target} failed.");
        }
    }

    public async Task<bool> RequestShutdownAsync(CancellationToken ct = default)
    {
        if (_pipeStream is not null)
        {
            DaemonIpcEnvelope resp = await SendPipeRequestAsync(
                new DaemonIpcEnvelope(DaemonIpcMessageKind.ShutdownRequest, Guid.NewGuid()),
                ct).ConfigureAwait(false);
            return resp.Success;
        }
        return false;
    }

    public void CloseConnection(NodeId target)
    {
        // Géré par le démon partagé
    }

    public void StartAcceptLoop(Action<byte[]> onPayloadReceived, CancellationToken ct = default)
    {
        _onPayloadReceived = onPayloadReceived;
    }

    private static MessageBuffer BuildDBusCall(DBusConnection conn, string member, string? signature, Action<MessageWriter> writeBody)
    {
        using MessageWriter writer = conn.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: DaemonIpcConstants.DBusServiceName,
            path: DaemonIpcConstants.DBusObjectPath,
            @interface: DaemonIpcConstants.DBusInterfaceName,
            member: member,
            signature: signature);
        writeBody(writer);
        return writer.CreateMessage();
    }

    private async Task<DaemonIpcEnvelope> SendPipeRequestAsync(DaemonIpcEnvelope req, CancellationToken ct)
    {
        NamedPipeClientStream pipe = _pipeStream ?? throw new InvalidOperationException("Windows Named Pipe is not connected.");
        var tcs = new TaskCompletionSource<DaemonIpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPipeRequests[req.RequestId] = tcs;
        try
        {
            await DaemonIpcFraming.WriteFrameAsync(pipe, req, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingPipeRequests.TryRemove(req.RequestId, out _);
        }
    }

    private async Task PipeReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                DaemonIpcEnvelope? env = await DaemonIpcFraming.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
                if (env is null)
                {
                    break;
                }

                if (env.Kind == DaemonIpcMessageKind.IncomingPacketEvent && env.IncomingPacket is not null)
                {
                    _onPayloadReceived?.Invoke(env.IncomingPacket.SerializedPacket);
                }
                else if (env.Kind == DaemonIpcMessageKind.PeerSeenEvent && env.PeerSeen is not null)
                {
                    PeerSeen?.Invoke(env.PeerSeen);
                }
                else if (env.RequestId != Guid.Empty && _pendingPipeRequests.TryRemove(env.RequestId, out var tcs))
                {
                    tcs.TrySetResult(env);
                }
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _incomingSub?.Dispose();
        _peerSeenSub?.Dispose();
        _dbusConn?.Dispose();
        _pipeReadCts?.Cancel();
        _pipeReadCts?.Dispose();
        _pipeStream?.Dispose();
    }
}
