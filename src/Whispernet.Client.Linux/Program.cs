using System.Security.Cryptography;
using System.Text;
using Whispernet.App.Messaging;
using Whispernet.Core;
using Whispernet.Transport.Iroh;

namespace Whispernet.Client.Linux;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? rootDir = ParseRootOption(args);
        string effectiveRoot = string.IsNullOrWhiteSpace(rootDir)
            ? WhispernetPaths.GetDataDirectory()
            : Path.GetFullPath(rootDir);

        var accountService = new AccountService(effectiveRoot);

        if (args.Contains("--tui-demo", StringComparer.OrdinalIgnoreCase))
        {
            return await RunTuiDemoAsync(effectiveRoot, accountService).ConfigureAwait(false);
        }

        if (args.Contains("--sim-dag", StringComparer.OrdinalIgnoreCase))
        {
            return await RunSimDagDemoAsync(effectiveRoot, accountService).ConfigureAwait(false);
        }

        if (args.Contains("--mod-demo", StringComparer.OrdinalIgnoreCase))
        {
            return await RunModQueueDemoAsync(effectiveRoot, accountService).ConfigureAwait(false);
        }

        if (args.Contains("--test-reopen", StringComparer.OrdinalIgnoreCase))
        {
            return await RunReopenAutomationTestAsync(args).ConfigureAwait(false);
        }

        bool forceCli = args.Contains("--cli", StringComparer.OrdinalIgnoreCase);
        bool forceTui = args.Contains("--tui", StringComparer.OrdinalIgnoreCase);
        if (forceTui || (!forceCli && !Console.IsInputRedirected))
        {
            var tui = new NcursesTui(effectiveRoot, accountService);
            await tui.RunAsync().ConfigureAwait(false);
            return 0;
        }

        AccountSession? activeSession = null;
        SqlitePeerCacheRepository? peerCache = null;
        IrohEndpointAdapter? transport = null;
        GroupMeshService? directMesh = null;
        CancellationTokenSource? meshCts = null;

        Console.WriteLine("Whispernet.App.Messaging Interactive CLI");
        Console.WriteLine($"Data root: {effectiveRoot}");
        Console.WriteLine("Type 'help' for available commands, 'exit' to quit.");

        try
        {
            while (true)
            {
                string promptPrefix = activeSession is null
                    ? "whispernet"
                    : $"whispernet:{activeSession.Profile.Username}@{activeSession.Profile.AuthorId.Value[..8]}";
                Console.Write($"{promptPrefix}> ");

                string? line = Console.ReadLine();
                if (line is null)
                {
                    break;
                }

                List<string> tokens = SplitArgs(line);
                if (tokens.Count == 0)
                {
                    continue;
                }

                string cmd = tokens[0].ToLowerInvariant();
                if (cmd is "exit" or "quit")
                {
                    break;
                }

                try
                {
                    switch (cmd)
                    {
                        case "help":
                            PrintHelp();
                            break;

                        case "tui":
                        {
                            var tui = new NcursesTui(effectiveRoot, accountService);
                            await tui.RunAsync().ConfigureAwait(false);
                            break;
                        }

                        case "paths":
                            Console.WriteLine($"Default OS root : {WhispernetPaths.GetDataDirectory()}");
                            Console.WriteLine($"Active root     : {effectiveRoot}");
                            Console.WriteLine($"Accounts dir    : {WhispernetPaths.GetAccountsDirectory(effectiveRoot)}");
                            Console.WriteLine($"PeerCache DB    : {WhispernetPaths.GetPeerCacheDbPath(effectiveRoot)}");
                            break;

                        case "account":
                            activeSession = await HandleAccountCommandAsync(
                                tokens, accountService, activeSession).ConfigureAwait(false);
                            break;

                        case "vault":
                            await HandleVaultCommandAsync(
                                tokens, accountService, activeSession).ConfigureAwait(false);
                            break;

                        case "cache":
                            peerCache = await HandleCacheCommandAsync(
                                tokens, effectiveRoot, peerCache).ConfigureAwait(false);
                            break;

                        case "mesh":
                            (transport, directMesh, meshCts) = await HandleMeshCommandAsync(
                                tokens, activeSession, peerCache, transport, directMesh, meshCts).ConfigureAwait(false);
                            break;

                        default:
                            Console.WriteLine($"Unknown command '{tokens[0]}'. Type 'help' for usage.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            activeSession?.Dispose();
            StopMesh(ref transport, ref directMesh, ref meshCts);
        }

        return 0;
    }

    private static async Task<AccountSession?> HandleAccountCommandAsync(
        List<string> tokens,
        IAccountService accountService,
        AccountSession? activeSession)
    {
        if (tokens.Count < 2)
        {
            Console.WriteLine("Usage: account <create|list|unlock|lock|status|sign|verify> ...");
            return activeSession;
        }

        string sub = tokens[1].ToLowerInvariant();
        switch (sub)
        {
            case "create":
            {
                if (tokens.Count < 3)
                {
                    Console.WriteLine("Usage: account create <username> [--no-password]");
                    return activeSession;
                }

                string username = tokens[2];
                bool noPassword = tokens.Any(t => t.Equals("--no-password", StringComparison.OrdinalIgnoreCase));
                string? password = noPassword ? null : ReadPassword("Optional password (leave empty for none): ");
                if (string.IsNullOrEmpty(password))
                {
                    password = null;
                }

                AccountProfile profile = await accountService.CreateAccountAsync(username, password).ConfigureAwait(false);
                Console.WriteLine("Account created:");
                Console.WriteLine($"  Username          : {profile.Username}");
                Console.WriteLine($"  AuthorId          : {profile.AuthorId}");
                Console.WriteLine($"  PasswordProtected : {profile.IsPasswordProtected}");
                Console.WriteLine($"  PublicKey (SPKI)  : {profile.PublicKeySpkiBase64}");
                return activeSession;
            }

            case "list":
            {
                IReadOnlyList<AccountProfile> accounts = await accountService.ListAccountsAsync().ConfigureAwait(false);
                if (accounts.Count == 0)
                {
                    Console.WriteLine("No local accounts found.");
                    return activeSession;
                }

                foreach (AccountProfile acc in accounts)
                {
                    string marker = activeSession?.Profile.AuthorId == acc.AuthorId ? "*" : " ";
                    Console.WriteLine($"{marker} {acc.AuthorId} | {acc.Username} | protected={acc.IsPasswordProtected} | created={acc.CreatedAtUtc:O}");
                }
                return activeSession;
            }

            case "unlock":
            {
                if (tokens.Count < 3)
                {
                    Console.WriteLine("Usage: account unlock <username|authorId> [password]");
                    return activeSession;
                }

                string query = tokens[2];
                string? inlinePassword = tokens.Count >= 4 && !tokens[3].Equals("--no-password", StringComparison.OrdinalIgnoreCase)
                    ? tokens[3]
                    : null;
                IReadOnlyList<AccountProfile> accounts = await accountService.ListAccountsAsync().ConfigureAwait(false);

                List<AccountProfile> matches = accounts
                    .Where(a =>
                        a.AuthorId.Value.Equals(query, StringComparison.Ordinal) ||
                        a.Username.Equals(query, StringComparison.Ordinal))
                    .ToList();

                if (matches.Count > 1 && string.IsNullOrEmpty(inlinePassword))
                {
                    AccountProfile? latestPasswordless = matches
                        .Where(a => !a.IsPasswordProtected)
                        .OrderByDescending(a => a.CreatedAtUtc)
                        .FirstOrDefault();
                    if (latestPasswordless is not null)
                    {
                        matches = new List<AccountProfile> { latestPasswordless };
                    }
                }

                if (matches.Count == 0)
                {
                    Console.WriteLine($"No local account found matching '{query}'.");
                    return activeSession;
                }

                if (matches.Count > 1)
                {
                    Console.WriteLine($"Multiple accounts match '{query}'. Please specify the exact AuthorId:");
                    foreach (AccountProfile m in matches)
                    {
                        Console.WriteLine($"  {m.AuthorId} | {m.Username} | protected={m.IsPasswordProtected} | created={m.CreatedAtUtc:O}");
                    }
                    return activeSession;
                }

                AccountProfile target = matches[0];
                string? password = !target.IsPasswordProtected
                    ? null
                    : (inlinePassword ?? ReadPassword("Password: "));

                AccountSession newSession = await accountService.UnlockAccountAsync(target.AuthorId, password).ConfigureAwait(false);
                activeSession?.Dispose();
                Console.WriteLine($"Unlocked account '{newSession.Profile.Username}' ({newSession.Profile.AuthorId}).");
                Console.WriteLine($"  Folder encrypted: {newSession.IsFolderEncrypted}");
                return newSession;
            }

            case "lock":
            {
                if (activeSession is null)
                {
                    Console.WriteLine("No active account session.");
                    return null;
                }

                activeSession.Dispose();
                Console.WriteLine("Account session locked and zeroed.");
                return null;
            }

            case "status":
            {
                if (activeSession is null)
                {
                    Console.WriteLine("Status: locked (no active session).");
                }
                else
                {
                    Console.WriteLine($"Status          : unlocked");
                    Console.WriteLine($"Username        : {activeSession.Profile.Username}");
                    Console.WriteLine($"AuthorId        : {activeSession.Profile.AuthorId}");
                    Console.WriteLine($"FolderEncrypted : {activeSession.IsFolderEncrypted}");
                    Console.WriteLine($"Directory       : {activeSession.AccountDirectory}");
                }
                return activeSession;
            }

            case "sign":
            {
                if (activeSession is null)
                {
                    Console.WriteLine("Unlock an account first ('account unlock <authorId>').");
                    return activeSession;
                }

                if (tokens.Count < 3)
                {
                    Console.WriteLine("Usage: account sign <text>");
                    return activeSession;
                }

                string text = string.Join(' ', tokens.Skip(2));
                byte[] sig = accountService.SignData(activeSession, Encoding.UTF8.GetBytes(text));
                Console.WriteLine($"Signature (Base64): {Convert.ToBase64String(sig)}");
                return activeSession;
            }

            case "verify":
            {
                if (tokens.Count < 5)
                {
                    Console.WriteLine("Usage: account verify <authorId|pubKeyBase64> <sigBase64> <text>");
                    return activeSession;
                }

                string keyOrAuthor = tokens[2];
                byte[] sig = Convert.FromBase64String(tokens[3]);
                string text = string.Join(' ', tokens.Skip(4));

                byte[] pubKeySpki;
                var maybeAuthorId = new AuthorId(keyOrAuthor);
                if (maybeAuthorId.IsValid)
                {
                    IReadOnlyList<AccountProfile> accounts = await accountService.ListAccountsAsync().ConfigureAwait(false);
                    AccountProfile? acc = accounts.FirstOrDefault(a => a.AuthorId == maybeAuthorId);
                    if (acc is null)
                    {
                        Console.WriteLine($"No local account found for AuthorId {maybeAuthorId}.");
                        return activeSession;
                    }
                    pubKeySpki = Convert.FromBase64String(acc.PublicKeySpkiBase64);
                }
                else
                {
                    pubKeySpki = Convert.FromBase64String(keyOrAuthor);
                }

                bool valid = accountService.VerifySignature(pubKeySpki, Encoding.UTF8.GetBytes(text), sig);
                Console.WriteLine($"Signature valid: {valid}");
                return activeSession;
            }

            default:
                Console.WriteLine($"Unknown account subcommand '{sub}'.");
                return activeSession;
        }
    }

    private static async Task HandleVaultCommandAsync(
        List<string> tokens,
        IAccountService accountService,
        AccountSession? activeSession)
    {
        if (activeSession is null)
        {
            Console.WriteLine("Unlock an account first ('account unlock <authorId>').");
            return;
        }

        if (tokens.Count < 2)
        {
            Console.WriteLine("Usage: vault <write|read|list> ...");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        switch (sub)
        {
            case "write":
            {
                if (tokens.Count < 4)
                {
                    Console.WriteLine("Usage: vault write <relativePath> <text>");
                    return;
                }

                string relPath = tokens[2];
                string content = string.Join(' ', tokens.Skip(3));
                await accountService.WriteVaultFileAsync(
                    activeSession,
                    relPath,
                    Encoding.UTF8.GetBytes(content)).ConfigureAwait(false);
                Console.WriteLine($"Saved '{relPath}' (encrypted={activeSession.IsFolderEncrypted}).");
                break;
            }

            case "read":
            {
                if (tokens.Count < 3)
                {
                    Console.WriteLine("Usage: vault read <relativePath>");
                    return;
                }

                string relPath = tokens[2];
                byte[] data = await accountService.ReadVaultFileAsync(activeSession, relPath).ConfigureAwait(false);
                Console.WriteLine(Encoding.UTF8.GetString(data));
                break;
            }

            case "list":
            {
                string vaultDir = Path.Combine(activeSession.AccountDirectory, "vault");
                if (!Directory.Exists(vaultDir))
                {
                    Console.WriteLine("Vault directory is empty.");
                    return;
                }

                string[] files = Directory.GetFiles(vaultDir, "*", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    Console.WriteLine("Vault directory is empty.");
                    return;
                }

                foreach (string file in files)
                {
                    Console.WriteLine($"  {Path.GetRelativePath(vaultDir, file).Replace('\\', '/')}");
                }
                break;
            }

            default:
                Console.WriteLine($"Unknown vault subcommand '{sub}'.");
                break;
        }
    }

    private static async Task<SqlitePeerCacheRepository?> HandleCacheCommandAsync(
        List<string> tokens,
        string effectiveRoot,
        SqlitePeerCacheRepository? peerCache)
    {
        if (tokens.Count < 2)
        {
            Console.WriteLine("Usage: cache <init|list|upsert|prune>");
            return peerCache;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "init" || peerCache is null)
        {
            Directory.CreateDirectory(effectiveRoot);
            string dbPath = WhispernetPaths.GetPeerCacheDbPath(effectiveRoot);
            peerCache = new SqlitePeerCacheRepository(dbPath);
            await peerCache.InitializeAsync().ConfigureAwait(false);
            if (sub == "init")
            {
                Console.WriteLine($"PeerCache initialized at {dbPath}");
                return peerCache;
            }
        }

        switch (sub)
        {
            case "list":
            {
                int limit = tokens.Count >= 3 && int.TryParse(tokens[2], out int parsed) ? parsed : 50;
                IReadOnlyList<CachedPeerEntry> entries = await peerCache.LoadCachedPeersAsync(limit).ConfigureAwait(false);
                if (entries.Count == 0)
                {
                    Console.WriteLine("PeerCache is empty.");
                    return peerCache;
                }

                foreach (CachedPeerEntry e in entries)
                {
                    Console.WriteLine($"  {e.NodeId} | role={e.Role} | ok={e.SuccessCount} | fail={e.ConsecutiveFailures} | seen={e.LastSeenUtc:O}");
                }
                return peerCache;
            }

            case "upsert":
            {
                if (tokens.Count < 3)
                {
                    Console.WriteLine("Usage: cache upsert <nodeId> [Peer|MeetingPoint] [ticket]");
                    return peerCache;
                }

                var nodeId = new NodeId(tokens[2]);
                NodeRole role = tokens.Count >= 4 && Enum.TryParse(tokens[3], ignoreCase: true, out NodeRole parsedRole)
                    ? parsedRole
                    : NodeRole.Peer;
                string? ticket = tokens.Count >= 5 ? tokens[4] : null;

                var entry = new CachedPeerEntry(nodeId, role, ticket, DateTimeOffset.UtcNow, SuccessCount: 1);
                await peerCache.ApplyCacheUpdateAsync(new[] { entry }, Array.Empty<NodeId>()).ConfigureAwait(false);
                Console.WriteLine($"Upserted peer {nodeId} (role={role}).");
                return peerCache;
            }

            case "prune":
            {
                int days = tokens.Count >= 3 && int.TryParse(tokens[2], out int d) ? d : 7;
                int maxFail = tokens.Count >= 4 && int.TryParse(tokens[3], out int f) ? f : 5;
                int pruned = await peerCache.PruneExpiredAndFailedPeersAsync(
                    DateTimeOffset.UtcNow, TimeSpan.FromDays(days), maxFail).ConfigureAwait(false);
                Console.WriteLine($"Pruned {pruned} peer(s).");
                return peerCache;
            }

            default:
                Console.WriteLine($"Unknown cache subcommand '{sub}'.");
                return peerCache;
        }
    }

    private static async Task<(IrohEndpointAdapter? Transport, GroupMeshService? Mesh, CancellationTokenSource? Cts)> HandleMeshCommandAsync(
        List<string> tokens,
        AccountSession? activeSession,
        SqlitePeerCacheRepository? peerCache,
        IrohEndpointAdapter? transport,
        GroupMeshService? directMesh,
        CancellationTokenSource? meshCts)
    {
        if (tokens.Count < 2)
        {
            Console.WriteLine("Usage: mesh <start|info|conv|send|stop>");
            return (transport, directMesh, meshCts);
        }

        string sub = tokens[1].ToLowerInvariant();
        switch (sub)
        {
            case "start":
            {
                if (transport is not null)
                {
                    Console.WriteLine($"Mesh already running (SelfId={transport.SelfId}).");
                    return (transport, directMesh, meshCts);
                }

                string alpn = tokens.Count >= 3 ? tokens[2] : "/whispernet/direct/1";
                transport = IrohEndpointAdapter.CreateAndBind(alpn: alpn, enableRelay: true, onlineTimeoutMs: 3000);
                directMesh = new GroupMeshService(transport, peerCache);
                directMesh.MessageReceived += msg =>
                {
                    Console.WriteLine();
                    Console.WriteLine($"[INCOMING] [{msg.ConversationId}] {msg.SenderName} ({msg.SenderId}): {msg.Content}");
                };

                meshCts = new CancellationTokenSource();
                transport.StartAcceptLoop(directMesh.HandleIncomingPayload, meshCts.Token);

                Console.WriteLine("Mesh started:");
                Console.WriteLine($"  SelfId : {transport.SelfId}");
                Console.WriteLine($"  Ticket : {transport.SelfTicket}");
                return (transport, directMesh, meshCts);
            }

            case "info":
            {
                if (transport is null)
                {
                    Console.WriteLine("Mesh is not running. Use 'mesh start' first.");
                    return (transport, directMesh, meshCts);
                }

                Console.WriteLine($"SelfId : {transport.SelfId}");
                Console.WriteLine($"Ticket : {transport.SelfTicket}");
                return (transport, directMesh, meshCts);
            }

            case "conv":
            {
                if (directMesh is null)
                {
                    Console.WriteLine("Mesh is not running. Use 'mesh start' first.");
                    return (transport, directMesh, meshCts);
                }

                if (tokens.Count < 4)
                {
                    Console.WriteLine("Usage: mesh conv <conversationId> <nodeId1> [nodeId2 ...]");
                    return (transport, directMesh, meshCts);
                }

                string convId = tokens[2];
                IEnumerable<NodeId> members = tokens.Skip(3).Select(id => new NodeId(id));
                directMesh.RegisterConversation(convId, members);
                Console.WriteLine($"Conversation '{convId}' registered.");
                return (transport, directMesh, meshCts);
            }

            case "send":
            {
                if (directMesh is null)
                {
                    Console.WriteLine("Mesh is not running. Use 'mesh start' first.");
                    return (transport, directMesh, meshCts);
                }

                if (tokens.Count < 4)
                {
                    Console.WriteLine("Usage: mesh send <conversationId> <text>");
                    return (transport, directMesh, meshCts);
                }

                string convId = tokens[2];
                string text = string.Join(' ', tokens.Skip(3));
                string senderName = activeSession?.Profile.Username ?? "Anonymous";
                IReadOnlyDictionary<NodeId, bool> results = await directMesh.SendMessageAsync(
                    convId, senderName, text).ConfigureAwait(false);

                foreach (KeyValuePair<NodeId, bool> kv in results)
                {
                    Console.WriteLine($"  -> {kv.Key}: {(kv.Value ? "OK" : "FAILED")}");
                }
                return (transport, directMesh, meshCts);
            }

            case "stop":
            {
                StopMesh(ref transport, ref directMesh, ref meshCts);
                Console.WriteLine("Mesh stopped.");
                return (null, null, null);
            }

            default:
                Console.WriteLine($"Unknown mesh subcommand '{sub}'.");
                return (transport, directMesh, meshCts);
        }
    }

    private static void StopMesh(
        ref IrohEndpointAdapter? transport,
        ref GroupMeshService? directMesh,
        ref CancellationTokenSource? meshCts)
    {
        meshCts?.Cancel();
        meshCts?.Dispose();
        meshCts = null;
        directMesh = null;
        transport?.Dispose();
        transport = null;
    }

    private static string? ParseRootOption(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--root")
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    internal static List<string> SplitArgs(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static async Task<int> RunTuiDemoAsync(string effectiveRoot, IAccountService accountService)
    {
        var tui = new NcursesTui(effectiveRoot, accountService);
        await tui.ProcessInputLineAsync("/account create Alice --no-password").ConfigureAwait(false);
        await tui.ProcessInputLineAsync("/account unlock Alice").ConfigureAwait(false);
        await tui.ProcessInputLineAsync("/group use dev-room \"Whispernet Core Team\"").ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        tui.RecordPeerSeen(new NodeId("mp_raspi_node_01"), NodeRole.MeetingPoint, now.AddMinutes(-2));
        tui.RecordPeerSeen(new NodeId("peer_bob_fresh99"), NodeRole.Peer, now.AddMinutes(-8));
        tui.RecordPeerSeen(new NodeId("peer_old_stale42"), NodeRole.Peer, now.AddMinutes(-25)); // > 15 min -> excluded

        await tui.ProcessInputLineAsync("Hello team! Testing the ncurses interface.").ConfigureAwait(false);

        Console.WriteLine("=== FRAME 1: GROUP CHAT & ACTIVE PEERS (<15m) ===");
        Console.WriteLine(tui.RenderFrame(100, 20));

        await tui.ProcessInputLineAsync("/filter AUTH").ConfigureAwait(false);
        Console.WriteLine("\n=== FRAME 2: TRANSACTION LOGS (FILTERED BY THEME: AUTH) ===");
        Console.WriteLine(tui.RenderFrame(100, 20));
        return 0;
    }

    private static async Task<int> RunSimDagDemoAsync(string effectiveRoot, IAccountService accountService)
    {
        var tui = new NcursesTui(effectiveRoot, accountService);
        if (!Console.IsInputRedirected)
        {
            _ = Task.Run(tui.RunThreeUserLateMessageSimulationAsync);
            await tui.RunAsync().ConfigureAwait(false);
            return 0;
        }

        (ChatEvent lateE3, string groupId, string groupTitle) = await tui
            .PrepareThreeUserOutOfOrderScenarioAsync()
            .ConfigureAwait(false);

        Console.WriteLine("=== STEP 1: BEFORE LATE MESSAGE E3 ARRIVES (Only E1, E2, E4 received) ===");
        Console.WriteLine(tui.RenderFrame(112, 20));

        await Task.Delay(2000).ConfigureAwait(false);
        tui.IngestDagEvent(groupId, groupTitle, lateE3, "Charlie");

        Console.WriteLine("\n=== STEP 2: AFTER LATE MESSAGE E3 ARRIVES (Inserted between E1 and E2!) ===");
        Console.WriteLine(tui.RenderFrame(112, 20));
        return 0;
    }

    private static async Task<int> RunModQueueDemoAsync(string effectiveRoot, IAccountService accountService)
    {
        var tui = new NcursesTui(effectiveRoot, accountService);
        await tui.PrepareModeratorQueueDemoAsync().ConfigureAwait(false);

        Console.WriteLine("=== MODERATOR PENDING APPROVALS QUEUE (Before Accept/Ignore) ===");
        Console.WriteLine(tui.RenderFrame(112, 20));

        await tui.ProcessInputLineAsync("/mod accept Bob").ConfigureAwait(false);
        await tui.ProcessInputLineAsync("/mod ignore Charlie").ConfigureAwait(false);

        Console.WriteLine("\n=== MODERATOR PENDING APPROVALS QUEUE (After Accept Bob + Ignore Charlie) ===");
        Console.WriteLine(tui.RenderFrame(112, 20));
        return 0;
    }

    private static async Task<int> RunReopenAutomationTestAsync(string[] args)
    {
        int phaseIdx = Array.FindIndex(args, static a => a.Equals("--phase", StringComparison.OrdinalIgnoreCase));
        if (phaseIdx >= 0 && phaseIdx + 2 < args.Length)
        {
            string phase = args[phaseIdx + 1];
            string sharedRoot = args[phaseIdx + 2];
            if (phase == "1")
            {
                return await RunReopenPhase1Async(sharedRoot).ConfigureAwait(false);
            }
            if (phase == "2")
            {
                return await RunReopenPhase2Async(sharedRoot).ConfigureAwait(false);
            }
        }

        string testRoot = Path.Combine(Path.GetTempPath(), $"whispernet_reopen_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        Console.WriteLine($"[REOPEN-TEST] Root directory: {testRoot}");

        try
        {
            string dllPath = typeof(Program).Assembly.Location;

            Console.WriteLine("\n--- PHASE 1: Create accounts, start mesh, create group, invite/join, exchange messages ---");
            int rc1 = await RunChildPhaseAsync(dllPath, "1", testRoot).ConfigureAwait(false);
            if (rc1 != 0)
            {
                Console.WriteLine($"[REOPEN-TEST] FAILED in Phase 1 (exit code {rc1}).");
                return 1;
            }

            Console.WriteLine("\n--- PHASE 2: Reopen fresh app processes after exit, unlock accounts, start mesh, exchange messages ---");
            int rc2 = await RunChildPhaseAsync(dllPath, "2", testRoot).ConfigureAwait(false);
            if (rc2 != 0)
            {
                Console.WriteLine($"[REOPEN-TEST] FAILED in Phase 2 (exit code {rc2}).");
                return 1;
            }

            Console.WriteLine("\n[REOPEN-TEST] SUCCESS: All messages arrived before and after full app exit & reopen!");
            return 0;
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    private static async Task<int> RunChildPhaseAsync(string dllPath, string phase, string testRoot)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(dllPath);
        psi.ArgumentList.Add("--test-reopen");
        psi.ArgumentList.Add("--phase");
        psi.ArgumentList.Add(phase);
        psi.ArgumentList.Add(testRoot);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.Write(stdout);
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.Write(stderr);
        }
        return proc.ExitCode;
    }

    private static async Task<int> RunReopenPhase1Async(string sharedRoot)
    {
        string rootA = Path.Combine(sharedRoot, "alice");
        string rootB = Path.Combine(sharedRoot, "bob");
        string rootC = Path.Combine(sharedRoot, "charlie");

        var tuiA = new NcursesTui(rootA, new AccountService(rootA));
        var tuiB = new NcursesTui(rootB, new AccountService(rootB));
        var tuiC = new NcursesTui(rootC, new AccountService(rootC));

        await tuiA.InitializePersistenceAsync().ConfigureAwait(false);
        await tuiB.InitializePersistenceAsync().ConfigureAwait(false);
        await tuiC.InitializePersistenceAsync().ConfigureAwait(false);

        // Test BOTH orders: tuiA does /mesh start BEFORE /account unlock; tuiB & tuiC do /account unlock BEFORE /mesh start
        await tuiA.ProcessInputLineAsync("/account create Alice --no-password").ConfigureAwait(false);
        await tuiA.ProcessInputLineAsync("/mesh start").ConfigureAwait(false);
        await tuiA.ProcessInputLineAsync("/account unlock Alice").ConfigureAwait(false);

        await tuiB.ProcessInputLineAsync("/account create Bob --no-password").ConfigureAwait(false);
        await tuiB.ProcessInputLineAsync("/account unlock Bob").ConfigureAwait(false);
        await tuiB.ProcessInputLineAsync("/mesh start").ConfigureAwait(false);

        await tuiC.ProcessInputLineAsync("/account create Charlie --no-password").ConfigureAwait(false);
        await tuiC.ProcessInputLineAsync("/account unlock Charlie").ConfigureAwait(false);
        await tuiC.ProcessInputLineAsync("/mesh start").ConfigureAwait(false);

        Console.WriteLine($"[Phase1] Alice   NodeId = {tuiA.SelfNodeId}");
        Console.WriteLine($"[Phase1] Bob     NodeId = {tuiB.SelfNodeId}");
        Console.WriteLine($"[Phase1] Charlie NodeId = {tuiC.SelfNodeId}");
        if (tuiA.SelfNodeId is null || tuiB.SelfNodeId is null || tuiC.SelfNodeId is null ||
            tuiA.SelfNodeId == tuiB.SelfNodeId || tuiA.SelfNodeId == tuiC.SelfNodeId || tuiB.SelfNodeId == tuiC.SelfNodeId)
        {
            Console.WriteLine("[Phase1] ERROR: Peers do not have distinct NodeIds!");
            return 1;
        }

        await tuiA.ProcessInputLineAsync("/group create Voltaire").ConfigureAwait(false);
        string groupId = tuiA.ActiveGroupId;
        await tuiA.ProcessInputLineAsync("/group invite").ConfigureAwait(false);
        string? inviteLink = tuiA.LastInviteLink;
        if (string.IsNullOrWhiteSpace(inviteLink))
        {
            Console.WriteLine("[Phase1] ERROR: Failed to create invite link.");
            return 1;
        }

        await tuiB.ProcessInputLineAsync($"/group join {inviteLink}").ConfigureAwait(false);
        await tuiC.ProcessInputLineAsync($"/group join {inviteLink}").ConfigureAwait(false);

        // Wait for Alice to receive both Bob's and Charlie's join requests
        bool gotJoinReqs = await WaitUntilAsync(
            () => tuiA.GovernanceService.GetPendingJoinRequests(new GroupId(groupId)).Count >= 2,
            timeoutMs: 20000).ConfigureAwait(false);
        if (!gotJoinReqs)
        {
            Console.WriteLine("[Phase1] ERROR: Alice did not receive both join requests.");
            DumpLogs("Alice", tuiA);
            DumpLogs("Bob", tuiB);
            DumpLogs("Charlie", tuiC);
            return 1;
        }

        await tuiA.ProcessInputLineAsync("/mod accept Bob").ConfigureAwait(false);
        await tuiA.ProcessInputLineAsync("/mod accept Charlie").ConfigureAwait(false);

        // Wait for Bob and Charlie to be synced into the group
        bool allSynced = await WaitUntilAsync(
            () => tuiB.ActiveGroupId == groupId && tuiC.ActiveGroupId == groupId,
            timeoutMs: 20000).ConfigureAwait(false);
        if (!allSynced)
        {
            Console.WriteLine("[Phase1] ERROR: Bob or Charlie did not receive RosterSync after /mod accept.");
            DumpLogs("Alice", tuiA);
            DumpLogs("Bob", tuiB);
            DumpLogs("Charlie", tuiC);
            return 1;
        }

        await tuiA.ProcessInputLineAsync("Msg1 from Alice in Phase 1").ConfigureAwait(false);
        await tuiB.ProcessInputLineAsync("Msg2 from Bob in Phase 1").ConfigureAwait(false);
        await tuiC.ProcessInputLineAsync("Msg3 from Charlie in Phase 1").ConfigureAwait(false);

        string[] phase1Expected =
        {
            "Msg1 from Alice in Phase 1",
            "Msg2 from Bob in Phase 1",
            "Msg3 from Charlie in Phase 1"
        };

        bool phase1Converged = await WaitUntilAsync(
            () => HasAllMessages(tuiA, groupId, phase1Expected) &&
                  HasAllMessages(tuiB, groupId, phase1Expected) &&
                  HasAllMessages(tuiC, groupId, phase1Expected),
            timeoutMs: 25000).ConfigureAwait(false);

        Console.WriteLine($"[Phase1] All 3 peers received all 3 Phase-1 messages: {phase1Converged}");
        if (!phase1Converged)
        {
            DumpLogs("Alice", tuiA);
            DumpLogs("Bob", tuiB);
            DumpLogs("Charlie", tuiC);
            return 1;
        }

        return 0;
    }

    private static async Task<int> RunReopenPhase2Async(string sharedRoot)
    {
        string rootA = Path.Combine(sharedRoot, "alice");
        string rootB = Path.Combine(sharedRoot, "bob");
        string rootC = Path.Combine(sharedRoot, "charlie");

        var tuiA = new NcursesTui(rootA, new AccountService(rootA));
        var tuiB = new NcursesTui(rootB, new AccountService(rootB));
        var tuiC = new NcursesTui(rootC, new AccountService(rootC));

        await tuiA.InitializePersistenceAsync().ConfigureAwait(false);
        await tuiB.InitializePersistenceAsync().ConfigureAwait(false);
        await tuiC.InitializePersistenceAsync().ConfigureAwait(false);

        // Reopen: test both mesh-before-unlock (Alice) and unlock-before-mesh (Bob & Charlie)
        await tuiA.ProcessInputLineAsync("/mesh start").ConfigureAwait(false);
        await tuiA.ProcessInputLineAsync("/account unlock Alice").ConfigureAwait(false);

        await tuiB.ProcessInputLineAsync("/account unlock Bob").ConfigureAwait(false);
        await tuiB.ProcessInputLineAsync("/mesh start").ConfigureAwait(false);

        await tuiC.ProcessInputLineAsync("/account unlock Charlie").ConfigureAwait(false);
        await tuiC.ProcessInputLineAsync("/mesh start").ConfigureAwait(false);

        Console.WriteLine($"[Phase2] Alice   NodeId = {tuiA.SelfNodeId}, ActiveGroup = {tuiA.ActiveGroupId}");
        Console.WriteLine($"[Phase2] Bob     NodeId = {tuiB.SelfNodeId}, ActiveGroup = {tuiB.ActiveGroupId}");
        Console.WriteLine($"[Phase2] Charlie NodeId = {tuiC.SelfNodeId}, ActiveGroup = {tuiC.ActiveGroupId}");

        string groupId = tuiA.ActiveGroupId;
        if (groupId == "general" || tuiB.ActiveGroupId != groupId || tuiC.ActiveGroupId != groupId)
        {
            Console.WriteLine($"[Phase2] ERROR: Group was not restored on reopen (Alice={tuiA.ActiveGroupId}, Bob={tuiB.ActiveGroupId}, Charlie={tuiC.ActiveGroupId}).");
            DumpLogs("Alice", tuiA);
            DumpLogs("Bob", tuiB);
            DumpLogs("Charlie", tuiC);
            return 1;
        }

        await tuiA.ProcessInputLineAsync("Msg4 from Alice AFTER REOPEN").ConfigureAwait(false);
        await tuiB.ProcessInputLineAsync("Msg5 from Bob AFTER REOPEN").ConfigureAwait(false);
        await tuiC.ProcessInputLineAsync("Msg6 from Charlie AFTER REOPEN").ConfigureAwait(false);

        string[] allExpected =
        {
            "Msg1 from Alice in Phase 1",
            "Msg2 from Bob in Phase 1",
            "Msg3 from Charlie in Phase 1",
            "Msg4 from Alice AFTER REOPEN",
            "Msg5 from Bob AFTER REOPEN",
            "Msg6 from Charlie AFTER REOPEN"
        };

        bool phase2Converged = await WaitUntilAsync(
            () => HasAllMessages(tuiA, groupId, allExpected) &&
                  HasAllMessages(tuiB, groupId, allExpected) &&
                  HasAllMessages(tuiC, groupId, allExpected),
            timeoutMs: 25000).ConfigureAwait(false);

        Console.WriteLine($"[Phase2] All 3 peers converged on all 6 messages (Phase 1 + Phase 2): {phase2Converged}");

        if (!phase2Converged)
        {
            DumpLogs("Alice", tuiA);
            DumpLogs("Bob", tuiB);
            DumpLogs("Charlie", tuiC);
            return 1;
        }

        return 0;
    }

    private static bool HasAllMessages(NcursesTui tui, string groupId, IEnumerable<string> expectedSubstrings)
    {
        IReadOnlyList<string> actual = tui.GetRoomMessageContents(groupId);
        return expectedSubstrings.All(exp => actual.Any(a => a.Contains(exp, StringComparison.Ordinal)));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, int timeoutMs)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }
            await Task.Delay(200).ConfigureAwait(false);
            elapsed += 200;
        }
        return predicate();
    }

    private static void DumpLogs(string label, NcursesTui tui)
    {
        Console.WriteLine($"=== LOGS ({label}) ===");
        foreach (string line in tui.GetRecentLogLines(40))
        {
            Console.WriteLine("  " + line);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Commands:
              tui
              paths
              account create <username> [--no-password]
              account list
              account unlock <username|authorId>
              account lock
              account status
              account sign <text>
              account verify <authorId|pubKeyBase64> <sigBase64> <text>
              vault write <relativePath> <text>
              vault read <relativePath>
              vault list
              cache init
              cache list [limit]
              cache upsert <nodeId> [Peer|MeetingPoint] [ticket]
              cache prune [maxAgeDays] [maxFailures]
              mesh start [alpn]
              mesh info
              mesh conv <conversationId> <nodeId1> [nodeId2 ...]
              mesh send <conversationId> <text>
              mesh stop
              exit | quit
            """);
    }
}
