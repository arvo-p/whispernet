using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using N0.IrohNet;

namespace Whispernet;

public static unsafe class IrohHelpers
{
    public const string GroupChatAlpn = "/whispernet/1";

    public static Endpoint* CreateAndBindEndpoint(bool localOnly = false)
    {
        EndpointConfig config = iroh.endpoint_config_default();
        try
        {
            if (localOnly)
            {
                config.relay_mode = RelayMode.RELAY_MODE_DISABLED;
                config.discovery_cfg = DiscoveryConfig.DISCOVERY_CONFIG_NONE;
            }
            else
            {
                config.relay_mode = RelayMode.RELAY_MODE_DEFAULT;
                config.discovery_cfg = DiscoveryConfig.DISCOVERY_CONFIG_ALL;
            }

            byte[] alpnBytes = Encoding.UTF8.GetBytes(GroupChatAlpn);
            fixed (byte* pAlpn = alpnBytes)
            {
                var slice = new slice_ref_uint8
                {
                    ptr = pAlpn,
                    len = (nuint)alpnBytes.Length
                };
                iroh.endpoint_config_add_alpn(&config, slice);
            }

            Endpoint* ep = iroh.endpoint_default();
            EndpointResult res = iroh.endpoint_bind(&config, null, null, &ep);
            if (res != EndpointResult.ENDPOINT_RESULT_OK)
            {
                iroh.endpoint_free(ep);
                throw new InvalidOperationException($"Failed to bind Iroh endpoint: {res}");
            }

            if (!localOnly)
            {
                // Wait up to 3s for relay/direct address discovery (non-fatal if offline/LAN)
                iroh.endpoint_online(&ep, 3000);
            }

            return ep;
        }
        finally
        {
            iroh.endpoint_config_free(config);
        }
    }

    public static (string Ticket, string EndpointId) GetEndpointTicketAndId(Endpoint* ep)
    {
        EndpointAddr addr = iroh.endpoint_addr_default();
        try
        {
            EndpointResult res = iroh.endpoint_addr(&ep, &addr);
            if (res != EndpointResult.ENDPOINT_RESULT_OK)
            {
                throw new InvalidOperationException($"Failed to retrieve endpoint address: {res}");
            }

            byte* ticketPtr = iroh.endpoint_addr_as_str(&addr);
            string ticket = Marshal.PtrToStringUTF8((IntPtr)ticketPtr) ?? string.Empty;
            iroh.rust_free_string(ticketPtr);

            byte* idPtr = iroh.public_key_as_base32(&addr.id);
            string endpointId = Marshal.PtrToStringUTF8((IntPtr)idPtr) ?? string.Empty;
            iroh.rust_free_string(idPtr);

            return (ticket, endpointId);
        }
        finally
        {
            iroh.endpoint_addr_free(addr);
        }
    }

    public static Connection* ConnectToTicket(Endpoint* ep, string ticket)
    {
        EndpointAddr addr = iroh.endpoint_addr_default();
        byte[] ticketBytes = Encoding.UTF8.GetBytes(ticket + "\0");
        fixed (byte* pTicket = ticketBytes)
        {
            AddrResult addrRes = iroh.endpoint_addr_from_string(pTicket, &addr);
            if (addrRes != AddrResult.ADDR_RESULT_OK)
            {
                iroh.endpoint_addr_free(addr);
                throw new ArgumentException($"Invalid endpoint ticket ({addrRes})");
            }
        }

        Connection* conn = iroh.connection_default();
        byte[] alpnBytes = Encoding.UTF8.GetBytes(GroupChatAlpn);
        fixed (byte* pAlpn = alpnBytes)
        {
            var slice = new slice_ref_uint8
            {
                ptr = pAlpn,
                len = (nuint)alpnBytes.Length
            };
            // Note: endpoint_connect consumes `addr` by value in Rust FFI
            EndpointResult res = iroh.endpoint_connect(&ep, slice, addr, &conn);
            if (res != EndpointResult.ENDPOINT_RESULT_OK)
            {
                iroh.connection_free(conn);
                throw new InvalidOperationException($"Failed to connect to remote endpoint: {res}");
            }
        }

        return conn;
    }

    public static void SendMessageUni(Connection* conn, string message)
    {
        SendStream* sendStream = iroh.send_stream_default();
        EndpointResult openRes = iroh.connection_open_uni(&conn, &sendStream);
        if (openRes != EndpointResult.ENDPOINT_RESULT_OK)
        {
            iroh.send_stream_free(sendStream);
            throw new InvalidOperationException($"Failed to open unidirectional stream: {openRes}");
        }

        byte[] payload = Encoding.UTF8.GetBytes(message);
        fixed (byte* pPayload = payload)
        {
            var slice = new slice_ref_uint8
            {
                ptr = pPayload,
                len = (nuint)payload.Length
            };
            EndpointResult writeRes = iroh.send_stream_write_timeout(&sendStream, slice, 5000);
            if (writeRes != EndpointResult.ENDPOINT_RESULT_OK)
            {
                iroh.send_stream_free(sendStream);
                throw new InvalidOperationException($"Failed to write message to stream: {writeRes}");
            }
        }

        // send_stream_finish consumes sendStream
        EndpointResult finishRes = iroh.send_stream_finish(sendStream);
        if (finishRes != EndpointResult.ENDPOINT_RESULT_OK)
        {
            throw new InvalidOperationException($"Failed to finish stream: {finishRes}");
        }
    }

    public static string? ReceiveNextMessageUni(Connection* conn)
    {
        RecvStream* recvStream = iroh.recv_stream_default();
        EndpointResult acceptRes = iroh.connection_accept_uni(&conn, &recvStream);
        if (acceptRes != EndpointResult.ENDPOINT_RESULT_OK)
        {
            iroh.recv_stream_free(recvStream);
            return null;
        }

        Vec_uint8 buf = iroh.rust_buffer_alloc(0);
        try
        {
            EndpointResult readRes = iroh.recv_stream_read_to_end_timeout(&recvStream, &buf, 64 * 1024, 10000);
            if (readRes != EndpointResult.ENDPOINT_RESULT_OK)
            {
                return null;
            }

            if (buf.len == 0 || buf.ptr == null)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(buf.ptr, (int)buf.len);
        }
        finally
        {
            iroh.rust_buffer_free(buf);
            iroh.recv_stream_free(recvStream);
        }
    }
}

public sealed unsafe class GroupRoomHost : IDisposable
{
    private readonly Endpoint* _endpoint;
    private readonly ConcurrentDictionary<int, (string Name, IntPtr ConnPtr, object SendLock)> _peers = new();
    private int _nextPeerId;
    private volatile bool _running = true;

    public string HostName { get; }
    public string Ticket { get; }
    public string EndpointId { get; }
    public event Action<string>? OnLog;

    public GroupRoomHost(string hostName, bool localOnly = false)
    {
        HostName = hostName;
        _endpoint = IrohHelpers.CreateAndBindEndpoint(localOnly);
        (Ticket, EndpointId) = IrohHelpers.GetEndpointTicketAndId(_endpoint);
    }

    public void StartAcceptLoop()
    {
        Thread acceptThread = new(AcceptLoop)
        {
            IsBackground = true,
            Name = $"IrohHostAccept-{HostName}"
        };
        acceptThread.Start();
    }

    private void AcceptLoop()
    {
        byte[] alpnBytes = Encoding.UTF8.GetBytes(IrohHelpers.GroupChatAlpn);
        while (_running)
        {
            Connection* conn = iroh.connection_default();
            EndpointResult res;
            fixed (byte* pAlpn = alpnBytes)
            {
                var slice = new slice_ref_uint8
                {
                    ptr = pAlpn,
                    len = (nuint)alpnBytes.Length
                };
                Endpoint* ep = _endpoint;
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
                if (_running)
                {
                    OnLog?.Invoke($"[Host] Accept stopped ({res})");
                }
                break;
            }

            int peerId = Interlocked.Increment(ref _nextPeerId);
            IntPtr connPtr = (IntPtr)conn;
            _peers[peerId] = ($"Peer-{peerId}", connPtr, new object());

            Thread peerThread = new(() => HandlePeerReceiveLoop(peerId, connPtr))
            {
                IsBackground = true,
                Name = $"IrohHostPeer-{peerId}"
            };
            peerThread.Start();
        }
    }

    private void HandlePeerReceiveLoop(int peerId, IntPtr connPtr)
    {
        Connection* conn = (Connection*)connPtr;
        while (_running)
        {
            string? rawMessage = IrohHelpers.ReceiveNextMessageUni(conn);
            if (rawMessage == null)
            {
                break;
            }

            if (rawMessage.StartsWith("JOIN|", StringComparison.Ordinal))
            {
                string memberName = rawMessage["JOIN|".Length..].Trim();
                if (_peers.TryGetValue(peerId, out var existing))
                {
                    _peers[peerId] = (memberName, existing.ConnPtr, existing.SendLock);
                }

                string joinAnnouncement = $"[System] {memberName} joined the group.";
                OnLog?.Invoke(joinAnnouncement);
                BroadcastRaw($"MSG|{joinAnnouncement}");
            }
            else if (rawMessage.StartsWith("CHAT|", StringComparison.Ordinal))
            {
                // Format: CHAT|<sender>|<text>
                string rest = rawMessage["CHAT|".Length..];
                int sep = rest.IndexOf('|');
                if (sep > 0)
                {
                    string sender = rest[..sep];
                    string text = rest[(sep + 1)..];
                    string formatted = $"[{sender}]: {text}";
                    OnLog?.Invoke(formatted);
                    BroadcastRaw($"MSG|{formatted}");
                }
            }
            else if (rawMessage.StartsWith("LEAVE|", StringComparison.Ordinal))
            {
                break;
            }
        }

        if (_peers.TryRemove(peerId, out var removed))
        {
            lock (removed.SendLock)
            {
                // connection_close closes the QUIC connection (unblocking the member's
                // connection_accept_uni) and consumes/frees the Connection box in Rust
                iroh.connection_close((Connection*)removed.ConnPtr);
            }
            if (_running)
            {
                string leaveMsg = $"[System] {removed.Name} left the group.";
                OnLog?.Invoke(leaveMsg);
                BroadcastRaw($"MSG|{leaveMsg}");
            }
        }
    }

    public void SendHostMessage(string text)
    {
        string formatted = $"[{HostName}]: {text}";
        OnLog?.Invoke(formatted);
        BroadcastRaw($"MSG|{formatted}");
    }

    private void BroadcastRaw(string payload)
    {
        foreach (var kvp in _peers)
        {
            var (_, connPtr, sendLock) = kvp.Value;
            try
            {
                lock (sendLock)
                {
                    if (_peers.ContainsKey(kvp.Key))
                    {
                        IrohHelpers.SendMessageUni((Connection*)connPtr, payload);
                    }
                }
            }
            catch
            {
                // Disconnected peer will be cleaned up by its receive loop
            }
        }
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        // Note: endpoint_accept is currently blocking on a background thread holding an
        // FFI reference to _endpoint; freeing/closing _endpoint while endpoint_accept is
        // blocked inside Rust FFI would invalidate that pointer.
    }
}

public sealed unsafe class GroupRoomMember : IDisposable
{
    private readonly Endpoint* _endpoint;
    private readonly Connection* _connection;
    private readonly object _sendLock = new();
    private Thread? _receiveThread;
    private volatile bool _running = true;

    public string MemberName { get; }
    public string EndpointId { get; }
    public event Action<string>? OnMessageReceived;

    public GroupRoomMember(string memberName, string hostTicket, bool localOnly = false)
    {
        MemberName = memberName;
        _endpoint = IrohHelpers.CreateAndBindEndpoint(localOnly);
        (_, EndpointId) = IrohHelpers.GetEndpointTicketAndId(_endpoint);
        _connection = IrohHelpers.ConnectToTicket(_endpoint, hostTicket);
    }

    public void Start()
    {
        _receiveThread = new(ReceiveLoop)
        {
            IsBackground = true,
            Name = $"IrohMemberRecv-{MemberName}"
        };
        _receiveThread.Start();

        lock (_sendLock)
        {
            IrohHelpers.SendMessageUni(_connection, $"JOIN|{MemberName}");
        }
    }

    public void SendChatMessage(string text)
    {
        lock (_sendLock)
        {
            IrohHelpers.SendMessageUni(_connection, $"CHAT|{MemberName}|{text}");
        }
    }

    public ulong GetRttMs()
    {
        Connection* conn = _connection;
        return iroh.connection_rtt(&conn);
    }

    private void ReceiveLoop()
    {
        while (_running)
        {
            string? raw = IrohHelpers.ReceiveNextMessageUni(_connection);
            if (raw == null)
            {
                break;
            }

            if (raw.StartsWith("MSG|", StringComparison.Ordinal))
            {
                OnMessageReceived?.Invoke(raw["MSG|".Length..]);
            }
        }
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        // Signal the peer that we are disconnecting, without freeing the Connection box
        // while ReceiveLoop is blocked inside connection_accept_uni(&conn)
        try
        {
            lock (_sendLock)
            {
                IrohHelpers.SendMessageUni(_connection, $"LEAVE|{MemberName}");
            }
        }
        catch
        {
            // Ignore if already closed
        }
        _receiveThread?.Join(2000);
        iroh.connection_free(_connection);
        iroh.endpoint_close(_endpoint);
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("demo", StringComparison.OrdinalIgnoreCase))
        {
            await RunAutomatedGroupDemoAsync();
            return 0;
        }

        if (args[0].Equals("test-all", StringComparison.OrdinalIgnoreCase))
        {
            return await InfrastructureTests.RunAllAsync();
        }

        string mode = args[0].ToLowerInvariant();
        if (mode == "host")
        {
            string name = args.Length >= 2 ? args[1] : "Host";
            RunInteractiveHost(name);
            return 0;
        }

        if (mode == "join")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: dotnet run -- join <YourName> <HostTicket>");
                return 1;
            }
            string name = args[1];
            string ticket = args[2];
            RunInteractiveMember(name, ticket);
            return 0;
        }

        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Iroh.NET P2P Group Messaging Test App");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- test-all                   # Run all unit, SQLite, and real Iroh/DHT tests");
        Console.WriteLine("  dotnet run -- demo                       # Run an automated 3-peer group chat test (Alice, Bob, Charlie)");
        Console.WriteLine("  dotnet run -- host <YourName>            # Host a group chat room and print the invite ticket");
        Console.WriteLine("  dotnet run -- join <YourName> <Ticket>   # Join an existing group chat room using its ticket");
    }

    private static async Task RunAutomatedGroupDemoAsync()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  N0.IrohNet P2P Group Messaging Automated Test (3 Peers)   ");
        Console.WriteLine("============================================================");

        var aliceLogs = new ConcurrentQueue<string>();
        var bobInbox = new ConcurrentQueue<string>();
        var charlieInbox = new ConcurrentQueue<string>();

        using var aliceHost = new GroupRoomHost("Alice", localOnly: true);
        aliceHost.OnLog += msg =>
        {
            aliceLogs.Enqueue(msg);
            Console.WriteLine($"  [Alice Host View]   {msg}");
        };
        aliceHost.StartAcceptLoop();

        Console.WriteLine($"[1/4] Alice created group room.");
        Console.WriteLine($"      Alice Endpoint ID : {aliceHost.EndpointId}");
        Console.WriteLine($"      Room Ticket       : {aliceHost.Ticket}");
        Console.WriteLine();

        Console.WriteLine("[2/4] Bob and Charlie are joining Alice's group room over Iroh QUIC...");
        using var bob = new GroupRoomMember("Bob", aliceHost.Ticket, localOnly: true);
        bob.OnMessageReceived += msg =>
        {
            bobInbox.Enqueue(msg);
            Console.WriteLine($"  [Bob Inbox]         {msg}");
        };
        bob.Start();

        using var charlie = new GroupRoomMember("Charlie", aliceHost.Ticket, localOnly: true);
        charlie.OnMessageReceived += msg =>
        {
            charlieInbox.Enqueue(msg);
            Console.WriteLine($"  [Charlie Inbox]     {msg}");
        };
        charlie.Start();

        await Task.Delay(500);
        Console.WriteLine($"      Bob Endpoint ID     : {bob.EndpointId} (RTT: {bob.GetRttMs()} ms)");
        Console.WriteLine($"      Charlie Endpoint ID : {charlie.EndpointId} (RTT: {charlie.GetRttMs()} ms)");
        Console.WriteLine();

        Console.WriteLine("[3/4] Exchanging group messages...");
        aliceHost.SendHostMessage("Welcome to the Iroh.NET P2P group chat, everyone!");
        await Task.Delay(250);

        bob.SendChatMessage("Hey Alice and Charlie! Loud and clear from Bob.");
        await Task.Delay(250);

        charlie.SendChatMessage("Hi Bob and Alice! Receiving messages from everyone.");
        await Task.Delay(500);

        Console.WriteLine();
        Console.WriteLine("[4/4] Verifying message delivery across all group participants...");
        bool bobGotAlice = bobInbox.Any(m => m.Contains("[Alice]: Welcome"));
        bool bobGotCharlie = bobInbox.Any(m => m.Contains("[Charlie]: Hi Bob and Alice"));
        bool charlieGotAlice = charlieInbox.Any(m => m.Contains("[Alice]: Welcome"));
        bool charlieGotBob = charlieInbox.Any(m => m.Contains("[Bob]: Hey Alice and Charlie"));
        bool aliceGotAll = aliceLogs.Any(m => m.Contains("[Bob]:")) && aliceLogs.Any(m => m.Contains("[Charlie]:"));

        Console.WriteLine($"      - Alice received all group messages : {aliceGotAll}");
        Console.WriteLine($"      - Bob received Alice & Charlie      : {bobGotAlice && bobGotCharlie}");
        Console.WriteLine($"      - Charlie received Alice & Bob      : {charlieGotAlice && charlieGotBob}");

        if (aliceGotAll && bobGotAlice && bobGotCharlie && charlieGotAlice && charlieGotBob)
        {
            Console.WriteLine("\nSUCCESS: Group messaging verified end-to-end over N0.IrohNet!");
        }
        else
        {
            throw new InvalidOperationException("One or more group messages were not delivered.");
        }
    }

    private static void RunInteractiveHost(string name)
    {
        Console.WriteLine($"Starting group chat room as '{name}'...");
        using var host = new GroupRoomHost(name, localOnly: false);
        host.OnLog += msg => Console.WriteLine(msg);
        host.StartAcceptLoop();

        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($"Endpoint ID : {host.EndpointId}");
        Console.WriteLine($"Invite other collaborators to join with this command:");
        Console.WriteLine($"  dotnet run -- join <TheirName> {host.Ticket}");
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine("Type a message and press Enter to broadcast (or '/quit' to exit):");

        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null || line.Trim().Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (!string.IsNullOrWhiteSpace(line))
            {
                host.SendHostMessage(line.Trim());
            }
        }
    }

    private static void RunInteractiveMember(string name, string ticket)
    {
        Console.WriteLine($"Connecting to group room as '{name}'...");
        using var member = new GroupRoomMember(name, ticket, localOnly: false);
        member.OnMessageReceived += msg => Console.WriteLine(msg);
        member.Start();

        Console.WriteLine($"Connected! (Endpoint ID: {member.EndpointId}, RTT: {member.GetRttMs()} ms)");
        Console.WriteLine("Type a message and press Enter to send to the group (or '/quit' to exit):");

        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null || line.Trim().Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (!string.IsNullOrWhiteSpace(line))
            {
                member.SendChatMessage(line.Trim());
            }
        }
    }
}
