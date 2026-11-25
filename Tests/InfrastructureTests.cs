using System.Security.Cryptography;
using System.Text;
using Whispernet.App;
using Whispernet.Core;
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

        // Niveau 2 : Intégration SQLite réelle et Comptes chiffrés (.App)
        await RunTestAsync("2.1 SqlitePeerCacheRepository (CRUD + Ordre + Purge)", TestSqlitePeerCacheRepositoryAsync);
        await RunTestAsync("2.2 AccountService + WhispernetPaths (ECDSA + PBKDF2 + AES-GCM)", TestAccountServiceAsync);

        // Niveau 3 : Réseau réel Iroh QUIC + Mainline DHT (par NodeId seul)
        await RunTestAsync("3.1 Iroh QUIC + Discovery All (DirectMeshService 1-to-1)", TestRealIrohMainlineDhtDirectMessagingAsync);
        await RunTestAsync("3.2 Iroh QUIC + Mainline DHT SEULE (sans mDNS ni n0_dns)", TestRealIrohMainlineDhtIsolatedAsync);

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

        var aliceDirect = new DirectMeshService(aliceTransport);
        var bobDirect = new DirectMeshService(bobTransport);

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

        var aliceDirect = new DirectMeshService(aliceTransport);
        var bobDirect = new DirectMeshService(bobTransport);

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
}
