using System.Security.Cryptography;
using System.Text;
using Whispernet.App.Messaging;
using Whispernet.Core;
using Whispernet.Daemon.Contracts;
using Whispernet.Daemon.Core;
using Whispernet.Transport.Iroh;

namespace Whispernet;

public static class InfrastructureTests
{
    public static async Task<int> RunAllAsync()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  Whispernet — Suite de Tests Complète (Niveaux 1, 2 et 3)  ");
        Console.WriteLine("============================================================");

        // Niveau 1 : Tests déterministes Sans-I/O en mémoire
        RunTest("1.1 HyParView JOIN + FORWARD_JOIN + SHUFFLE + Invariant", TestHyParViewJoinAndShuffle);
        RunTest("1.2 Plumtree GOSSIP + PRUNE + Panne + IHAVE/GRAFT", TestPlumtreeBroadcastPruneAndCrashRecovery);
        RunTest("1.3 PeerCache Ordre LRU + Tolérance 5 échecs + Grace Fallback", TestPeerCacheOrderToleranceAndStaleFallback);
        RunTest("1.4 Anti-Îlots (PartitionHealTimer -> fusion de 2 îlots)", TestPartitionHealingAntiIslands);
        RunTest("1.5 Topologie adaptative avec hystérésis (>16 Plumtree, <12 Full Mesh)", TestAdaptiveTopologyHysteresis);
        RunTest("1.6 GroupGovernanceEngine (Manifest + LWW-CRDT + Moderation + Tombstone)", TestGroupGovernancePhase1);
        RunTest("1.7 Governance DAG Replay + Demotion Rollback + Log Compaction", TestGovernanceDagReplayAndCompaction);
        RunTest("1.8 PeerActivityTimetable (Weekday vs Weekend) + PRoPHET Top-3 Mutual Relays", TestPeerActivityTimetableAndProphetRelays);
        RunTest("1.9 FriendGraphEngine (Request/Accept/Reject/Remove + Mutual + Security)", TestFriendGraphEngineProtocol);

        // Niveau 2 : Intégration SQLite réelle et Comptes chiffrés (.App)
        await RunTestAsync("2.1 SqlitePeerCacheRepository (CRUD + Ordre + Purge)", TestSqlitePeerCacheRepositoryAsync);
        await RunTestAsync("2.2 AccountService + WhispernetPaths (ECDSA + PBKDF2 + AES-GCM)", TestAccountServiceAsync);
        await RunTestAsync("2.3 GroupGovernanceService + GroupMeshService (Phase 2)", TestGroupGovernancePhase2Async);
        await RunTestAsync("2.4 ECDH Pair Secret + Blind-Tag HMAC + UUID Deduplication", TestDirectMessageCryptoAndDedupAsync);
        await RunTestAsync("2.5 FriendService (Vault Persistence + Friend Link + DM Policy)", TestFriendServiceVaultAndPolicyAsync);

        // Niveau 3 : Réseau réel Iroh QUIC + Mainline DHT (par NodeId seul)
        await RunTestAsync("3.1 Iroh QUIC + Discovery All (GroupMeshService 1-to-1)", TestRealIrohMainlineDhtDirectMessagingAsync);
        await RunTestAsync("3.2 Iroh QUIC + Mainline DHT SEULE (sans mDNS ni n0_dns)", TestRealIrohMainlineDhtIsolatedAsync);
        await RunTestAsync("3.3 Invite Link (Mods-Only + Pre-Approval) + Live Iroh Join/Sync", TestLiveInvitationLinkAndPreApprovalAsync);
        await RunTestAsync("3.4 ChatDAG Batched Topological Slice Sync (Stream 0x02, 500/batch)", TestLiveDagTopologicalSliceSyncAsync);
        await RunTestAsync("3.5 1-on-1 Live QUIC ACK + Typing + PRoPHET Blind-Tagged Relay Fallback", TestLiveDirectMessagingAckAndDtnFallbackAsync);
        await RunTestAsync("3.6 WhispernetDaemonHost Unattended Blind DTN Relay & Watch-Tag Claim", TestDaemonUnattendedBlindRelayAsync);

        Console.WriteLine("\nTOUS LES TESTS ONT RÉUSSI AVEC SUCCÈS !");
        return 0;
    }

    private static void RunTest(string name, Action test)
    {
        Console.Write($"[TEST] {name} ... ");
        test();
        Console.WriteLine("OK");
    }

    private static async Task RunTestAsync(string name, Func<Task> test)
    {
        Console.Write($"[TEST] {name} ... ");
        await test();
        Console.WriteLine("OK");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion échouée : {message}");
        }
    }

    // =========================================================================
    // Mini-Simulateur Sans-I/O en mémoire
    // =========================================================================
    private sealed class InMemoryCluster
    {
        public Dictionary<NodeId, NodeState> Nodes { get; } = new();
        public Dictionary<NodeId, List<(MsgId Id, byte[] Payload)>> Delivered { get; } = new();
        public HashSet<NodeId> DeadNodes { get; } = new();
        public List<(NodeId From, NodeId To, NetworkMessage Msg)> NetworkQueue { get; } = new();
        public Random Rng { get; } = new(42);

        public NodeState AddNode(string id, NodeRole role = NodeRole.Peer, HyParViewConfig? hpvConfig = null)
        {
            var nodeId = new NodeId(id);
            var state = new NodeState(nodeId, role, hpvConfig);
            Nodes[nodeId] = state;
            Delivered[nodeId] = new List<(MsgId, byte[])>();
            return state;
        }

        public void Dispatch(NodeId origin, IReadOnlyList<OutAction> actions)
        {
            foreach (OutAction action in actions)
            {
                switch (action)
                {
                    case OutAction.Send send:
                        if (!DeadNodes.Contains(send.Target) && !DeadNodes.Contains(origin))
                        {
                            NetworkQueue.Add((origin, send.Target, send.Message));
                        }
                        break;
                    case OutAction.DeliverToApp del:
                        Delivered[origin].Add((del.MessageId, del.Payload));
                        break;
                }
            }
        }

        public void PumpAllMessages(int maxSteps = 500)
        {
            int steps = 0;
            while (NetworkQueue.Count > 0 && steps++ < maxSteps)
            {
                var (from, to, msg) = NetworkQueue[0];
                NetworkQueue.RemoveAt(0);

                if (DeadNodes.Contains(from) || DeadNodes.Contains(to))
                {
                    continue;
                }

                if (Nodes.TryGetValue(to, out NodeState? targetState))
                {
                    var actions = NodeOrchestrator.OnReceiveMessage(targetState, from, msg, Rng);
                    Dispatch(to, actions);
                }
            }
        }

        public void TriggerTimerAll(TimerKind timer)
        {
            foreach (var (id, state) in Nodes)
            {
                if (!DeadNodes.Contains(id))
                {
                    Dispatch(id, NodeOrchestrator.OnTimerExpired(state, timer, Rng));
                }
            }
        }
    }

    // =========================================================================
    // NIVEAU 1 : Tests Sans-I/O
    // =========================================================================
    private static void TestHyParViewJoinAndShuffle()
    {
        var cluster = new InMemoryCluster();
        var cfg = new HyParViewConfig { MaxActiveViewSize = 3, MaxPassiveViewSize = 10, ActiveRandomWalkLength = 3 };

        var nodeA = cluster.AddNode("A", NodeRole.MeetingPoint, cfg);
        string[] others = ["B", "C", "D", "E", "F"];

        foreach (string name in others)
        {
            var n = cluster.AddNode(name, NodeRole.Peer, cfg);
            cluster.Dispatch(n.HyParView.SelfId, NodeOrchestrator.InitiateJoin(n, nodeA.HyParView.SelfId));
            cluster.PumpAllMessages();
        }

        cluster.TriggerTimerAll(new TimerKind.ShuffleTimer());
        cluster.PumpAllMessages();

        foreach (var (id, state) in cluster.Nodes)
        {
            Assert(state.HyParView.ActiveView.Count > 0, $"Le nœud {id} a une ActiveView vide.");
            Assert(state.CheckConsistencyInvariant(), $"Invariant HyParView/Plumtree violé sur {id}.");
        }
    }

    private static void TestPlumtreeBroadcastPruneAndCrashRecovery()
    {
        var cluster = new InMemoryCluster();
        var cfg = new HyParViewConfig { MaxActiveViewSize = 4, MaxPassiveViewSize = 10 };

        var a = cluster.AddNode("A", NodeRole.Peer, cfg);
        var b = cluster.AddNode("B", NodeRole.Peer, cfg);
        var c = cluster.AddNode("C", NodeRole.Peer, cfg);
        var d = cluster.AddNode("D", NodeRole.Peer, cfg);
        var e = cluster.AddNode("E", NodeRole.Peer, cfg);

        // Former un maillage dense initial (tous en Eager au départ)
        void Link(NodeState x, NodeState y)
        {
            x.HyParView.ActiveView.Add(y.HyParView.SelfId);
            y.HyParView.ActiveView.Add(x.HyParView.SelfId);
            PlumtreeEngine.OnNeighborUp(x.Plumtree, y.HyParView.SelfId);
            PlumtreeEngine.OnNeighborUp(y.Plumtree, x.HyParView.SelfId);
        }

        Link(a, b);
        Link(a, c);
        Link(b, c);
        Link(b, e);
        Link(c, d);
        Link(d, e);

        // 1. Diffusion M1 : tous reçoivent M1 exactement 1 fois, et PRUNE élague les doublons
        byte[] m1 = Encoding.UTF8.GetBytes("Message M1");
        cluster.Dispatch(a.HyParView.SelfId, NodeOrchestrator.BroadcastMessage(a, new MsgId("M1"), m1));
        cluster.PumpAllMessages();

        foreach (var (id, list) in cluster.Delivered)
        {
            if (id == a.HyParView.SelfId) continue;
            Assert(list.Count(x => x.Id == new MsgId("M1")) == 1, $"M1 non reçu exactement 1 fois sur {id}.");
        }

        bool anyLazyCreated = cluster.Nodes.Values.Any(n => n.Plumtree.LazyPeers.Count > 0);
        Assert(anyLazyCreated, "Aucun pair n'est passé en Lazy après réception de doublons (PRUNE).");

        // 2. Crash brutal de B -> notification OnConnectionLost sur ses voisins
        cluster.DeadNodes.Add(b.HyParView.SelfId);
        foreach (var (id, state) in cluster.Nodes)
        {
            if (id != b.HyParView.SelfId && state.HyParView.ActiveView.Contains(b.HyParView.SelfId))
            {
                cluster.Dispatch(id, NodeOrchestrator.OnConnectionLost(state, b.HyParView.SelfId, cluster.Rng));
            }
        }
        cluster.PumpAllMessages();

        // 3. Diffusion M2 + Flush LazyPushTimer + Timeout IHaveTimer -> réparation par GRAFT
        byte[] m2 = Encoding.UTF8.GetBytes("Message M2 après crash");
        cluster.Dispatch(a.HyParView.SelfId, NodeOrchestrator.BroadcastMessage(a, new MsgId("M2"), m2));
        cluster.PumpAllMessages();

        cluster.TriggerTimerAll(new TimerKind.LazyPushTimer());
        cluster.PumpAllMessages();

        cluster.TriggerTimerAll(new TimerKind.IHaveTimer(new MsgId("M2")));
        cluster.PumpAllMessages();

        foreach (var (id, list) in cluster.Delivered)
        {
            if (id == a.HyParView.SelfId || id == b.HyParView.SelfId) continue;
            Assert(list.Count(x => x.Id == new MsgId("M2")) == 1, $"M2 manquant après réparation Plumtree sur {id}.");
        }
    }

    private static void TestPeerCacheOrderToleranceAndStaleFallback()
    {
        var now = DateTimeOffset.UtcNow;
        var cfg = new HyParViewConfig
        {
            MaxActiveViewSize = 4,
            MaxPassiveViewSize = 10,
            MaxConsecutiveFailuresBeforeEviction = 5,
            PeerCacheMaxAge = TimeSpan.FromDays(7),
            AllowStaleFallbackWhenCacheEmpty = true
        };
        var state = new HyParViewState(new NodeId("SELF"), NodeRole.Peer, cfg);

        var entries = new[]
        {
            new CachedPeerEntry(new NodeId("OLD_PEER"), NodeRole.Peer, null, now.AddDays(-3), SuccessCount: 2, ConsecutiveFailures: 0),
            new CachedPeerEntry(new NodeId("RECENT_PEER"), NodeRole.Peer, null, now.AddMinutes(-5), SuccessCount: 10, ConsecutiveFailures: 0),
            new CachedPeerEntry(new NodeId("PROBATION_PEER"), NodeRole.Peer, null, now.AddMinutes(-2), SuccessCount: 4, ConsecutiveFailures: 2),
            new CachedPeerEntry(new NodeId("MEETING_PI"), NodeRole.MeetingPoint, null, now.AddDays(-20), SuccessCount: 50, ConsecutiveFailures: 0),
        };

        var sorted = PeerCacheEngine.SortByPriority(entries);
        Assert(sorted[0].NodeId == new NodeId("MEETING_PI"), "MeetingPoint doit être 1er.");
        Assert(sorted[1].NodeId == new NodeId("RECENT_PEER"), "Pair sain récent doit être 2e.");
        Assert(sorted[2].NodeId == new NodeId("OLD_PEER"), "Pair sain plus ancien doit être 3e.");
        Assert(sorted[3].NodeId == new NodeId("PROBATION_PEER"), "Pair en probation (échecs récents) doit être en dernier.");

        // Tolérance : 4 échecs gardent le pair, 5e échec l'évince (sauf MeetingPoint)
        var actions = new List<OutAction>();
        for (int i = 0; i < 4; i++)
        {
            PeerCacheEngine.RecordPeerFailure(state, new NodeId("FLAKY"), now.AddMinutes(i), actions);
            Assert(state.PeerCacheMetadata.ContainsKey(new NodeId("FLAKY")), $"FLAKY ne doit pas être évincé à l'échec {i + 1}.");
        }
        PeerCacheEngine.RecordPeerFailure(state, new NodeId("FLAKY"), now.AddMinutes(5), actions);
        Assert(!state.PeerCacheMetadata.ContainsKey(new NodeId("FLAKY")), "FLAKY doit être évincé au 5e échec consécutif.");

        // MeetingPoint immunisé même après 6 échecs
        state.KnownRoles[new NodeId("MEETING_PI")] = NodeRole.MeetingPoint;
        for (int i = 0; i < 6; i++)
        {
            PeerCacheEngine.RecordPeerFailure(state, new NodeId("MEETING_PI"), now.AddMinutes(i), actions);
        }
        Assert(state.PeerCacheMetadata.ContainsKey(new NodeId("MEETING_PI")), "MeetingPoint ne doit jamais être évincé.");

        // Historique 100 % périmé (> 7 jours) -> Grace Fallback
        var staleState = new HyParViewState(new NodeId("SELF2"), NodeRole.Peer, cfg);
        var staleOnly = new[]
        {
            new CachedPeerEntry(new NodeId("VETERAN"), NodeRole.Peer, null, now.AddDays(-20), SuccessCount: 15)
        };
        PeerCacheEngine.HydrateFromPeerCache(staleState, staleOnly, now);
        Assert(staleState.PassiveView.Contains(new NodeId("VETERAN")), "Grace Fallback doit conserver VETERAN pour 1 essai.");
        Assert(staleState.PeerCacheMetadata[new NodeId("VETERAN")].ConsecutiveFailures == 4, "Grace Fallback doit placer VETERAN à Max-1 échecs.");
    }

    private static void TestPartitionHealingAntiIslands()
    {
        var cluster = new InMemoryCluster();
        var cfg = new HyParViewConfig { MaxActiveViewSize = 3, MaxPassiveViewSize = 10 };

        // Îlot 1 : A, B, C
        var a = cluster.AddNode("A", NodeRole.Peer, cfg);
        var b = cluster.AddNode("B", NodeRole.Peer, cfg);
        var c = cluster.AddNode("C", NodeRole.Peer, cfg);

        // Îlot 2 : D, E, F
        var d = cluster.AddNode("D", NodeRole.Peer, cfg);
        var e = cluster.AddNode("E", NodeRole.Peer, cfg);
        var f = cluster.AddNode("F", NodeRole.Peer, cfg);

        void Link(NodeState x, NodeState y)
        {
            x.HyParView.ActiveView.Add(y.HyParView.SelfId);
            y.HyParView.ActiveView.Add(x.HyParView.SelfId);
            x.HyParView.SeenNodesInCurrentEpoch.Add(y.HyParView.SelfId);
            y.HyParView.SeenNodesInCurrentEpoch.Add(x.HyParView.SelfId);
        }

        Link(a, b); Link(b, c); Link(c, a);
        Link(d, e); Link(e, f); Link(f, d);

        // C a un vieux souvenir de D dans son PeerCache (hors de son îlot courant)
        c.HyParView.PeerCacheMetadata[d.HyParView.SelfId] =
            new CachedPeerEntry(d.HyParView.SelfId, NodeRole.Peer, null, DateTimeOffset.UtcNow.AddHours(-2), SuccessCount: 3);

        // Déclencher PartitionHealTimer sur C -> envoie un Shuffle inter-îlots vers D via DHT
        var healActions = NodeOrchestrator.OnTimerExpired(c, new TimerKind.PartitionHealTimer(), cluster.Rng);
        Assert(healActions.Any(act => act is OutAction.Connect { Target: var t, AllowDhtLookup: true } && t == d.HyParView.SelfId),
            "PartitionHeal doit émettre Connect(D, AllowDhtLookup: true).");

        cluster.Dispatch(c.HyParView.SelfId, healActions);
        cluster.PumpAllMessages();

        // Vérifier qu'au moins un nœud de l'Îlot 2 (D, E ou F) a intégré un nœud de l'Îlot 1 (A, B ou C) dans sa PassiveView
        bool island2LearnedIsland1 = new[] { d, e, f }.Any(n =>
            n.HyParView.PassiveView.Contains(a.HyParView.SelfId)
            || n.HyParView.PassiveView.Contains(b.HyParView.SelfId)
            || n.HyParView.PassiveView.Contains(c.HyParView.SelfId));

        Assert(island2LearnedIsland1,
            "Les Vues Passives des deux îlots doivent s'être fusionnées après PartitionHeal.");
    }

    // =========================================================================
    // NIVEAU 2 : Intégration SQLite réelle
    // =========================================================================
    private static async Task TestSqlitePeerCacheRepositoryAsync()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"whispernet_test_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqlitePeerCacheRepository(dbPath);
            await repo.InitializeAsync();

            var now = DateTimeOffset.UtcNow;
            var entries = new[]
            {
                new CachedPeerEntry(new NodeId("P_OLD"), NodeRole.Peer, "t1", now.AddDays(-10), SuccessCount: 1),
                new CachedPeerEntry(new NodeId("P_FRESH"), NodeRole.Peer, "t2", now.AddMinutes(-1), SuccessCount: 5),
                new CachedPeerEntry(new NodeId("P_FAIL"), NodeRole.Peer, "t3", now.AddMinutes(-2), SuccessCount: 1, ConsecutiveFailures: 5),
                new CachedPeerEntry(new NodeId("P_MP"), NodeRole.MeetingPoint, "t4", now.AddDays(-30), SuccessCount: 20, ConsecutiveFailures: 8),
            };

            await repo.ApplyCacheUpdateAsync(entries, Array.Empty<NodeId>());
            var loaded = await repo.LoadCachedPeersAsync();
            Assert(loaded.Count == 4, $"Attendu 4 entrées, obtenu {loaded.Count}.");
            Assert(loaded[0].NodeId == new NodeId("P_MP"), "SQLite doit retourner MeetingPoint en premier.");
            Assert(loaded[1].NodeId == new NodeId("P_FRESH"), "SQLite doit retourner le pair frais en 2e.");

            int pruned = await repo.PruneExpiredAndFailedPeersAsync(now, TimeSpan.FromDays(7), maxConsecutiveFailures: 5);
            Assert(pruned == 2, $"Attendu 2 pairs purgés (P_OLD et P_FAIL), obtenu {pruned}.");

            var afterPrune = await repo.LoadCachedPeersAsync();
            Assert(afterPrune.Count == 2, "Il doit rester P_MP (protégé) et P_FRESH.");
            Assert(afterPrune.Any(e => e.NodeId == new NodeId("P_MP")), "MeetingPoint ne doit pas être purgé par SQLite.");
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    // =========================================================================
    // NIVEAU 3 : Test Réseau Réel Iroh QUIC + Mainline DHT (par NodeId seul)
    // =========================================================================
    private static async Task TestRealIrohMainlineDhtDirectMessagingAsync()
    {
        const string alpn = "/whispernet/direct-test/1";
        using var aliceTransport = IrohEndpointAdapter.CreateAndBind(alpn: alpn, enableRelay: true, onlineTimeoutMs: 3000);
        using var bobTransport = IrohEndpointAdapter.CreateAndBind(alpn: alpn, enableRelay: true, onlineTimeoutMs: 3000);

        var aliceDirect = new GroupMeshService(aliceTransport);
        var bobDirect = new GroupMeshService(bobTransport);

        var bobReceivedTcs = new TaskCompletionSource<DirectChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobDirect.MessageReceived += msg => bobReceivedTcs.TrySetResult(msg);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        bobTransport.StartAcceptLoop(bobDirect.HandleIncomingPayload, cts.Token);

        // Alice ne connaît QUE le NodeId de Bob (aucun ticket fourni) -> résolution via Mainline DHT / Pkarr / mDNS
        aliceDirect.RegisterConversation("room-1to1", new[] { bobTransport.SelfId });
        var sendResults = await aliceDirect.SendMessageAsync("room-1to1", "Alice", "Salut Bob via Mainline DHT !", cts.Token);

        Assert(sendResults.TryGetValue(bobTransport.SelfId, out bool sentOk) && sentOk,
            "L'envoi 1-to-1 d'Alice vers le NodeId de Bob via Iroh/DHT a échoué.");

        DirectChatMessage received = await bobReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert(received.Content == "Salut Bob via Mainline DHT !", $"Contenu inattendu : {received.Content}");
        Assert(received.SenderId == aliceTransport.SelfId, "SenderId incorrect.");
    }

    private static async Task TestRealIrohMainlineDhtIsolatedAsync()
    {
        const string alpn = "/whispernet/dht-only-test/1";
        // (DiscoveryConfig)4 correspond à DiscoveryConfig::MainlineDht dans notre binaire natif :
        // AUCUN serveur DNS/Pkarr de n0, AUCUN mDNS local -> 100 % BitTorrent Mainline DHT !
        const N0.IrohNet.DiscoveryConfig mainlineDhtOnly = (N0.IrohNet.DiscoveryConfig)4;

        using var aliceTransport = IrohEndpointAdapter.CreateAndBind(
            alpn: alpn, enableRelay: true, onlineTimeoutMs: 5000, discoveryConfig: mainlineDhtOnly);
        using var bobTransport = IrohEndpointAdapter.CreateAndBind(
            alpn: alpn, enableRelay: true, onlineTimeoutMs: 5000, discoveryConfig: mainlineDhtOnly);

        var aliceDirect = new GroupMeshService(aliceTransport);
        var bobDirect = new GroupMeshService(bobTransport);

        var bobReceivedTcs = new TaskCompletionSource<DirectChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobDirect.MessageReceived += msg => bobReceivedTcs.TrySetResult(msg);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        bobTransport.StartAcceptLoop(bobDirect.HandleIncomingPayload, cts.Token);

        // Laisser le temps à Bob de publier son paquet Pkarr BEP-44 sur la Mainline DHT
        await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);

        aliceDirect.RegisterConversation("room-dht-only", new[] { bobTransport.SelfId });
        var sendResults = await aliceDirect.SendMessageAsync("room-dht-only", "Alice", "Preuve Mainline DHT pure !", cts.Token);

        Assert(sendResults.TryGetValue(bobTransport.SelfId, out bool sentOk) && sentOk,
            "L'envoi 1-to-1 via Mainline DHT pure (sans mDNS ni n0_dns) a échoué.");

        DirectChatMessage received = await bobReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
        Assert(received.Content == "Preuve Mainline DHT pure !", $"Contenu inattendu : {received.Content}");
    }

    // =========================================================================
    // NIVEAU 2.2 : Création de compte sécurisée (.App)
    // =========================================================================
    private static async Task TestAccountServiceAsync()
    {
        // Vérification de la résolution multiplateforme des chemins
        string defaultDataDir = WhispernetPaths.GetDataDirectory();
        if (OperatingSystem.IsWindows())
        {
            Assert(defaultDataDir.EndsWith("Whispernet", StringComparison.Ordinal),
                $"Unexpected Windows data path: {defaultDataDir}");
        }
        else
        {
            Assert(defaultDataDir.EndsWith(Path.Combine(".local", "share", "whispernet"), StringComparison.Ordinal) ||
                   defaultDataDir.EndsWith("whispernet", StringComparison.Ordinal),
                $"Unexpected Linux data path: {defaultDataDir}");
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), $"whispernet_acc_test_{Guid.NewGuid():N}");
        try
        {
            var service = new AccountService(tempRoot);

            // 1. Compte protégé par mot de passe
            AccountProfile alice = await service.CreateAccountAsync("Alice", "MySuperSecretPassword!2026");
            Assert(alice.AuthorId.IsValid, "AuthorId must be a valid 64-char hex SHA-256.");
            Assert(alice.IsPasswordProtected, "Alice account must be marked as password-protected.");

            byte[] pubSpki = Convert.FromBase64String(alice.PublicKeySpkiBase64);
            string expectedAuthorId = Convert.ToHexStringLower(SHA256.HashData(pubSpki));
            Assert(alice.AuthorId.Value == expectedAuthorId, "AuthorId must match SHA-256 of SPKI public key.");

            string aliceDir = WhispernetPaths.GetAccountDirectory(alice.AuthorId, tempRoot);
            Assert(File.Exists(Path.Combine(aliceDir, "profile.json")), "Missing profile.json.");
            Assert(File.Exists(Path.Combine(aliceDir, "identity.key.enc")), "Missing identity.key.enc.");
            Assert(File.Exists(Path.Combine(aliceDir, "folder.dek.enc")), "Missing folder.dek.enc.");
            Assert(!File.Exists(Path.Combine(aliceDir, "identity.key")), "No plaintext key file should exist.");

            // Mauvais mot de passe -> doit lever CryptographicException
            bool wrongPasswordRejected = false;
            try
            {
                using var _ = await service.UnlockAccountAsync(alice.AuthorId, "WrongPassword");
            }
            catch (CryptographicException)
            {
                wrongPasswordRejected = true;
            }
            Assert(wrongPasswordRejected, "Wrong password must be rejected by AES-GCM.");

            // Bon mot de passe -> déverrouillage, signature et coffre chiffré
            using (AccountSession session = await service.UnlockAccountAsync(alice.AuthorId, "MySuperSecretPassword!2026"))
            {
                Assert(session.IsFolderEncrypted, "Password-protected session must enable folder encryption.");
                byte[] message = Encoding.UTF8.GetBytes("Message signed by Alice");
                byte[] sig = service.SignData(session, message);
                Assert(service.VerifySignature(pubSpki, message, sig), "ECDSA P-256 signature must be valid.");

                byte[] secretNote = Encoding.UTF8.GetBytes("Confidential vault content");
                await service.WriteVaultFileAsync(session, "notes/secret.txt", secretNote);

                string diskPath = Path.Combine(aliceDir, "vault", "notes", "secret.txt");
                byte[] diskBytes = await File.ReadAllBytesAsync(diskPath);
                Assert(!diskBytes.AsSpan().SequenceEqual(secretNote), "Vault file must not be stored in plaintext.");

                byte[] decryptedNote = await service.ReadVaultFileAsync(session, "notes/secret.txt");
                Assert(decryptedNote.AsSpan().SequenceEqual(secretNote), "Vault decryption must restore exact plaintext.");
            }

            // 2. Compte sans mot de passe (optionnel)
            AccountProfile bob = await service.CreateAccountAsync("Bob", password: null);
            Assert(!bob.IsPasswordProtected, "Bob account without password must have IsPasswordProtected = false.");
            using (AccountSession bobSession = await service.UnlockAccountAsync(bob.AuthorId))
            {
                Assert(!bobSession.IsFolderEncrypted, "Without password, folder encryption is disabled.");
                byte[] msg = Encoding.UTF8.GetBytes("Hello Bob");
                byte[] sig = service.SignData(bobSession, msg);
                Assert(service.VerifySignature(Convert.FromBase64String(bob.PublicKeySpkiBase64), msg, sig),
                    "Signature without password must be valid.");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void TestAdaptiveTopologyHysteresis()
    {
        var state = new HyParViewState(new NodeId("self"), NodeRole.Peer, HyParViewConfig.AdaptiveGroupDefaults);
        var actions = new List<OutAction>();
        var rng = new Random(42);

        // 1. Add up to 16 peers -> stays in Full Mesh (<= 16)
        for (int i = 1; i <= 16; i++)
        {
            state.ActiveView.Add(new NodeId($"p{i}"));
        }
        HyParViewEngine.TrimActiveViewToEffectiveCapacity(state, actions, rng);
        Assert(!state.IsInPlumtreeOverlayMode, "At 16 online peers, must remain in Full Mesh.");
        Assert(state.EffectiveMaxActiveViewSize == 16, "EffectiveMaxActiveViewSize must be 16 in Full Mesh.");
        Assert(state.ActiveView.Count == 16, "All 16 peers must stay in ActiveView.");

        // 2. 17th peer joins (> 16) -> switches to Plumtree ceil(ln(18)) + 2 = 5
        state.ActiveView.Add(new NodeId("p17"));
        HyParViewEngine.TrimActiveViewToEffectiveCapacity(state, actions, rng);
        Assert(state.IsInPlumtreeOverlayMode, "At 17 online peers (>16), must switch to Plumtree overlay.");
        Assert(state.EffectiveMaxActiveViewSize == 5, $"Expected ceil(ln(18))+2=5, got {state.EffectiveMaxActiveViewSize}.");
        Assert(state.ActiveView.Count == 5, $"ActiveView must trim to 5, got {state.ActiveView.Count}.");
        Assert(state.PassiveView.Count == 12, $"Excess 12 peers must move to PassiveView, got {state.PassiveView.Count}.");

        // 3. Peers drop to 12 (hysteresis band 12..16) -> must STAY in Plumtree (no thrashing)
        while (state.KnownOnlinePeersCount > 12)
        {
            NodeId toRemove = state.PassiveView.First();
            state.PassiveView.Remove(toRemove);
        }
        HyParViewEngine.TrimActiveViewToEffectiveCapacity(state, actions, rng);
        Assert(state.KnownOnlinePeersCount == 12, "Expected 12 online peers.");
        Assert(state.IsInPlumtreeOverlayMode, "At 12 peers (not < 12), hysteresis must keep Plumtree mode.");

        // 4. Peers drop below 12 (11 peers) -> collapses back to Full Mesh
        state.PassiveView.Remove(state.PassiveView.First());
        HyParViewEngine.TrimActiveViewToEffectiveCapacity(state, actions, rng);
        Assert(state.KnownOnlinePeersCount == 11, "Expected 11 online peers.");
        Assert(!state.IsInPlumtreeOverlayMode, "Below 12 peers (< 12), must collapse back to Full Mesh.");
        Assert(state.EffectiveMaxActiveViewSize == 16, "EffectiveMaxActiveViewSize must return to 16.");
    }

    private static void TestGroupGovernancePhase1()
    {
        static bool VerifyEcdsa(ReadOnlySpan<byte> pubSpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubSpki, out _);
            return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }

        static GroupManifest SignManifest(ECDsa ownerKey, GroupManifest unsignedManifest)
        {
            byte[] sig = ownerKey.SignData(unsignedManifest.BuildSignableBytes(), HashAlgorithmName.SHA256);
            return unsignedManifest with { OwnerSignatureBase64 = Convert.ToBase64String(sig) };
        }

        static ModerationAction SignAction(ECDsa signerKey, ModerationAction unsignedAction)
        {
            byte[] sig = signerKey.SignData(unsignedAction.BuildSignableBytes(), HashAlgorithmName.SHA256);
            return unsignedAction with { SignatureBase64 = Convert.ToBase64String(sig) };
        }

        using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var modAKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var modBKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var userXKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        byte[] ownerPub = ownerKey.ExportSubjectPublicKeyInfo();
        byte[] modAPub = modAKey.ExportSubjectPublicKeyInfo();
        byte[] modBPub = modBKey.ExportSubjectPublicKeyInfo();
        byte[] userXPub = userXKey.ExportSubjectPublicKeyInfo();

        GroupId groupId = GroupId.FromOwnerPublicKeySpki(ownerPub);
        var modAId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(modAPub)));
        var modBId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(modBPub)));
        var userXId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(userXPub)));

        var mods = new Dictionary<AuthorId, string>
        {
            [modAId] = Convert.ToBase64String(modAPub),
            [modBId] = Convert.ToBase64String(modBPub)
        };

        GroupManifest v1 = SignManifest(ownerKey, new GroupManifest(
            groupId, Convert.ToBase64String(ownerPub), Version: 1, "Dev Group",
            IsPasswordProtected: false, null, null, null, IsTerminated: false,
            mods, DateTimeOffset.UtcNow, string.Empty));

        // 1. Reject forged manifest signed by a moderator instead of the Owner
        var state = new GroupGovernanceState(v1);
        GroupManifest forgedV2 = new(
            groupId, Convert.ToBase64String(ownerPub), Version: 2, "Hijacked",
            false, null, null, null, false, mods, DateTimeOffset.UtcNow,
            Convert.ToBase64String(modAKey.SignData(
                (v1 with { Version = 2, Title = "Hijacked" }).BuildSignableBytes(),
                HashAlgorithmName.SHA256)));
        Assert(!GroupGovernanceEngine.TryApplyManifest(state, forgedV2, VerifyEcdsa, out _),
            "Moderator must not be able to forge a GroupManifest.");

        // 2. Multi-moderator LWW convergence across out-of-order delivery
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ModerationAction admitX = SignAction(modAKey, new ModerationAction(
            groupId, 1, LamportClock: 1, userXId, Convert.ToBase64String(userXPub),
            new NodeId("node_x"), MemberStatus.Admitted, modAId, "Approved", now, string.Empty));
        ModerationAction silenceX = SignAction(modAKey, new ModerationAction(
            groupId, 1, LamportClock: 2, userXId, Convert.ToBase64String(userXPub),
            new NodeId("node_x"), MemberStatus.Silenced, modAId, "Spam", now.AddSeconds(1), string.Empty));
        ModerationAction banX = SignAction(modBKey, new ModerationAction(
            groupId, 1, LamportClock: 3, userXId, Convert.ToBase64String(userXPub),
            new NodeId("node_x"), MemberStatus.Banned, modBId, "Abuse", now.AddSeconds(2), string.Empty));

        var peer1State = new GroupGovernanceState(v1);
        var peer2State = new GroupGovernanceState(v1);

        // Peer 1 receives in order: admit -> silence -> ban
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peer1State, admitX, VerifyEcdsa, out _, out _), "peer1 admit");
        Assert(GroupGovernanceEngine.CanAuthorPost(peer1State, userXId), "Admitted user must be able to post.");
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peer1State, silenceX, VerifyEcdsa, out _, out _), "peer1 silence");
        Assert(!GroupGovernanceEngine.CanAuthorPost(peer1State, userXId) && GroupGovernanceEngine.CanAuthorReceive(peer1State, userXId),
            "Silenced user cannot post but can receive.");
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peer1State, banX, VerifyEcdsa, out var banActions, out _), "peer1 ban");
        Assert(!GroupGovernanceEngine.CanAuthorReceive(peer1State, userXId), "Banned user cannot receive.");
        Assert(banActions.Any(a => a is OutAction.CloseConnection c && c.Target == new NodeId("node_x")),
            "Ban must emit CloseConnection for target NodeId.");

        // Peer 2 receives in reverse order: ban -> silence -> admit
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peer2State, banX, VerifyEcdsa, out _, out _), "peer2 ban");
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peer2State, silenceX, VerifyEcdsa, out _, out _), "peer2 silence");
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peer2State, admitX, VerifyEcdsa, out _, out _), "peer2 admit");

        Assert(peer1State.GetMemberStatus(userXId) == MemberStatus.Banned, "Peer1 must converge to Banned.");
        Assert(peer2State.GetMemberStatus(userXId) == MemberStatus.Banned, "Peer2 must converge to Banned.");
        Assert(peer1State.ComputeModerationStateHash() == peer2State.ComputeModerationStateHash(),
            "Both peers must converge to identical ModerationStateHash.");

        // 3. Revoked moderator & termination tombstone
        var modsWithoutB = new Dictionary<AuthorId, string> { [modAId] = Convert.ToBase64String(modAPub) };
        GroupManifest v2 = SignManifest(ownerKey, v1 with { Version = 2, ModeratorPublicKeysSpkiBase64 = modsWithoutB });
        Assert(GroupGovernanceEngine.TryApplyManifest(peer1State, v2, VerifyEcdsa, out _), "Owner v2 update must succeed.");

        ModerationAction revokedAction = SignAction(modBKey, new ModerationAction(
            groupId, 2, LamportClock: 4, userXId, Convert.ToBase64String(userXPub),
            null, MemberStatus.Admitted, modBId, "Backdoor", now.AddSeconds(5), string.Empty));
        Assert(!GroupGovernanceEngine.TryApplyModerationAction(peer1State, revokedAction, VerifyEcdsa, out _, out _),
            "Revoked moderator must be rejected.");

        GroupManifest v3Terminate = SignManifest(ownerKey, v2 with { Version = 3, IsTerminated = true });
        Assert(GroupGovernanceEngine.TryApplyManifest(peer1State, v3Terminate, VerifyEcdsa, out _), "Termination must apply.");
        GroupManifest v4Revive = SignManifest(ownerKey, v3Terminate with { Version = 4, IsTerminated = false });
        Assert(!GroupGovernanceEngine.TryApplyManifest(peer1State, v4Revive, VerifyEcdsa, out _),
            "Termination tombstone must be irreversible.");
    }

    private static void TestGovernanceDagReplayAndCompaction()
    {
        static bool VerifyEcdsa(ReadOnlySpan<byte> pubSpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubSpki, out _);
            return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }

        static ModerationAction SignAction(ECDsa key, ModerationAction unsigned)
        {
            byte[] sig = key.SignData(unsigned.BuildSignableBytes(), HashAlgorithmName.SHA256);
            return unsigned with { SignatureBase64 = Convert.ToBase64String(sig) };
        }

        using var groupKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var modBKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var aliceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        byte[] groupPub = groupKey.ExportSubjectPublicKeyInfo();
        byte[] modBPub = modBKey.ExportSubjectPublicKeyInfo();
        byte[] alicePub = aliceKey.ExportSubjectPublicKeyInfo();

        GroupId groupId = GroupId.FromOwnerPublicKeySpki(groupPub);
        var ownerAuthorId = new AuthorId(groupId.Value);
        var modBId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(modBPub)));
        var aliceId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(alicePub)));

        var mods = new Dictionary<AuthorId, string> { [modBId] = Convert.ToBase64String(modBPub) };
        var unsignedManifest = new GroupManifest(
            groupId, Convert.ToBase64String(groupPub), 1, "DAG Mod Room",
            false, null, null, null, false, mods, DateTimeOffset.UtcNow, string.Empty);
        GroupManifest manifest = unsignedManifest with
        {
            OwnerSignatureBase64 = Convert.ToBase64String(groupKey.SignData(unsignedManifest.BuildSignableBytes(), HashAlgorithmName.SHA256))
        };

        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        // 1. ModB admits Alice at clock=1
        ModerationAction admitAlice = SignAction(modBKey, new ModerationAction(
            groupId, 1, LamportClock: 1, aliceId, Convert.ToBase64String(alicePub),
            new NodeId("node_alice"), MemberStatus.Admitted, modBId, "Welcome", t0, string.Empty));

        // 2. Owner demotes ModB at clock=2, while ModB goes rogue and bans Alice at clock=2 (even backdating IssuedAtUtc!)
        ModerationAction demoteModB = SignAction(groupKey, new ModerationAction(
            groupId, 1, LamportClock: 2, modBId, Convert.ToBase64String(modBPub),
            null, MemberStatus.Unknown, ownerAuthorId, "Demoted", t0.AddSeconds(5), string.Empty,
            Parents: new[] { admitAlice.ComputeActionId() },
            Kind: GovernanceActionKind.DemoteModerator));

        ModerationAction rogueBanAlice = SignAction(modBKey, new ModerationAction(
            groupId, 1, LamportClock: 2, aliceId, Convert.ToBase64String(alicePub),
            new NodeId("node_alice"), MemberStatus.Banned, modBId, "Rogue ban", t0.AddMinutes(-10), string.Empty,
            Parents: new[] { admitAlice.ComputeActionId() },
            Kind: GovernanceActionKind.MemberModeration));

        // Peer receives admitAlice, THEN rogueBanAlice (before demotion arrives!)
        var peerState = new GroupGovernanceState(manifest);
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peerState, admitAlice, VerifyEcdsa, out _, out _), "Admit failed");
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peerState, rogueBanAlice, VerifyEcdsa, out _, out _), "Rogue ban ingest failed");
        Assert(peerState.GetMemberStatus(aliceId) == MemberStatus.Banned, "Before demotion arrives, Alice is temporarily Banned.");

        // Now demoteModB arrives late -> DAG sorts demoteModB BEFORE rogueBanAlice -> rogueBanAlice is voided on replay!
        Assert(GroupGovernanceEngine.TryApplyModerationAction(peerState, demoteModB, VerifyEcdsa, out _, out _), "Demote ingest failed");
        Assert(peerState.GetMemberStatus(aliceId) == MemberStatus.Admitted,
            "Late demotion must undo ModB's rogue ban and restore Alice to Admitted!");
        Assert(!peerState.ActiveModerators.ContainsKey(modBId), "ModB must no longer be an active moderator.");

        // 3. Test Log Compaction: add 60 newer actions and compact old superseded/voided ones
        for (ulong c = 3; c <= 65; c++)
        {
            ModerationAction refresh = SignAction(groupKey, new ModerationAction(
                groupId, 1, LamportClock: c, aliceId, Convert.ToBase64String(alicePub),
                new NodeId("node_alice"), MemberStatus.Admitted, ownerAuthorId, "Refresh", t0.AddSeconds(c), string.Empty,
                Parents: peerState.PrepareNextGovernanceContext().Parents));
            Assert(GroupGovernanceEngine.TryApplyModerationAction(peerState, refresh, VerifyEcdsa, out _, out _), "Refresh failed");
        }

        string hashBeforeCompact = peerState.ComputeModerationStateHash();
        int pruned = GroupGovernanceEngine.CompactGovernanceLog(peerState, recentWindow: 10);
        Assert(pruned > 0, "CompactGovernanceLog must prune superseded/voided historical events.");
        Assert(peerState.ComputeModerationStateHash() == hashBeforeCompact,
            "Compaction must preserve the exact derived ModerationStateHash.");
    }

    // =========================================================================
    // NIVEAU 2.3 : Gouvernance de groupe (.App) + Anti-Entropie + Repli Non-Ami
    // =========================================================================
    private static async Task TestGroupGovernancePhase2Async()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"whispernet_grp2_test_{Guid.NewGuid():N}");
        try
        {
            var accountService = new AccountService(tempRoot);
            var govService = new GroupGovernanceService(accountService);

            AccountProfile aliceProf = await accountService.CreateAccountAsync("Alice", "AlicePwd!2026");
            AccountProfile bobProf = await accountService.CreateAccountAsync("Bob", null);
            AccountProfile charlieProf = await accountService.CreateAccountAsync("Charlie", null);
            AccountProfile bob2Prof = await accountService.CreateAccountAsync("Bob2", null);

            using AccountSession alice = await accountService.UnlockAccountAsync(aliceProf.AuthorId, "AlicePwd!2026");
            using AccountSession bob = await accountService.UnlockAccountAsync(bobProf.AuthorId);
            using AccountSession charlie = await accountService.UnlockAccountAsync(charlieProf.AuthorId);
            using AccountSession bob2 = await accountService.UnlockAccountAsync(bob2Prof.AuthorId);

            // 1. Alice creates two independent groups -> distinct GroupIds (dedicated per-group ECDSA P-256 keys)
            GroupGovernanceState groupA = await govService.CreateGroupAsync(
                alice, "Room Alpha", joinPassword: "JoinSecret!42", creatorNodeId: new NodeId("node_alice"));
            GroupGovernanceState groupB = await govService.CreateGroupAsync(
                alice, "Room Beta", joinPassword: null, creatorNodeId: new NodeId("node_alice"));

            Assert(groupA.GroupId != groupB.GroupId, "Two groups by the same creator must have distinct GroupIds.");
            Assert(groupA.GroupId.Value != aliceProf.AuthorId.Value, "GroupId must be independent of creator's AuthorId.");
            Assert(groupA.GetMemberStatus(aliceProf.AuthorId) == MemberStatus.Admitted, "Creator must be Admitted.");

            // Reload from encrypted vault
            GroupGovernanceState reloadedA = await govService.LoadGroupFromVaultAsync(alice, groupA.GroupId);
            Assert(reloadedA.CurrentManifest.Title == "Room Alpha", "Reloaded manifest title mismatch.");

            // 2. JoinApprovalRequest with wrong/right password -> Pending queue -> Accept
            JoinApprovalRequest badReq = govService.CreateJoinApprovalRequest(
                bob, groupA.GroupId, new NodeId("node_bob"), joinPassword: "WrongPassword");
            Assert(!govService.TryProcessJoinApprovalRequest(groupA, badReq, out _),
                "Wrong group join password must be rejected.");

            JoinApprovalRequest bobReq = govService.CreateJoinApprovalRequest(
                bob, groupA.GroupId, new NodeId("node_bob"), joinPassword: "JoinSecret!42");
            Assert(govService.TryProcessJoinApprovalRequest(groupA, bobReq, out _),
                "Valid join request must be queued.");

            JoinApprovalRequest charlieReq = govService.CreateJoinApprovalRequest(
                charlie, groupA.GroupId, new NodeId("node_charlie"), joinPassword: "JoinSecret!42");
            Assert(govService.TryProcessJoinApprovalRequest(groupA, charlieReq, out _),
                "Charlie join request must be queued.");

            Assert(govService.GetPendingJoinRequests(groupA.GroupId).Count == 2, "Expected 2 pending join requests.");

            govService.AcceptPendingMember(alice, groupA, bobProf.AuthorId);
            govService.AcceptPendingMember(alice, groupA, charlieProf.AuthorId);
            Assert(govService.GetPendingJoinRequests(groupA.GroupId).Count == 0, "Pending queue must be empty after accept.");

            // 3. Anti-entropy sync & non-friend fallback via GroupMeshService
            using var aliceTransport = IrohEndpointAdapter.CreateAndBind("/whispernet/grp2-a/1", enableRelay: false, onlineTimeoutMs: 500);
            using var charlieTransport = IrohEndpointAdapter.CreateAndBind("/whispernet/grp2-c/1", enableRelay: false, onlineTimeoutMs: 500);

            var aliceMesh = new GroupMeshService(aliceTransport);
            var charlieMesh = new GroupMeshService(charlieTransport);

            aliceMesh.RegisterGroup(groupA);
            GroupRosterSyncPayload syncPayload = aliceMesh.BuildSyncPayload(groupA.GroupId);
            Assert(charlieMesh.ApplySyncPayload(syncPayload, accountService.VerifySignature, out string? syncErr),
                $"Charlie sync failed: {syncErr}");

            // Charlie has 0 online friends -> falls back to non-friend Admitted room members (Alice & Bob)
            IReadOnlyList<NodeId> fallbackTargets = charlieMesh.GetReconnectTargets(groupA.GroupId, onlineFriends: Array.Empty<NodeId>());
            Assert(fallbackTargets.Contains(new NodeId("node_alice")) && fallbackTargets.Contains(new NodeId("node_bob")),
                "Zero-friends fallback must return Admitted non-friend room EndpointIds.");

            // 4. Multi-group QUIC ref-counting: Bob is in both groupA and groupB
            ModerationAction admitBobInB = govService.SignModerationAction(
                alice, groupB, bobProf.AuthorId, bobProf.PublicKeySpkiBase64, MemberStatus.Admitted, "Welcome", new NodeId("node_bob"));
            Assert(GroupGovernanceEngine.TryApplyModerationAction(groupB, admitBobInB, accountService.VerifySignature, out _, out _),
                "Admit Bob in groupB failed.");
            aliceMesh.RegisterGroup(groupB);

            Assert(!aliceMesh.ShouldClosePhysicalConnection(new NodeId("node_bob"), groupA.GroupId.Value),
                "Must NOT close physical QUIC link to Bob while groupB still uses it.");

            // 5. Ban Bob1 & reject new account Bob2 (Unknown) + Silenced hop-1 filtering
            ModerationAction silenceCharlie = govService.SignModerationAction(
                alice, groupA, charlieProf.AuthorId, charlieProf.PublicKeySpkiBase64, MemberStatus.Silenced, "Cooldown", new NodeId("node_charlie"));
            Assert(GroupGovernanceEngine.TryApplyModerationAction(groupA, silenceCharlie, accountService.VerifySignature, out _, out _),
                "Silence Charlie failed.");
            Assert(!aliceMesh.CanAcceptGroupPost(groupA.GroupId, charlieProf.AuthorId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                "Silenced author post must be rejected.");

            ModerationAction banBob = govService.SignModerationAction(
                alice, groupA, bobProf.AuthorId, bobProf.PublicKeySpkiBase64, MemberStatus.Banned, "Ban", new NodeId("node_bob"));
            Assert(GroupGovernanceEngine.TryApplyModerationAction(groupA, banBob, accountService.VerifySignature, out _, out _),
                "Ban Bob failed.");
            Assert(!aliceMesh.CanAcceptGroupPost(groupA.GroupId, bobProf.AuthorId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                "Banned author post must be rejected.");
            Assert(!aliceMesh.CanAcceptGroupPost(groupA.GroupId, bob2Prof.AuthorId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                "Unknown new account Bob2 must be rejected.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static async Task TestLiveInvitationLinkAndPreApprovalAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"whispernet_inv_test_{Guid.NewGuid():N}");
        try
        {
            var accountService = new AccountService(tempRoot);
            var govService = new GroupGovernanceService(accountService);

            AccountProfile aliceProf = await accountService.CreateAccountAsync("Alice", null);
            AccountProfile bobProf = await accountService.CreateAccountAsync("Bob", null);
            using AccountSession alice = await accountService.UnlockAccountAsync(aliceProf.AuthorId);
            using AccountSession bob = await accountService.UnlockAccountAsync(bobProf.AuthorId);

            const string alpn = "/whispernet/invite-test/1";
            using var aliceTransport = IrohEndpointAdapter.CreateAndBind(alpn: alpn, enableRelay: true, onlineTimeoutMs: 3000);
            using var bobTransport = IrohEndpointAdapter.CreateAndBind(alpn: alpn, enableRelay: true, onlineTimeoutMs: 3000);

            GroupGovernanceState aliceGroup = await govService.CreateGroupAsync(
                alice, "VIP Room", joinPassword: null, creatorNodeId: aliceTransport.SelfId);

            // 1. Non-moderator (Bob) cannot create an invitation link
            bool nonModRejected = false;
            try
            {
                govService.CreateInvitationLink(bob, aliceGroup, bobTransport.SelfId);
            }
            catch (UnauthorizedAccessException)
            {
                nonModRejected = true;
            }
            Assert(nonModRejected, "Non-moderator must be blocked from creating invitation links.");

            // 2. Moderator (Alice) creates a pre-approved invite link for Bob
            string inviteLink = govService.CreateInvitationLink(
                alice, aliceGroup, aliceTransport.SelfId, preApprove: true, targetAuthorId: bobProf.AuthorId);
            Assert(inviteLink.StartsWith("whispernet://invite/", StringComparison.Ordinal), "Invalid invite link prefix.");

            var aliceMesh = new GroupMeshService(aliceTransport);
            var bobMesh = new GroupMeshService(bobTransport);
            aliceMesh.RegisterGroup(aliceGroup);

            var bobSyncedTcs = new TaskCompletionSource<GroupRosterSyncPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var aliceChatTcs = new TaskCompletionSource<DirectChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            aliceMesh.JoinRequestReceived += req =>
            {
                if (govService.TryProcessJoinApprovalRequest(aliceGroup, req, out _, out ModerationAction? autoAdmitted, alice) &&
                    autoAdmitted is not null)
                {
                    aliceMesh.RegisterGroup(aliceGroup);
                    _ = aliceMesh.SendSyncPayloadAsync(req.RequesterNodeId, aliceMesh.BuildSyncPayload(aliceGroup.GroupId));
                }
            };
            aliceMesh.MessageReceived += msg => aliceChatTcs.TrySetResult(msg);

            bobMesh.RosterSyncReceived += sync =>
            {
                if (bobMesh.ApplySyncPayload(sync, accountService.VerifySignature, out _))
                {
                    bobSyncedTcs.TrySetResult(sync);
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            aliceTransport.StartAcceptLoop(aliceMesh.HandleIncomingPayload, cts.Token);
            bobTransport.StartAcceptLoop(bobMesh.HandleIncomingPayload, cts.Token);

            // 3. Bob parses the link, sends JoinApprovalRequest over live QUIC, gets auto-admitted & synced
            GroupInvitationData parsed = GroupGovernanceService.ParseInvitationLink(inviteLink);
            JoinApprovalRequest bobReq = govService.CreateJoinApprovalRequest(
                bob, parsed.GroupId, bobTransport.SelfId, preApprovalTicket: parsed.PreApprovalTicket);
            await bobMesh.SendJoinRequestAsync(parsed.BootstrapNodeId, bobReq, cts.Token);

            GroupRosterSyncPayload synced = await bobSyncedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
            Assert(synced.Manifest.Title == "VIP Room", "Synced manifest title mismatch.");
            Assert(aliceGroup.GetMemberStatus(bobProf.AuthorId) == MemberStatus.Admitted, "Bob must be auto-admitted.");

            // 4. Bob sends a governed chat message to Alice
            await bobMesh.SendMessageAsync(parsed.GroupId.Value, "Bob", "Thanks for the pre-approved invite!", cts.Token, bobProf.AuthorId);
            DirectChatMessage received = await aliceChatTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            Assert(received.Content == "Thanks for the pre-approved invite!", "Chat message content mismatch.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static async Task TestLiveDagTopologicalSliceSyncAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"whispernet_dagsync_test_{Guid.NewGuid():N}");
        try
        {
            var accountService = new AccountService(tempRoot);
            var chatEventService = new ChatEventService(accountService);

            AccountProfile aliceProf = await accountService.CreateAccountAsync("Alice", null);
            AccountProfile bobProf = await accountService.CreateAccountAsync("Bob", null);
            using AccountSession alice = await accountService.UnlockAccountAsync(aliceProf.AuthorId);
            using AccountSession bob = await accountService.UnlockAccountAsync(bobProf.AuthorId);

            using var groupKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            GroupId groupId = GroupId.FromOwnerPublicKeySpki(groupKey.ExportSubjectPublicKeyInfo());
            byte[] roomIdBytes = Convert.FromHexString(groupId.Value);

            var aliceDag = new ChatRoomDagState(roomIdBytes);
            var bobDag = new ChatRoomDagState(roomIdBytes);

            ulong t0 = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Shared prefix E1 -> E2 on both Alice and Bob
            ChatEvent e1 = chatEventService.CreateSignedEvent(alice, roomIdBytes, Array.Empty<byte[]>(), 1, t0 + 1, "E1");
            ChatEvent e2 = chatEventService.CreateSignedEvent(bob, roomIdBytes, new[] { e1.EventId }, 2, t0 + 2, "E2");
            Assert(chatEventService.TryIngestEvent(aliceDag, e1, out _), "Alice E1");
            Assert(chatEventService.TryIngestEvent(aliceDag, e2, out _), "Alice E2");
            Assert(chatEventService.TryIngestEvent(bobDag, e1, out _), "Bob E1");
            Assert(chatEventService.TryIngestEvent(bobDag, e2, out _), "Bob E2");

            // Alice goes offline while Bob creates 1,050 new events (3 batches: 500 + 500 + 50)
            for (int i = 0; i < 1050; i++)
            {
                (IReadOnlyList<byte[]> parents, ulong nextClock) = CausalDagEngine.PrepareNextEventCausalContext(bobDag);
                ChatEvent next = chatEventService.CreateSignedEvent(
                    bob, roomIdBytes, parents, nextClock, t0 + (ulong)(10 + i), $"Msg-{i}");
                Assert(chatEventService.TryIngestEvent(bobDag, next, out string? err), $"Bob ingest {i}: {err}");
            }
            Assert(bobDag.SortedHistory.Count == 1052, "Bob must have 1052 events.");

            const string alpn = "/whispernet/dag-sync-test/1";
            using var aliceTransport = IrohEndpointAdapter.CreateAndBind(alpn: alpn, enableRelay: true, onlineTimeoutMs: 3000);
            using var bobTransport = IrohEndpointAdapter.CreateAndBind(alpn: alpn, enableRelay: true, onlineTimeoutMs: 3000);

            var aliceMesh = new GroupMeshService(aliceTransport);
            var bobMesh = new GroupMeshService(bobTransport);

            var syncCompletedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int receivedBatches = 0;

            bobMesh.DagSyncRequestReceived += req =>
            {
                _ = bobMesh.StreamTopologicalSliceAsync(req.RequesterNodeId, req.GroupId, bobDag, req.KnownHeadIds);
            };

            aliceMesh.DagSyncBatchReceived += batch =>
            {
                Assert(chatEventService.TryIngestBatch(aliceDag, batch.Events, out string? err), $"Alice batch ingest: {err}");

                if (Interlocked.Increment(ref receivedBatches) == batch.TotalBatches)
                {
                    syncCompletedTcs.TrySetResult(true);
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            aliceTransport.StartAcceptLoop(aliceMesh.HandleIncomingPayload, cts.Token);
            bobTransport.StartAcceptLoop(bobMesh.HandleIncomingPayload, cts.Token);

            // Alice sends only her HeadIds ([E2]) over stream 0x02
            await aliceMesh.SendDagSyncRequestAsync(bobTransport.SelfId, groupId, aliceDag, cts.Token);
            await syncCompletedTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

            Assert(receivedBatches == 3, $"Expected 3 batches (500+500+50), got {receivedBatches}.");
            Assert(aliceDag.SortedHistory.Count == 1052, $"Expected 1052 events on Alice, got {aliceDag.SortedHistory.Count}.");
            Assert(aliceDag.SortedHistory[^1].EventIdHex == bobDag.SortedHistory[^1].EventIdHex,
                "Alice and Bob must converge to identical DAG head.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void TestPeerActivityTimetableAndProphetRelays()
    {
        var selfId = new AuthorId(new string('a', 64));
        var recipientId = new AuthorId(new string('b', 64));
        var relay1 = new AuthorId(new string('1', 64)); // Matches recipient weekday 14h & weekend 20h
        var relay2 = new AuthorId(new string('2', 64)); // Matches recipient weekday 14h
        var relay3 = new AuthorId(new string('3', 64)); // Matches recipient weekend 20h
        var relay4 = new AuthorId(new string('4', 64)); // Opposite hours (03h)

        // Build a governed group with self, recipient, and 4 mutual peers
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] spki = ecdsa.ExportSubjectPublicKeyInfo();
        string spkiB64 = Convert.ToBase64String(spki);
        var groupId = GroupId.FromOwnerPublicKeySpki(spki);
        var manifest = new GroupManifest(groupId, spkiB64, 1, "MutualGroup", false, null, null, null, false,
            new Dictionary<AuthorId, string> { [selfId] = spkiB64 }, DateTimeOffset.UtcNow, "sig", 0, null,
            new[] { new GroupChannel(ChannelId.DefaultGeneralFor(groupId), "general", DateTimeOffset.UtcNow) });
        var gov = new GroupGovernanceState(manifest);
        void AddMember(AuthorId id, string nodeName)
        {
            gov.MemberStates[id] = new ModerationAction(groupId, 1, 1, id, spkiB64, new NodeId(nodeName),
                MemberStatus.Admitted, selfId, "ok", DateTimeOffset.UtcNow, "sig");
        }
        AddMember(selfId, "node-self");
        AddMember(recipientId, "node-rec");
        AddMember(relay1, "node-r1");
        AddMember(relay2, "node-r2");
        AddMember(relay3, "node-r3");
        AddMember(relay4, "node-r4");

        // Use Monday 14:00 local and Saturday 20:00 local
        var weekday14 = new DateTimeOffset(2026, 11, 23, 14, 0, 0, TimeSpan.Zero).ToLocalTime();
        var weekday03 = new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero).ToLocalTime();
        var weekend20 = new DateTimeOffset(2026, 11, 28, 20, 0, 0, TimeSpan.Zero).ToLocalTime();

        var profiles = new Dictionary<AuthorId, PeerActivityProfile>
        {
            [recipientId] = new PeerActivityProfile(recipientId, new NodeId("node-rec")),
            [relay1] = new PeerActivityProfile(relay1, new NodeId("node-r1")),
            [relay2] = new PeerActivityProfile(relay2, new NodeId("node-r2")),
            [relay3] = new PeerActivityProfile(relay3, new NodeId("node-r3")),
            [relay4] = new PeerActivityProfile(relay4, new NodeId("node-r4"))
        };

        for (int i = 0; i < 5; i++)
        {
            PeerActivityTimetable.RecordObservation(profiles[recipientId], weekday14);
            PeerActivityTimetable.RecordObservation(profiles[recipientId], weekend20);
            PeerActivityTimetable.RecordObservation(profiles[relay1], weekday14);
            PeerActivityTimetable.RecordObservation(profiles[relay1], weekend20);
            PeerActivityTimetable.RecordObservation(profiles[relay2], weekday14);
            PeerActivityTimetable.RecordObservation(profiles[relay3], weekend20);
            PeerActivityTimetable.RecordObservation(profiles[relay4], weekday03);
        }

        var prophet = new Dictionary<AuthorId, double>
        {
            [relay1] = 0.85,
            [relay2] = 0.75,
            [relay3] = 0.65,
            [relay4] = 0.10
        };

        IReadOnlyList<RelayCandidateScore> top3 = ProphetRoutingEngine.SelectTopRelays(
            selfId, recipientId, new[] { gov }, profiles, prophet, weekday14, maxRelays: 3);

        Assert(top3.Count == 3, $"Expected top 3 relays, got {top3.Count}.");
        Assert(top3[0].AuthorId == relay1, "Relay1 (best weekday+weekend match) must rank first.");
        Assert(!top3.Any(r => r.AuthorId == relay4), "Relay4 (mismatching 03h schedule) must be excluded from top 3.");
    }

    private static async Task TestDirectMessageCryptoAndDedupAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"whispernet_dm_crypto_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var svcA = new AccountService(Path.Combine(tempRoot, "a"));
            var svcB = new AccountService(Path.Combine(tempRoot, "b"));
            AccountProfile profA = await svcA.CreateAccountAsync("Alice");
            AccountProfile profB = await svcB.CreateAccountAsync("Bob");
            using AccountSession sessA = await svcA.UnlockAccountAsync(profA.AuthorId);
            using AccountSession sessB = await svcB.UnlockAccountAsync(profB.AuthorId);

            byte[] secretAB = svcA.DerivePairSecret(sessA, Convert.FromBase64String(profB.PublicKeySpkiBase64));
            byte[] secretBA = svcB.DerivePairSecret(sessB, Convert.FromBase64String(profA.PublicKeySpkiBase64));
            Assert(secretAB.SequenceEqual(secretBA), "ECDH P-256 pair_secret must be symmetric.");

            string tag1 = DirectMessagingService.ComputeBlindRoutingTagHex(secretAB, 100);
            string tag2 = DirectMessagingService.ComputeBlindRoutingTagHex(secretBA, 100);
            string tagNext = DirectMessagingService.ComputeBlindRoutingTagHex(secretAB, 101);
            Assert(tag1 == tag2 && tag1.Length == 64, "Blind routing tag must match for the same epoch.");
            Assert(tag1 != tagNext, "Blind routing tag must rotate across epochs.");

            var repo = new SqliteDirectMessageRepository(WhispernetPaths.GetDirectMessagesDbPath(tempRoot));
            await repo.InitializeAsync();
            Guid uuid = Guid.NewGuid();
            var rec = new DirectMessageRecord(uuid, profA.AuthorId, false, "Hello via QUIC+DTN", DirectMessageStatus.Delivered, DateTimeOffset.UtcNow);
            bool firstInsert = await repo.TryInsertMessageAsync(rec);
            bool secondInsert = await repo.TryInsertMessageAsync(rec);
            Assert(firstInsert && !secondInsert, "SQLite direct_messages must deduplicate identical MessageUuid.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static async Task TestLiveDirectMessagingAckAndDtnFallbackAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"whispernet_dm_live_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var svcA = new AccountService(Path.Combine(tempRoot, "alice"));
            var svcB = new AccountService(Path.Combine(tempRoot, "bob"));
            var svcR = new AccountService(Path.Combine(tempRoot, "relay"));

            AccountProfile profA = await svcA.CreateAccountAsync("Alice");
            AccountProfile profB = await svcB.CreateAccountAsync("Bob");
            AccountProfile profR = await svcR.CreateAccountAsync("RelayPeer");

            using AccountSession sessA = await svcA.UnlockAccountAsync(profA.AuthorId);
            using AccountSession sessB = await svcB.UnlockAccountAsync(profB.AuthorId);
            using AccountSession sessR = await svcR.UnlockAccountAsync(profR.AuthorId);

            using var tA = IrohEndpointAdapter.CreateAndBind(alpn: "/whispernet/dm-test/1", enableRelay: true, onlineTimeoutMs: 2500);
            using var tB = IrohEndpointAdapter.CreateAndBind(alpn: "/whispernet/dm-test/1", enableRelay: true, onlineTimeoutMs: 2500);
            using var tR = IrohEndpointAdapter.CreateAndBind(alpn: "/whispernet/dm-test/1", enableRelay: true, onlineTimeoutMs: 2500);

            var meshA = new GroupMeshService(tA);
            var meshB = new GroupMeshService(tB);
            var meshR = new GroupMeshService(tR);

            var repoA = new SqliteDirectMessageRepository(WhispernetPaths.GetDirectMessagesDbPath(Path.Combine(tempRoot, "alice")));
            var repoB = new SqliteDirectMessageRepository(WhispernetPaths.GetDirectMessagesDbPath(Path.Combine(tempRoot, "bob")));
            var repoR = new SqliteDirectMessageRepository(WhispernetPaths.GetDirectMessagesDbPath(Path.Combine(tempRoot, "relay")));
            await repoA.InitializeAsync();
            await repoB.InitializeAsync();
            await repoR.InitializeAsync();

            // Shared governed group so RelayPeer is a mutual friend in the same group
            var groupId = GroupId.FromOwnerPublicKeySpki(Convert.FromBase64String(profA.PublicKeySpkiBase64));
            var manifest = new GroupManifest(groupId, profA.PublicKeySpkiBase64, 1, "DmTestGroup", false, null, null, null, false,
                new Dictionary<AuthorId, string> { [profA.AuthorId] = profA.PublicKeySpkiBase64 }, DateTimeOffset.UtcNow, "sig", 0, null,
                new[] { new GroupChannel(ChannelId.DefaultGeneralFor(groupId), "general", DateTimeOffset.UtcNow) });
            var gov = new GroupGovernanceState(manifest);
            gov.MemberStates[profA.AuthorId] = new ModerationAction(groupId, 1, 1, profA.AuthorId, profA.PublicKeySpkiBase64, tA.SelfId, MemberStatus.Admitted, profA.AuthorId, "owner", DateTimeOffset.UtcNow, "sig");
            gov.MemberStates[profB.AuthorId] = new ModerationAction(groupId, 1, 2, profB.AuthorId, profB.PublicKeySpkiBase64, tB.SelfId, MemberStatus.Admitted, profA.AuthorId, "ok", DateTimeOffset.UtcNow, "sig");
            gov.MemberStates[profR.AuthorId] = new ModerationAction(groupId, 1, 3, profR.AuthorId, profR.PublicKeySpkiBase64, tR.SelfId, MemberStatus.Admitted, profA.AuthorId, "ok", DateTimeOffset.UtcNow, "sig");

            using var dmA = new DirectMessagingService(svcA, sessA, tA, meshA, repoA, () => new[] { gov });
            using var dmB = new DirectMessagingService(svcB, sessB, tB, meshB, repoB, () => new[] { gov });
            using var dmR = new DirectMessagingService(svcR, sessR, tR, meshR, repoR, () => new[] { gov });

            dmA.RegisterContactsFromGroup(gov);
            dmB.RegisterContactsFromGroup(gov);
            dmR.RegisterContactsFromGroup(gov);
            dmA.RecordPeerActivity(profR.AuthorId, tR.SelfId);
            dmA.RecordPeerActivity(profB.AuthorId, tB.SelfId);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            tA.StartAcceptLoop(meshA.HandleIncomingPayload, cts.Token);
            tB.StartAcceptLoop(meshB.HandleIncomingPayload, cts.Token);
            tR.StartAcceptLoop(meshR.HandleIncomingPayload, cts.Token);

            // 1. Live direct QUIC stream + immediate ACK + typing indicator
            var bobGotLive = new TaskCompletionSource<DirectMessageRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            var aliceGotAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bobGotTyping = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            dmB.DirectMessageReceived += rec => bobGotLive.TrySetResult(rec);
            dmB.TypingIndicatorReceived += t => { if (t.IsTyping) bobGotTyping.TrySetResult(true); };
            Guid liveUuid = Guid.Empty;
            dmA.MessageStatusChanged += (uuid, st, _) =>
            {
                if (uuid == liveUuid && st == DirectMessageStatus.Delivered)
                {
                    aliceGotAck.TrySetResult(true);
                }
            };

            DirectMessageRecord liveRec = await dmA.SendDirectMessageAsync(profB.AuthorId, "Live QUIC 1-on-1 hello!", ct: cts.Token);
            liveUuid = liveRec.MessageUuid;
            DirectMessageRecord deliveredB = await bobGotLive.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await aliceGotAck.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            Assert(deliveredB.MessageUuid == liveUuid && deliveredB.Content == "Live QUIC 1-on-1 hello!", "Bob must receive live 1-on-1 message.");

            await dmA.SendTypingIndicatorAsync(profB.AuthorId, isTyping: true, cts.Token);
            Assert(await bobGotTyping.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token), "Bob must receive ephemeral typing indicator.");

            // 2. Unreachable recipient -> Blind-tagged DTN bundle handed to RelayPeer -> QueuedViaRelay -> Bob pulls on reconnect
            var offlinePeerId = new AuthorId(new string('f', 64));
            using var offlineEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string offlineSpkiB64 = Convert.ToBase64String(offlineEcdsa.ExportSubjectPublicKeyInfo());
            // Use Bob's real SPKI with an unreachable NodeId so Bob can later pull & decrypt from RelayPeer!
            var unreachableNode = new NodeId(new string('0', 64));
            var relayStoredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queuedViaRelayTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            meshR.DtnBundleReceived += _ => relayStoredTcs.TrySetResult(true);
            dmA.MessageStatusChanged += (_, st, _) =>
            {
                if (st == DirectMessageStatus.QueuedViaRelay)
                {
                    queuedViaRelayTcs.TrySetResult(true);
                }
            };

            DirectMessageRecord queuedRec = await dmA.SendDirectMessageAsync(
                profB.AuthorId,
                "Offline DTN blind bundle!",
                recipientNodeId: unreachableNode,
                ct: cts.Token);
            await queuedViaRelayTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
            await relayStoredTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

            var bobGotDtn = new TaskCompletionSource<DirectMessageRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            dmB.DirectMessageReceived += rec =>
            {
                if (rec.MessageUuid == queuedRec.MessageUuid)
                {
                    bobGotDtn.TrySetResult(rec);
                }
            };

            // Bob reconnects and queries RelayPeer with its blind tags
            await dmB.QueryPeerForBlindTaggedBundlesAsync(tR.SelfId, DateTimeOffset.UtcNow);
            DirectMessageRecord dtnDeliveredB = await bobGotDtn.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            Assert(dtnDeliveredB.Content == "Offline DTN blind bundle!", "Bob must decrypt blind-tagged DTN bundle from RelayPeer.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static async Task TestDaemonUnattendedBlindRelayAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"whispernet_daemon_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. Start unattended WhispernetDaemonHost (0 accounts logged in)
            string daemonDir = Path.Combine(tempRoot, "daemon");
            await using var daemon = new WhispernetDaemonHost(daemonDir, "/whispernet/daemon-test/1");
            await daemon.StartAsync();

            // 2. Bob sends a blind-tagged DTN bundle for offline Arvo to the unattended daemon
            var svcBob = new AccountService(Path.Combine(tempRoot, "bob"));
            var svcArvo = new AccountService(Path.Combine(tempRoot, "arvo"));
            AccountProfile profBob = await svcBob.CreateAccountAsync("Bob");
            AccountProfile profArvo = await svcArvo.CreateAccountAsync("Arvo");
            using AccountSession sessBob = await svcBob.UnlockAccountAsync(profBob.AuthorId);
            using AccountSession sessArvo = await svcArvo.UnlockAccountAsync(profArvo.AuthorId);

            using var tBob = IrohEndpointAdapter.CreateAndBind(alpn: "/whispernet/daemon-test/1", enableRelay: true, onlineTimeoutMs: 2500);
            var meshBob = new GroupMeshService(tBob);
            var repoBob = new SqliteDirectMessageRepository(WhispernetPaths.GetDirectMessagesDbPath(Path.Combine(tempRoot, "bob")));
            await repoBob.InitializeAsync();

            var groupId = GroupId.FromOwnerPublicKeySpki(Convert.FromBase64String(profBob.PublicKeySpkiBase64));
            var manifest = new GroupManifest(groupId, profBob.PublicKeySpkiBase64, 1, "DaemonGroup", false, null, null, null, false,
                new Dictionary<AuthorId, string> { [profBob.AuthorId] = profBob.PublicKeySpkiBase64 }, DateTimeOffset.UtcNow, "sig", 0, null,
                new[] { new GroupChannel(ChannelId.DefaultGeneralFor(groupId), "general", DateTimeOffset.UtcNow) });
            var gov = new GroupGovernanceState(manifest);
            gov.MemberStates[profBob.AuthorId] = new ModerationAction(groupId, 1, 1, profBob.AuthorId, profBob.PublicKeySpkiBase64, tBob.SelfId, MemberStatus.Admitted, profBob.AuthorId, "owner", DateTimeOffset.UtcNow, "sig");
            gov.MemberStates[profArvo.AuthorId] = new ModerationAction(groupId, 1, 2, profArvo.AuthorId, profArvo.PublicKeySpkiBase64, new NodeId(new string('0', 64)), MemberStatus.Admitted, profBob.AuthorId, "ok", DateTimeOffset.UtcNow, "sig");
            var relayAuthor = new AuthorId(new string('d', 64));
            gov.MemberStates[relayAuthor] = new ModerationAction(groupId, 1, 3, relayAuthor, profBob.PublicKeySpkiBase64, daemon.SelfId, MemberStatus.Admitted, profBob.AuthorId, "daemon", DateTimeOffset.UtcNow, "sig");

            using var dmBob = new DirectMessagingService(svcBob, sessBob, tBob, meshBob, repoBob, () => new[] { gov });
            dmBob.RegisterContactsFromGroup(gov);
            dmBob.RecordPeerActivity(relayAuthor, daemon.SelfId);

            var daemonStored = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            daemon.LogEmitted += log => { if (log.Contains("Stored blind DTN bundle", StringComparison.Ordinal)) daemonStored.TrySetResult(true); };

            await dmBob.SendDirectMessageAsync(profArvo.AuthorId, "Unattended daemon relay message!");
            await daemonStored.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // 3. Arvo logs in later and registers its rotating BlindTags with the unattended daemon
            byte[] pairSecret = svcArvo.DerivePairSecret(sessArvo, Convert.FromBase64String(profBob.PublicKeySpkiBase64));
            long epoch = dmBob.GetEpoch(DateTimeOffset.UtcNow);
            string expectedTag = DirectMessagingService.ComputeBlindRoutingTagHex(pairSecret, epoch);

            IReadOnlyList<byte[]> claimed = await daemon.RegisterWatchTagsAsync(
                new RegisterWatchTagsRequest(profArvo.AuthorId, new[] { expectedTag }));

            Assert(claimed.Count == 1, $"Expected daemon to return 1 matching blind-tagged bundle for Arvo, got {claimed.Count}.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void TestFriendGraphEngineProtocol()
    {
        using var aliceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bobKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] aliceSpki = aliceKey.ExportSubjectPublicKeyInfo();
        byte[] bobSpki = bobKey.ExportSubjectPublicKeyInfo();
        string aliceSpkiB64 = Convert.ToBase64String(aliceSpki);
        string bobSpkiB64 = Convert.ToBase64String(bobSpki);
        var aliceId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(aliceSpki)));
        var bobId = new AuthorId(Convert.ToHexStringLower(SHA256.HashData(bobSpki)));

        static bool VerifySig(ReadOnlySpan<byte> spki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256);
        }

        var bobState = new FriendGraphState(bobId);
        var reqUnsigned = new FriendRequestPacket(
            Guid.NewGuid(), aliceId, bobId, aliceSpkiB64, "Alice", new NodeId("node-alice"), 1, DateTimeOffset.UtcNow, string.Empty);
        var reqSigned = reqUnsigned with { SignatureBase64 = Convert.ToBase64String(aliceKey.SignData(reqUnsigned.BuildSignableBytes(), HashAlgorithmName.SHA256)) };

        // Forged signature must fail
        var forged = reqSigned with { SenderDisplayName = "Mallory" };
        Assert(!FriendGraphEngine.TryApplyIncomingRequest(bobState, forged, VerifySig, out _, out _), "Forged friend request should be rejected.");

        // Valid incoming request
        Assert(FriendGraphEngine.TryApplyIncomingRequest(bobState, reqSigned, VerifySig, out bool autoAccepted, out string? err), $"Valid request failed: {err}");
        Assert(!autoAccepted && bobState.GetStatus(aliceId) == FriendStatus.PendingIncoming, "Expected PendingIncoming.");

        // Stale request with same/lower clock must fail
        Assert(!FriendGraphEngine.TryApplyIncomingRequest(bobState, reqSigned, VerifySig, out _, out _), "Stale clock request should be rejected.");

        // Accept on Alice's side via FriendResponsePacket
        var aliceState = new FriendGraphState(aliceId);
        aliceState.Entries[bobId] = new FriendEntry(bobId, bobSpkiB64, "Bob", new NodeId("node-bob"), FriendStatus.PendingOutgoing, 1, DateTimeOffset.UtcNow);
        var respUnsigned = new FriendResponsePacket(
            Guid.NewGuid(), bobId, aliceId, bobSpkiB64, "Bob", new NodeId("node-bob"), Accepted: true, LogicalClock: 2, DateTimeOffset.UtcNow, string.Empty);
        var respSigned = respUnsigned with { SignatureBase64 = Convert.ToBase64String(bobKey.SignData(respUnsigned.BuildSignableBytes(), HashAlgorithmName.SHA256)) };
        Assert(FriendGraphEngine.TryApplyIncomingResponse(aliceState, respSigned, VerifySig, out err), $"Valid accept response failed: {err}");
        Assert(aliceState.IsFriend(bobId), "Alice should now have Bob as Accepted friend.");

        // Remove on Alice's side via FriendRemovePacket from Bob
        var remUnsigned = new FriendRemovePacket(Guid.NewGuid(), bobId, aliceId, bobSpkiB64, 3, DateTimeOffset.UtcNow, string.Empty);
        var remSigned = remUnsigned with { SignatureBase64 = Convert.ToBase64String(bobKey.SignData(remUnsigned.BuildSignableBytes(), HashAlgorithmName.SHA256)) };
        Assert(FriendGraphEngine.TryApplyIncomingRemove(aliceState, remSigned, VerifySig, out err), $"Valid remove failed: {err}");
        Assert(!aliceState.IsFriend(bobId) && aliceState.GetStatus(bobId) == FriendStatus.Removed, "Expected Removed status.");

        // Simultaneous mutual friend request -> autoAccepted
        var mutualState = new FriendGraphState(bobId);
        mutualState.Entries[aliceId] = new FriendEntry(aliceId, aliceSpkiB64, "Alice", null, FriendStatus.PendingOutgoing, 1, DateTimeOffset.UtcNow);
        Assert(FriendGraphEngine.TryApplyIncomingRequest(mutualState, reqSigned, VerifySig, out bool mutualAccepted, out err), $"Mutual request failed: {err}");
        Assert(mutualAccepted && mutualState.IsFriend(aliceId), "Simultaneous mutual request should auto-promote to Accepted.");
    }

    private static async Task TestFriendServiceVaultAndPolicyAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"whispernet_friend_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var accountService = new AccountService(tempRoot);
            AccountProfile profAlice = await accountService.CreateAccountAsync("Alice", null);
            AccountProfile profBob = await accountService.CreateAccountAsync("Bob", null);
            using AccountSession sessAlice = await accountService.UnlockAccountAsync(profAlice.AuthorId, null);
            using AccountSession sessBob = await accountService.UnlockAccountAsync(profBob.AuthorId, null);

            var aliceFriends = new FriendService(accountService);
            var bobFriends = new FriendService(accountService);
            await aliceFriends.LoadFromVaultAsync(sessAlice);
            await bobFriends.LoadFromVaultAsync(sessBob);

            string bobLink = bobFriends.CreateFriendLink(sessBob, new NodeId("node-bob"));
            FriendInvitationData parsed = FriendService.ParseFriendLink(bobLink);
            Assert(parsed.AuthorId == profBob.AuthorId && parsed.DisplayName == "Bob", "Friend link round-trip mismatch.");

            // Alice sends request to Bob (local account on same root -> writes to [TEST-ONLY] test-loopback-friend-inbox)
            FriendEntry outEntry = await aliceFriends.SendFriendRequestAsync(
                parsed.AuthorId, parsed.PublicKeySpkiBase64, parsed.DisplayName, recipientNodeId: null);
            Assert(outEntry.Status == FriendStatus.PendingOutgoing, "Expected PendingOutgoing on Alice.");

            // Bob consumes the [TEST-ONLY] loopback inbox and sees Alice's incoming request
            await bobFriends.ConsumeTestLoopbackInboxAsync();
            Assert(bobFriends.GetIncomingRequests().Count == 1, "Expected 1 incoming request on Bob via loopback inbox.");

            // Bob accepts
            await bobFriends.AcceptFriendRequestAsync(profAlice.AuthorId);
            Assert(bobFriends.IsFriend(profAlice.AuthorId), "Bob should have Alice as friend.");

            // Test same-daemon DM loopback between Bob and Alice
            var sameDaemonNode = new NodeId("same-daemon-node");
            using var sharedTransport = new SameDaemonLoopbackTestTransport(sameDaemonNode);
            var sharedMesh = new GroupMeshService(sharedTransport);
            var bobDmRepo = new SqliteDirectMessageRepository(WhispernetPaths.GetDirectMessagesDbPath(tempRoot, profBob.AuthorId));
            var aliceDmRepo = new SqliteDirectMessageRepository(WhispernetPaths.GetDirectMessagesDbPath(tempRoot, profAlice.AuthorId));
            await bobDmRepo.InitializeAsync();
            await aliceDmRepo.InitializeAsync();
            using var bobDm = new DirectMessagingService(accountService, sessBob, sharedTransport, sharedMesh, bobDmRepo, () => Array.Empty<GroupGovernanceState>());
            using var aliceDm = new DirectMessagingService(accountService, sessAlice, sharedTransport, sharedMesh, aliceDmRepo, () => Array.Empty<GroupGovernanceState>());

            DirectMessageRecord? aliceGotDm = null;
            aliceDm.DirectMessageReceived += rec => aliceGotDm = rec;

            DirectMessageRecord sentDm = await bobDm.SendDirectMessageAsync(
                profAlice.AuthorId,
                "Hello Alice on same daemon!",
                profAlice.PublicKeySpkiBase64,
                sameDaemonNode);
            Assert(sentDm.Status == DirectMessageStatus.Delivered, $"Expected Delivered via same-daemon loopback, got {sentDm.Status}.");

            await aliceDm.ConsumeTestLoopbackInboxAsync();
            Assert(aliceGotDm is not null && aliceGotDm.Content == "Hello Alice on same daemon!", "Alice should receive Bob's DM via same-daemon loopback.");

            // Test DM policy toggle & vault persistence
            Assert(bobFriends.IsAuthorAllowedForDm(new AuthorId("stranger")), "By default non-friends should be allowed for DMs.");
            await bobFriends.SetRestrictDmsToFriendsAsync(true);
            Assert(!bobFriends.IsAuthorAllowedForDm(new AuthorId("stranger")), "Non-friend should be blocked when RestrictDmsToFriends=true.");
            Assert(bobFriends.IsAuthorAllowedForDm(profAlice.AuthorId), "Accepted friend should remain allowed.");

            // Reload from vault and verify persistence
            var bobReloaded = new FriendService(accountService);
            await bobReloaded.LoadFromVaultAsync(sessBob);
            Assert(bobReloaded.IsFriend(profAlice.AuthorId), "Accepted friend should persist in vault.");
            Assert(bobReloaded.RestrictDmsToFriends, "RestrictDmsToFriends setting should persist in vault.");

            // Remove friend
            await bobReloaded.RemoveFriendAsync(profAlice.AuthorId);
            Assert(!bobReloaded.IsFriend(profAlice.AuthorId), "Friend should be removed.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class SameDaemonLoopbackTestTransport : IMeshTransport
    {
        public SameDaemonLoopbackTestTransport(NodeId selfId) => SelfId = selfId;
        public NodeId SelfId { get; }
        public string SelfTicket => SelfId.Value;
        public bool HasActiveConnection(NodeId target) => false;
        public Task<bool> ConnectSmartAsync(NodeId target, string? cachedTicket = null, bool allowDhtLookup = true, CancellationToken ct = default) => Task.FromResult(false);
        public Task SendUniAsync(NodeId target, byte[] payload, string? cachedTicket = null, bool allowDhtLookup = true, CancellationToken ct = default) => Task.CompletedTask;
        public void CloseConnection(NodeId target) { }
        public void StartAcceptLoop(Action<byte[]> onPayloadReceived, CancellationToken ct = default) { }
        public void Dispose() { }
    }
}
