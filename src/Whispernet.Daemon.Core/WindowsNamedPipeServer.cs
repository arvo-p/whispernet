using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Whispernet.Core;
using Whispernet.Daemon.Contracts;

namespace Whispernet.Daemon.Core;

/// <summary>
/// Serveur IPC Windows basé sur un Named Pipe (<c>\\.\pipe\whispernetd</c>) avec ACL explicite
/// permettant aux applications du bureau utilisateur (Tray Companion, WPF) de communiquer
/// avec le service Windows s'exécutant en Session 0.
/// </summary>
public sealed class WindowsNamedPipeServer : IAsyncDisposable
{
    private readonly WhispernetDaemonHost _host;
    private readonly List<Stream> _clients = new();
    private readonly object _clientsLock = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public WindowsNamedPipeServer(WhispernetDaemonHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _host.IncomingPacketAvailable += OnIncomingPacket;
        _host.PeerSeenAvailable += OnPeerSeen;
    }

    [SupportedOSPlatform("windows")]
    public void Start(string pipeName = DaemonIpcConstants.WindowsPipeName, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptTask = AcceptLoopAsync(pipeName, _cts.Token);
    }

    [SupportedOSPlatform("windows")]
    private async Task AcceptLoopAsync(string pipeName, CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                security);

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(Stream clientStream, CancellationToken ct)
    {
        lock (_clientsLock)
        {
            _clients.Add(clientStream);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                DaemonIpcEnvelope? req = await DaemonIpcFraming.ReadFrameAsync(clientStream, ct).ConfigureAwait(false);
                if (req is null)
                {
                    break;
                }

                DaemonIpcEnvelope resp = await DispatchRequestAsync(req, ct).ConfigureAwait(false);
                await DaemonIpcFraming.WriteFrameAsync(clientStream, resp, ct).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(clientStream);
            }
            await clientStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<DaemonIpcEnvelope> DispatchRequestAsync(DaemonIpcEnvelope req, CancellationToken ct)
    {
        try
        {
            switch (req.Kind)
            {
                case DaemonIpcMessageKind.GetStatusRequest:
                {
                    DaemonStatusDto status = await _host.GetStatusAsync(ct).ConfigureAwait(false);
                    return new DaemonIpcEnvelope(DaemonIpcMessageKind.GetStatusResponse, req.RequestId, true, Status: status);
                }

                case DaemonIpcMessageKind.RegisterWatchTagsRequest when req.RegisterWatchTags is not null:
                {
                    IReadOnlyList<byte[]> matched = await _host.RegisterWatchTagsAsync(req.RegisterWatchTags, ct).ConfigureAwait(false);
                    return new DaemonIpcEnvelope(DaemonIpcMessageKind.RegisterWatchTagsResponse, req.RequestId, true, MatchedBundles: matched);
                }

                case DaemonIpcMessageKind.SendWirePacketRequest when req.SendWirePacket is not null:
                {
                    bool ok = await _host.SendWirePacketAsync(req.SendWirePacket, ct).ConfigureAwait(false);
                    return new DaemonIpcEnvelope(DaemonIpcMessageKind.SendWirePacketResponse, req.RequestId, ok);
                }

                case DaemonIpcMessageKind.SubscribeGroupRequest when req.SubscribeGroup is not null:
                {
                    bool ok = await _host.SubscribeGroupAsync(req.SubscribeGroup, ct).ConfigureAwait(false);
                    return new DaemonIpcEnvelope(DaemonIpcMessageKind.SubscribeGroupResponse, req.RequestId, ok);
                }

                case DaemonIpcMessageKind.ConnectPeerRequest when req.ConnectTargetNodeId is NodeId nid:
                {
                    bool ok = await _host.ConnectPeerAsync(nid, ct: ct).ConfigureAwait(false);
                    return new DaemonIpcEnvelope(DaemonIpcMessageKind.ConnectPeerResponse, req.RequestId, ok);
                }

                case DaemonIpcMessageKind.ShutdownRequest:
                {
                    _host.RequestShutdown();
                    return new DaemonIpcEnvelope(DaemonIpcMessageKind.ShutdownResponse, req.RequestId, true);
                }

                default:
                    return new DaemonIpcEnvelope(req.Kind, req.RequestId, false, Error: "Unsupported IPC request.");
            }
        }
        catch (Exception ex)
        {
            return new DaemonIpcEnvelope(req.Kind, req.RequestId, false, Error: ex.Message);
        }
    }

    private void OnIncomingPacket(DaemonIncomingPacketDto dto)
        => BroadcastEvent(new DaemonIpcEnvelope(DaemonIpcMessageKind.IncomingPacketEvent, Guid.Empty, true, IncomingPacket: dto));

    private void OnPeerSeen(DaemonPeerSeenDto dto)
        => BroadcastEvent(new DaemonIpcEnvelope(DaemonIpcMessageKind.PeerSeenEvent, Guid.Empty, true, PeerSeen: dto));

    private void BroadcastEvent(DaemonIpcEnvelope evt)
    {
        List<Stream> snapshot;
        lock (_clientsLock)
        {
            if (_clients.Count == 0)
            {
                return;
            }
            snapshot = _clients.ToList();
        }

        foreach (Stream client in snapshot)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await DaemonIpcFraming.WriteFrameAsync(client, evt, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _host.IncomingPacketAvailable -= OnIncomingPacket;
        _host.PeerSeenAvailable -= OnPeerSeen;
        _cts?.Cancel();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
    }
}
