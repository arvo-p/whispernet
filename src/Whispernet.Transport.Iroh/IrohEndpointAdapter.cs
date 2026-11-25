using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using N0.IrohNet;
using Whispernet.Core;

namespace Whispernet.Transport.Iroh;

/// <summary>
/// Adaptateur réseau Iroh QUIC encapsulant <see cref="iroh"/> (avec Mainline DHT + Pkarr/DNS + mDNS activés).
/// Gère un pool de connexions QUIC persistantes et la connexion intelligente (<c>ConnectSmartAsync</c>) :
/// essai rapide via ticket en cache, puis résolution automatique par <see cref="NodeId"/> seul via Mainline DHT.
/// </summary>
public sealed class IrohEndpointAdapter : IDisposable
{
    private readonly IntPtr _endpointPtr;
    private readonly string _alpn;
    private readonly ConcurrentDictionary<NodeId, (IntPtr ConnPtr, object SendLock)> _connections = new();
    private volatile bool _disposed;
    private volatile bool _acceptLoopStarted;

    public NodeId SelfId { get; }
    public string SelfTicket { get; }

    private IrohEndpointAdapter(IntPtr endpointPtr, string alpn, NodeId selfId, string selfTicket)
    {
        _endpointPtr = endpointPtr;
        _alpn = alpn;
        SelfId = selfId;
        SelfTicket = selfTicket;
    }

    /// <summary>
    /// Crée et lie un endpoint Iroh avec le mode de découverte choisi (par défaut <see cref="DiscoveryConfig.DISCOVERY_CONFIG_ALL"/> :
    /// Mainline DHT + Pkarr/DNS + mDNS ; passer <c>(DiscoveryConfig)4</c> pour Mainline DHT seule)
    /// et une clé secrète optionnelle (ex: persistée sur Raspberry Pi).
    /// </summary>
    public static unsafe IrohEndpointAdapter CreateAndBind(
        string alpn = "/whispernet/1",
        string? secretKeyBase32 = null,
        bool enableRelay = true,
        int onlineTimeoutMs = 3000,
        DiscoveryConfig discoveryConfig = DiscoveryConfig.DISCOVERY_CONFIG_ALL)
    {
        EndpointConfig config = iroh.endpoint_config_default();
        try
        {
            config.relay_mode = enableRelay ? RelayMode.RELAY_MODE_DEFAULT : RelayMode.RELAY_MODE_DISABLED;
            config.discovery_cfg = discoveryConfig;

            if (!string.IsNullOrWhiteSpace(secretKeyBase32))
            {
                SecretKey* secretKey = iroh.secret_key_default();
                byte[] keyBytes = Encoding.UTF8.GetBytes(secretKeyBase32 + "\0");
                fixed (byte* pKey = keyBytes)
                {
                    KeyResult keyRes = iroh.secret_key_from_base32(pKey, &secretKey);
                    if (keyRes != KeyResult.KEY_RESULT_OK)
                    {
                        iroh.secret_key_free(secretKey);
                        throw new ArgumentException($"Clé secrète Base32 invalide ({keyRes}).", nameof(secretKeyBase32));
                    }
                }
                iroh.endpoint_config_add_secret_key(&config, secretKey);
            }

            byte[] alpnBytes = Encoding.UTF8.GetBytes(alpn);
            fixed (byte* pAlpn = alpnBytes)
            {
                var slice = new slice_ref_uint8 { ptr = pAlpn, len = (nuint)alpnBytes.Length };
                iroh.endpoint_config_add_alpn(&config, slice);
            }

            Endpoint* ep = iroh.endpoint_default();
            EndpointResult bindRes = iroh.endpoint_bind(&config, null, null, &ep);
            if (bindRes != EndpointResult.ENDPOINT_RESULT_OK)
            {
                iroh.endpoint_free(ep);
                throw new InvalidOperationException($"Échec du bind Iroh ({bindRes}).");
            }

            if (onlineTimeoutMs > 0)
            {
                iroh.endpoint_online(&ep, (ulong)onlineTimeoutMs);
            }

            var (ticket, endpointId) = ReadSelfIdentity(ep);
            return new IrohEndpointAdapter((IntPtr)ep, alpn, new NodeId(endpointId), ticket);
        }
        finally
        {
            iroh.endpoint_config_free(config);
        }
    }

    /// <summary>
    /// Connexion intelligente exploitant la Mainline DHT :
    /// 1. Réutilise la connexion QUIC du pool si elle est déjà ouverte.
    /// 2. Tente une connexion rapide si <paramref name="cachedTicket"/> est fourni.
    /// 3. En cas d'échec ou d'absence de ticket (et si <paramref name="allowDhtLookup"/> est vrai),
    ///    résout l'adresse actuelle du pair à partir de son <see cref="NodeId"/> seul via Mainline DHT / Pkarr / mDNS.
    /// </summary>
    public Task<bool> ConnectSmartAsync(
        NodeId target,
        string? cachedTicket = null,
        bool allowDhtLookup = true,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connections.ContainsKey(target))
        {
            return Task.FromResult(true);
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ConnectSmartSync(target, cachedTicket, allowDhtLookup);
        }, ct);
    }

    private unsafe bool ConnectSmartSync(NodeId target, string? cachedTicket, bool allowDhtLookup)
    {
        if (!string.IsNullOrWhiteSpace(cachedTicket))
        {
            if (TryConnectWithTicket(cachedTicket, out Connection* ticketConn))
            {
                _connections[target] = ((IntPtr)ticketConn, new object());
                return true;
            }
        }

        if (allowDhtLookup && TryConnectByNodeIdViaDht(target, out Connection* dhtConn))
        {
            _connections[target] = ((IntPtr)dhtConn, new object());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Envoie un payload binaire sur un stream QUIC unidirectionnel vers <paramref name="target"/>
    /// (en résolvant automatiquement via Mainline DHT si aucune connexion n'est ouverte).
    /// </summary>
    public async Task SendUniAsync(
        NodeId target,
        byte[] payload,
        string? cachedTicket = null,
        bool allowDhtLookup = true,
        CancellationToken ct = default)
    {
        bool connected = await ConnectSmartAsync(target, cachedTicket, allowDhtLookup, ct).ConfigureAwait(false);
        if (!connected || !_connections.TryGetValue(target, out var entry))
        {
            throw new InvalidOperationException($"Impossible de joindre le pair {target} (ticket et résolution Mainline DHT échoués).");
        }

        await Task.Run(() => SendUniSync(target, entry, payload), ct).ConfigureAwait(false);
    }

    private unsafe void SendUniSync(NodeId target, (IntPtr ConnPtr, object SendLock) entry, byte[] payload)
    {
        lock (entry.SendLock)
        {
            Connection* conn = (Connection*)entry.ConnPtr;
            SendStream* sendStream = iroh.send_stream_default();
            EndpointResult openRes = iroh.connection_open_uni(&conn, &sendStream);
            if (openRes != EndpointResult.ENDPOINT_RESULT_OK)
            {
                iroh.send_stream_free(sendStream);
                _connections.TryRemove(target, out _);
                throw new InvalidOperationException($"Échec d'ouverture de stream QUIC vers {target}: {openRes}");
            }

            fixed (byte* pPayload = payload)
            {
                var slice = new slice_ref_uint8 { ptr = pPayload, len = (nuint)payload.Length };
                EndpointResult writeRes = iroh.send_stream_write_timeout(&sendStream, slice, 5000);
                if (writeRes != EndpointResult.ENDPOINT_RESULT_OK)
                {
                    iroh.send_stream_free(sendStream);
                    throw new InvalidOperationException($"Échec d'écriture sur stream QUIC vers {target}: {writeRes}");
                }
            }

            EndpointResult finishRes = iroh.send_stream_finish(sendStream);
            if (finishRes != EndpointResult.ENDPOINT_RESULT_OK)
            {
                throw new InvalidOperationException($"Échec de finalisation de stream QUIC vers {target}: {finishRes}");
            }
        }
    }

    public unsafe void CloseConnection(NodeId target)
    {
        if (_connections.TryRemove(target, out var entry))
        {
            lock (entry.SendLock)
            {
                iroh.connection_close((Connection*)entry.ConnPtr);
            }
        }
    }

    /// <summary>
    /// Démarre une boucle d'acceptation en arrière-plan pour recevoir les streams QUIC unidirectionnels entrants.
    /// </summary>
    public void StartAcceptLoop(Action<byte[]> onPayloadReceived, CancellationToken ct = default)
    {
        _acceptLoopStarted = true;
        Thread acceptThread = new(() => AcceptLoopSync(onPayloadReceived, ct))
        {
            IsBackground = true,
            Name = $"IrohAccept-{SelfId.Value[..Math.Min(8, SelfId.Value.Length)]}"
        };
        acceptThread.Start();
    }

    private unsafe void AcceptLoopSync(Action<byte[]> onPayloadReceived, CancellationToken ct)
    {
        byte[] alpnBytes = Encoding.UTF8.GetBytes(_alpn);
        while (!_disposed && !ct.IsCancellationRequested)
        {
            Connection* conn = iroh.connection_default();
            EndpointResult res;
            fixed (byte* pAlpn = alpnBytes)
            {
                var slice = new slice_ref_uint8 { ptr = pAlpn, len = (nuint)alpnBytes.Length };
                Endpoint* ep = (Endpoint*)_endpointPtr;
                res = iroh.endpoint_accept(&ep, slice, &conn);
            }

            if (res == EndpointResult.ENDPOINT_RESULT_INCOMING_ERROR)
            {
                iroh.connection_free(conn);
                continue;
            }

            if (res != EndpointResult.ENDPOINT_RESULT_OK)
            {
                iroh.connection_free(conn);
                break;
            }

            IntPtr connPtr = (IntPtr)conn;
            Thread peerThread = new(() => ReadIncomingStreamsSync(connPtr, onPayloadReceived, ct))
            {
                IsBackground = true
            };
            peerThread.Start();
        }
    }

    private unsafe void ReadIncomingStreamsSync(IntPtr connPtr, Action<byte[]> onPayloadReceived, CancellationToken ct)
    {
        Connection* conn = (Connection*)connPtr;
        while (!_disposed && !ct.IsCancellationRequested)
        {
            RecvStream* recvStream = iroh.recv_stream_default();
            if (iroh.connection_accept_uni(&conn, &recvStream) != EndpointResult.ENDPOINT_RESULT_OK)
            {
                iroh.recv_stream_free(recvStream);
                break;
            }

            Vec_uint8 buf = iroh.rust_buffer_alloc(0);
            try
            {
                if (iroh.recv_stream_read_to_end_timeout(&recvStream, &buf, 256 * 1024, 10000) == EndpointResult.ENDPOINT_RESULT_OK
                    && buf.len > 0
                    && buf.ptr != null)
                {
                    byte[] managed = new byte[(int)buf.len];
                    Marshal.Copy((IntPtr)buf.ptr, managed, 0, managed.Length);
                    onPayloadReceived(managed);
                }
            }
            finally
            {
                iroh.rust_buffer_free(buf);
                iroh.recv_stream_free(recvStream);
            }
        }
    }

    private unsafe bool TryConnectWithTicket(string ticket, out Connection* conn)
    {
        conn = null;
        EndpointAddr addr = iroh.endpoint_addr_default();
        byte[] ticketBytes = Encoding.UTF8.GetBytes(ticket + "\0");
        fixed (byte* pTicket = ticketBytes)
        {
            if (iroh.endpoint_addr_from_string(pTicket, &addr) != AddrResult.ADDR_RESULT_OK)
            {
                iroh.endpoint_addr_free(addr);
                return false;
            }
        }

        return TryConnectWithEndpointAddr(addr, out conn);
    }

    private unsafe bool TryConnectByNodeIdViaDht(NodeId nodeId, out Connection* conn)
    {
        conn = null;
        PublicKey pubKey = iroh.public_key_default();
        byte[] idBytes = Encoding.UTF8.GetBytes(nodeId.Value + "\0");
        fixed (byte* pId = idBytes)
        {
            if (iroh.public_key_from_base32(pId, &pubKey) != KeyResult.KEY_RESULT_OK)
            {
                iroh.public_key_free(pubKey);
                return false;
            }
        }

        // EndpointAddr sans IP ni relais : force Iroh à résoudre via Mainline DHT + Pkarr/DNS + mDNS
        EndpointAddr dhtAddr = iroh.endpoint_addr_new(pubKey);
        return TryConnectWithEndpointAddr(dhtAddr, out conn);
    }

    private unsafe bool TryConnectWithEndpointAddr(EndpointAddr addr, out Connection* conn)
    {
        Connection* candidate = iroh.connection_default();
        byte[] alpnBytes = Encoding.UTF8.GetBytes(_alpn);
        fixed (byte* pAlpn = alpnBytes)
        {
            var slice = new slice_ref_uint8 { ptr = pAlpn, len = (nuint)alpnBytes.Length };
            Endpoint* ep = (Endpoint*)_endpointPtr;
            EndpointResult res = iroh.endpoint_connect(&ep, slice, addr, &candidate);
            if (res == EndpointResult.ENDPOINT_RESULT_OK)
            {
                conn = candidate;
                return true;
            }
        }

        iroh.connection_free(candidate);
        conn = null;
        return false;
    }

    private static unsafe (string Ticket, string EndpointId) ReadSelfIdentity(Endpoint* ep)
    {
        EndpointAddr addr = iroh.endpoint_addr_default();
        try
        {
            if (iroh.endpoint_addr(&ep, &addr) != EndpointResult.ENDPOINT_RESULT_OK)
            {
                throw new InvalidOperationException("Impossible de lire l'adresse locale de l'endpoint Iroh.");
            }

            byte* ticketPtr = iroh.endpoint_addr_as_str(&addr);
            string ticket = Marshal.PtrToStringUTF8((IntPtr)ticketPtr) ?? string.Empty;
            iroh.rust_free_string(ticketPtr);

            byte* idPtr = iroh.public_key_as_base32(&addr.id);
            string id = Marshal.PtrToStringUTF8((IntPtr)idPtr) ?? string.Empty;
            iroh.rust_free_string(idPtr);

            return (ticket, id);
        }
        finally
        {
            iroh.endpoint_addr_free(addr);
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var kvp in _connections)
        {
            if (_connections.TryRemove(kvp.Key, out var entry))
            {
                lock (entry.SendLock)
                {
                    iroh.connection_close((Connection*)entry.ConnPtr);
                }
            }
        }

        // iroh-c-ffi endpoint_accept holds an RwLock read guard on ep.ep while blocking;
        // calling endpoint_close (which awaits ep.ep.write()) would deadlock if AcceptLoop is running.
        if (!_acceptLoopStarted)
        {
            iroh.endpoint_close((Endpoint*)_endpointPtr);
        }
    }
}
