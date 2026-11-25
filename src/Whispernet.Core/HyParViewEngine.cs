namespace Whispernet.Core;

/// <summary>
/// Implémentation Sans-I/O complète du protocole HyParView (gestion de la Vue Active,
/// de la Vue Passive, du Join/ForwardJoin, du remplacement après panne et du Shuffle).
/// Toutes les fonctions sont pures vis-à-vis du réseau et retournent la liste des <see cref="OutAction"/>.
/// </summary>
public static class HyParViewEngine
{
    private const string LogCat = "HyParView";

    private static void EmitLog(
        HyParViewState state,
        List<OutAction> actions,
        ProtocolLogLevel level,
        string message)
    {
        string viewSummary = $"[Active={state.ActiveView.Count}/{state.Config.MaxActiveViewSize}, Passive={state.PassiveView.Count}/{state.Config.MaxPassiveViewSize}, Pending={state.PendingNeighbors.Count}, Busy={state.BusyPeers.Count}]";
        actions.Add(new OutAction.Log(level, LogCat, $"[Self={state.SelfId}] {message} {viewSummary}"));
    }

    /// <summary>
    /// Initie l'entrée du nœud dans le réseau en contactant un pair d'amorçage (ex: Raspberry Pi Point de Meeting).
    /// </summary>
    public static IReadOnlyList<OutAction> InitiateJoin(HyParViewState state, NodeId contactNode)
    {
        var actions = new List<OutAction>();
        if (contactNode == state.SelfId)
        {
            EmitLog(state, actions, ProtocolLogLevel.Warning, $"InitiateJoin ignoré : contactNode ({contactNode}) == SelfId.");
            return actions;
        }

        EmitLog(state, actions, ProtocolLogLevel.Info, $"InitiateJoin vers le point d'entrée {contactNode} (Role={state.SelfRole}).");
        actions.Add(new OutAction.Connect(contactNode));
        actions.Add(new OutAction.Send(
            contactNode,
            new NetworkMessage.HyParView(new HyParViewMessage.Join(state.SelfId, state.SelfRole))));

        return actions;
    }

    /// <summary>
    /// Marque ou démarque un pair comme ayant une transmission de données en cours,
    /// afin d'éviter son éviction aléatoire de la Vue Active pendant un transfert.
    /// </summary>
    public static void SetPeerBusy(HyParViewState state, NodeId peer, bool isBusy)
    {
        if (isBusy)
        {
            if (state.ActiveView.Contains(peer))
            {
                state.BusyPeers.Add(peer);
            }
        }
        else
        {
            state.BusyPeers.Remove(peer);
        }
    }

    /// <summary>
    /// Point d'entrée unique pour le traitement d'un message HyParView reçu.
    /// </summary>
    public static IReadOnlyList<OutAction> HandleMessage(
        HyParViewState state,
        HyParViewMessage message,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        return message switch
        {
            HyParViewMessage.Join join => OnReceiveJoin(state, join.Sender, join.Role, rng),
            HyParViewMessage.ForwardJoin fwd => OnReceiveForwardJoin(state, fwd.NewNode, fwd.Ttl, fwd.Sender, fwd.Role, rng),
            HyParViewMessage.Neighbor nb => OnReceiveNeighbor(state, nb.Sender, nb.HighPriority, nb.Role, rng),
            HyParViewMessage.NeighborAck ack => OnReceiveNeighborAck(state, ack.Sender, ack.Accepted, ack.Role, rng),
            HyParViewMessage.Disconnect disc => OnReceiveDisconnect(state, disc.Sender, rng),
            HyParViewMessage.Shuffle shuf => OnReceiveShuffle(state, shuf.Origin, shuf.Sample, shuf.Ttl, shuf.Sender, rng),
            HyParViewMessage.ShuffleReply reply => OnReceiveShuffleReply(state, reply.Sender, reply.Sample, rng),
            _ => Array.Empty<OutAction>()
        };
    }

    /// <summary>
    /// Traitement de <c>JOIN</c> :
    /// 1. Libère une place dans la Vue Active si elle est pleine (en déconnectant un pair aléatoire).
    /// 2. Ajoute le nouveau nœud dans la Vue Active.
    /// 3. Propage <c>FORWARD_JOIN(newNode, ttl = ARWL)</c> à tous les autres voisins actifs.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveJoin(
        HyParViewState state,
        NodeId newNode,
        NodeRole role = NodeRole.Peer,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();
        if (newNode == state.SelfId)
        {
            return actions;
        }

        state.KnownRoles[newNode] = role;
        EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception JOIN de {newNode} (Role={role}).");
        AddNodeToActiveView(state, newNode, actions, rng);

        byte arwl = state.Config.ActiveRandomWalkLength;
        foreach (NodeId peer in state.ActiveView)
        {
            if (peer != newNode)
            {
                EmitLog(state, actions, ProtocolLogLevel.Debug, $"Envoi FORWARD_JOIN(newNode={newNode}, ttl={arwl}) à {peer}.");
                actions.Add(new OutAction.Send(
                    peer,
                    new NetworkMessage.HyParView(
                        new HyParViewMessage.ForwardJoin(newNode, arwl, state.SelfId, role))));
            }
        }

        return actions;
    }

    /// <summary>
    /// Traitement de <c>FORWARD_JOIN(newNode, ttl)</c> :
    /// - Si <c>ttl == 0</c> ou si notre Vue Active ne contient qu'un seul voisin : on accepte <c>newNode</c> dans notre Vue Active.
    /// - Si <c>ttl == PRWL</c> : on ajoute <c>newNode</c> dans notre Vue Passive.
    /// - Si <c>newNode</c> n'a pas été intégré à la Vue Active : on relaie <c>FORWARD_JOIN(newNode, ttl - 1)</c> à un voisin actif aléatoire (différent de l'émetteur).
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveForwardJoin(
        HyParViewState state,
        NodeId newNode,
        byte ttl,
        NodeId sender,
        NodeRole role = NodeRole.Peer,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();
        if (newNode == state.SelfId)
        {
            return actions;
        }

        state.KnownRoles[newNode] = role;
        EmitLog(state, actions, ProtocolLogLevel.Debug, $"Réception FORWARD_JOIN(newNode={newNode}, ttl={ttl}) depuis {sender}.");

        if (ttl == 0 || state.ActiveView.Count <= 1)
        {
            if (!state.ActiveView.Contains(newNode))
            {
                EmitLog(state, actions, ProtocolLogLevel.Info, $"FORWARD_JOIN terminé (ttl={ttl}, ActiveCount={state.ActiveView.Count}) -> intégration de {newNode} dans ActiveView.");
                AddNodeToActiveView(state, newNode, actions, rng);
                actions.Add(new OutAction.Connect(newNode));
                actions.Add(new OutAction.Send(
                    newNode,
                    new NetworkMessage.HyParView(
                        new HyParViewMessage.Neighbor(state.SelfId, HighPriority: true, state.SelfRole))));
            }
            return actions;
        }

        if (ttl == state.Config.PassiveRandomWalkLength)
        {
            EmitLog(state, actions, ProtocolLogLevel.Debug, $"FORWARD_JOIN atteint PRWL (ttl={ttl}) -> ajout de {newNode} dans PassiveView.");
            AddNodeToPassiveView(state, newNode, actions, rng);
        }

        // Choisir un voisin actif au hasard différent de sender et de newNode
        var candidates = state.ActiveView
            .Where(p => p != sender && p != newNode)
            .ToList();

        if (candidates.Count > 0)
        {
            NodeId nextHop = candidates[rng.Next(candidates.Count)];
            EmitLog(state, actions, ProtocolLogLevel.Trace, $"Relais FORWARD_JOIN(newNode={newNode}, ttl={ttl - 1}) vers {nextHop}.");
            actions.Add(new OutAction.Send(
                nextHop,
                new NetworkMessage.HyParView(
                    new HyParViewMessage.ForwardJoin(newNode, (byte)(ttl - 1), state.SelfId, role))));
        }
        else if (!state.ActiveView.Contains(newNode))
        {
            // Impasse dans la marche aléatoire : intégrer newNode directement
            EmitLog(state, actions, ProtocolLogLevel.Info, $"Impasse FORWARD_JOIN (aucun autre voisin) -> intégration directe de {newNode} dans ActiveView.");
            AddNodeToActiveView(state, newNode, actions, rng);
            actions.Add(new OutAction.Connect(newNode));
            actions.Add(new OutAction.Send(
                newNode,
                new NetworkMessage.HyParView(
                    new HyParViewMessage.Neighbor(state.SelfId, HighPriority: true, state.SelfRole))));
        }

        return actions;
    }

    /// <summary>
    /// Traitement d'une demande <c>NEIGHBOR(sender, highPriority)</c> :
    /// - Si <c>highPriority</c> est vrai ou si la Vue Active n'est pas pleine : on accepte <c>sender</c> (en évinçant un pair aléatoire si nécessaire) et on répond <c>NeighborAck(Accepted: true)</c>.
    /// - Sinon : on refuse avec <c>NeighborAck(Accepted: false)</c>.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveNeighbor(
        HyParViewState state,
        NodeId sender,
        bool highPriority,
        NodeRole role = NodeRole.Peer,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();
        if (sender == state.SelfId)
        {
            return actions;
        }

        state.KnownRoles[sender] = role;
        state.PendingNeighbors.Remove(sender);

        bool hasSpace = state.ActiveView.Count < state.Config.MaxActiveViewSize;
        if (highPriority || hasSpace || state.ActiveView.Contains(sender))
        {
            EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception NEIGHBOR(highPriority={highPriority}) de {sender} -> ACCEPTÉ.");
            AddNodeToActiveView(state, sender, actions, rng);
            actions.Add(new OutAction.Send(
                sender,
                new NetworkMessage.HyParView(
                    new HyParViewMessage.NeighborAck(state.SelfId, Accepted: true, state.SelfRole))));
        }
        else
        {
            EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception NEIGHBOR(highPriority=false) de {sender} mais ActiveView pleine -> REFUSÉ (ajouté à PassiveView).");
            AddNodeToPassiveView(state, sender, actions, rng);
            actions.Add(new OutAction.Send(
                sender,
                new NetworkMessage.HyParView(
                    new HyParViewMessage.NeighborAck(state.SelfId, Accepted: false, state.SelfRole))));
        }

        return actions;
    }

    /// <summary>
    /// Traitement de la réponse <c>NEIGHBOR_ACK(sender, accepted)</c> :
    /// - Si accepté : on intègre <c>sender</c> dans la Vue Active.
    /// - Si refusé : on ferme la connexion temporaire et on essaie un autre candidat de la Vue Passive.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveNeighborAck(
        HyParViewState state,
        NodeId sender,
        bool accepted,
        NodeRole role = NodeRole.Peer,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();
        state.PendingNeighbors.Remove(sender);
        state.KnownRoles[sender] = role;

        if (accepted)
        {
            EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception NEIGHBOR_ACK(accepted=true) de {sender} -> ajout à ActiveView.");
            AddNodeToActiveView(state, sender, actions, rng);
        }
        else
        {
            EmitLog(state, actions, ProtocolLogLevel.Warning, $"Réception NEIGHBOR_ACK(accepted=false) de {sender} -> fermeture et essai d'un autre candidat de PassiveView.");
            PeerCacheEngine.RecordPeerFailure(state, sender, DateTimeOffset.UtcNow, actions);
            actions.Add(new OutAction.CloseConnection(sender));
            TryPromoteFromPassiveView(state, actions, rng);
        }

        return actions;
    }

    /// <summary>
    /// Traitement de <c>DISCONNECT(sender)</c> :
    /// Retire <c>sender</c> de la Vue Active, l'ajoute à la Vue Passive, notifie Plumtree (<c>NeighborDown</c>)
    /// et tente de promouvoir un remplaçant si besoin.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveDisconnect(
        HyParViewState state,
        NodeId sender,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();

        if (state.ActiveView.Remove(sender))
        {
            state.BusyPeers.Remove(sender);
            EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception DISCONNECT de {sender} -> déplacé vers PassiveView.");
            actions.Add(new OutAction.NeighborDown(sender));
            actions.Add(new OutAction.CloseConnection(sender));
            AddNodeToPassiveView(state, sender, actions, rng);

            if (state.ActiveView.Count == 0)
            {
                TryPromoteFromPassiveView(state, actions, rng);
            }
        }
        else
        {
            EmitLog(state, actions, ProtocolLogLevel.Trace, $"Réception DISCONNECT de {sender} ignorée (absent de ActiveView).");
        }

        return actions;
    }

    /// <summary>
    /// Traitement de la perte brutale d'une connexion (<c>on_connection_lost</c> du rapport) :
    /// 1. Retire <c>deadPeer</c> de la Vue Active.
    /// 2. Émet <c>NeighborDown(deadPeer)</c> pour que Plumtree mette à jour <c>eager_peers</c> et <c>lazy_peers</c>.
    /// 3. Pioche un candidat aléatoire dans la Vue Passive et envoie <c>NEIGHBOR(high_priority)</c>.
    /// </summary>
    public static IReadOnlyList<OutAction> OnConnectionLost(
        HyParViewState state,
        NodeId deadPeer,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();
        bool wasPending = state.PendingNeighbors.Remove(deadPeer);

        if (state.ActiveView.Remove(deadPeer))
        {
            state.BusyPeers.Remove(deadPeer);
            EmitLog(state, actions, ProtocolLogLevel.Warning, $"Perte de connexion détectée avec le voisin actif {deadPeer} -> remplacement depuis PassiveView.");
            PeerCacheEngine.RecordPeerFailure(state, deadPeer, DateTimeOffset.UtcNow, actions);
            actions.Add(new OutAction.NeighborDown(deadPeer));
            actions.Add(new OutAction.CloseConnection(deadPeer));
            TryPromoteFromPassiveView(state, actions, rng);
        }
        else if (wasPending)
        {
            EmitLog(state, actions, ProtocolLogLevel.Warning, $"Échec de connexion vers le candidat {deadPeer} (Pending) -> essai d'un autre candidat depuis PassiveView.");
            PeerCacheEngine.RecordPeerFailure(state, deadPeer, DateTimeOffset.UtcNow, actions);
            actions.Add(new OutAction.CloseConnection(deadPeer));
            TryPromoteFromPassiveView(state, actions, rng);
        }

        return actions;
    }

    /// <summary>
    /// Brassage périodique (<c>shuffle</c> du rapport) :
    /// Construit un échantillon contenant <c>SelfId</c>, <c>k_active_sample</c> pairs de la Vue Active
    /// et <c>k_passive_sample</c> pairs de la Vue Passive, puis l'envoie à un voisin actif aléatoire.
    /// </summary>
    public static IReadOnlyList<OutAction> Shuffle(
        HyParViewState state,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();
        if (state.ActiveView.Count == 0)
        {
            EmitLog(state, actions, ProtocolLogLevel.Trace, "Shuffle ignoré : ActiveView est vide.");
            return actions;
        }

        NodeId target = PickRandom(state.ActiveView, rng);
        var sample = new List<NodeId> { state.SelfId };
        sample.AddRange(PickRandomSubset(state.ActiveView, state.Config.ShuffleActiveSampleSize, rng));
        sample.AddRange(PickRandomSubset(state.PassiveView, state.Config.ShufflePassiveSampleSize, rng));

        var distinctSample = sample.Distinct().ToList();
        state.LastSentShuffleSample.Clear();
        foreach (NodeId id in distinctSample)
        {
            if (id != state.SelfId)
            {
                state.LastSentShuffleSample.Add(id);
            }
        }

        EmitLog(state, actions, ProtocolLogLevel.Debug, $"Initiation SHUFFLE vers {target} avec {distinctSample.Count} pairs (ttl={state.Config.ShuffleWalkLength}).");
        actions.Add(new OutAction.Send(
            target,
            new NetworkMessage.HyParView(
                new HyParViewMessage.Shuffle(
                    Origin: state.SelfId,
                    Sample: distinctSample,
                    Ttl: state.Config.ShuffleWalkLength,
                    Sender: state.SelfId))));

        return actions;
    }

    /// <summary>
    /// Traitement d'un message <c>SHUFFLE(origin, sample, ttl, sender)</c> :
    /// - Si <c>ttl &gt; 0</c> et qu'il existe un autre voisin actif : décrémente <c>ttl</c> et relaie à un voisin aléatoire.
    /// - Sinon : sélectionne un sous-ensemble de sa propre Vue Passive en réponse (<c>ShuffleReply</c> vers <c>origin</c>)
    ///   et fusionne <c>sample</c> dans sa propre Vue Passive.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveShuffle(
        HyParViewState state,
        NodeId origin,
        IReadOnlyList<NodeId> sample,
        byte ttl,
        NodeId sender,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();

        if (ttl > 0 && state.ActiveView.Count > 1)
        {
            var candidates = state.ActiveView.Where(p => p != sender).ToList();
            if (candidates.Count > 0)
            {
                NodeId nextHop = candidates[rng.Next(candidates.Count)];
                EmitLog(state, actions, ProtocolLogLevel.Trace, $"Relais SHUFFLE(origin={origin}, ttl={ttl - 1}) vers {nextHop}.");
                actions.Add(new OutAction.Send(
                    nextHop,
                    new NetworkMessage.HyParView(
                        new HyParViewMessage.Shuffle(origin, sample, (byte)(ttl - 1), state.SelfId))));
                return actions;
            }
        }

        // Fin de la marche aléatoire : répondre à origin avec un échantillon de notre Vue Passive
        var replySample = PickRandomSubset(state.PassiveView, sample.Count, rng);
        MergeSampleIntoPassiveView(state, sample, replySample, actions, rng);
        EmitLog(state, actions, ProtocolLogLevel.Debug, $"Traitement final SHUFFLE(origin={origin}) : fusion de {sample.Count} pairs et envoi de ShuffleReply ({replySample.Count} pairs).");

        if (origin != state.SelfId)
        {
            actions.Add(new OutAction.Send(
                origin,
                new NetworkMessage.HyParView(
                    new HyParViewMessage.ShuffleReply(state.SelfId, replySample))));
        }

        return actions;
    }

    /// <summary>
    /// Traitement de <c>SHUFFLE_REPLY(sender, sample)</c> :
    /// Intègre les nouveaux pairs reçus dans la Vue Passive en remplaçant en priorité ceux qui avaient été envoyés.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveShuffleReply(
        HyParViewState state,
        NodeId sender,
        IReadOnlyList<NodeId> sample,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();
        MergeSampleIntoPassiveView(state, sample, state.LastSentShuffleSample, actions, rng);
        state.LastSentShuffleSample.Clear();
        EmitLog(state, actions, ProtocolLogLevel.Debug, $"Réception SHUFFLE_REPLY de {sender} ({sample.Count} pairs fusionnés dans PassiveView).");
        return actions;
    }

    // =========================================================================
    // Fonctions utilitaires internes (gestion des capacités Active & Passive)
    // =========================================================================

    private static void AddNodeToActiveView(
        HyParViewState state,
        NodeId node,
        List<OutAction> actions,
        Random rng)
    {
        if (node == state.SelfId || state.ActiveView.Contains(node))
        {
            return;
        }

        if (state.ActiveView.Count >= state.Config.MaxActiveViewSize)
        {
            DropRandomNodeFromActiveView(state, actions, rng);
        }

        state.PassiveView.Remove(node);
        state.PendingNeighbors.Remove(node);
        state.ActiveView.Add(node);
        state.SeenNodesInCurrentEpoch.Add(node);

        NodeRole role = state.KnownRoles.GetValueOrDefault(node, NodeRole.Peer);
        PeerCacheEngine.RecordPeerSuccess(state, node, DateTimeOffset.UtcNow, actions);
        EmitLog(state, actions, ProtocolLogLevel.Info, $"Ajout de {node} (Role={role}) à ActiveView.");
        actions.Add(new OutAction.NeighborUp(node, role));
    }

    private static void DropRandomNodeFromActiveView(
        HyParViewState state,
        List<OutAction> actions,
        Random rng)
    {
        if (state.ActiveView.Count == 0)
        {
            return;
        }

        // Préférer évincer un pair qui n'a aucune transmission de données en cours (non présent dans BusyPeers)
        var idlePeers = state.ActiveView
            .Where(p => !state.BusyPeers.Contains(p))
            .ToList();

        NodeId evicted;
        if (idlePeers.Count > 0)
        {
            evicted = idlePeers[rng.Next(idlePeers.Count)];
        }
        else
        {
            evicted = PickRandom(state.ActiveView, rng);
            EmitLog(state, actions, ProtocolLogLevel.Warning, $"ActiveView pleine et tous les pairs sont occupés (BusyPeers) -> éviction forcée de {evicted}.");
        }

        state.ActiveView.Remove(evicted);
        state.BusyPeers.Remove(evicted);

        EmitLog(state, actions, ProtocolLogLevel.Info, $"Éviction de {evicted} depuis ActiveView vers PassiveView (envoi DISCONNECT).");
        actions.Add(new OutAction.Send(
            evicted,
            new NetworkMessage.HyParView(new HyParViewMessage.Disconnect(state.SelfId))));
        actions.Add(new OutAction.NeighborDown(evicted));
        actions.Add(new OutAction.CloseConnection(evicted));

        AddNodeToPassiveView(state, evicted, actions, rng);
    }

    private static void AddNodeToPassiveView(
        HyParViewState state,
        NodeId node,
        List<OutAction> actions,
        Random rng)
    {
        if (node == state.SelfId || state.ActiveView.Contains(node) || state.PassiveView.Contains(node))
        {
            return;
        }

        if (state.PassiveView.Count >= state.Config.MaxPassiveViewSize)
        {
            // Politique LRU : évincer en priorité le pair standard le plus ancien au sens de LastSeenUtc
            var nonMeetingPeers = state.PassiveView
                .Where(p => state.KnownRoles.GetValueOrDefault(p, NodeRole.Peer) != NodeRole.MeetingPoint)
                .ToList();

            NodeId toDrop;
            if (nonMeetingPeers.Count > 0)
            {
                toDrop = nonMeetingPeers
                    .OrderBy(p => state.PeerCacheMetadata.TryGetValue(p, out var meta) ? meta.LastSeenUtc : DateTimeOffset.MinValue)
                    .First();
            }
            else
            {
                toDrop = PickRandom(state.PassiveView, rng);
            }

            state.PassiveView.Remove(toDrop);
            EmitLog(state, actions, ProtocolLogLevel.Trace, $"PassiveView pleine : remplacement LRU de {toDrop} (plus ancien) par {node}.");
        }

        state.PassiveView.Add(node);
        PeerCacheEngine.ObserveDiscoveredPeer(state, node, DateTimeOffset.UtcNow, actions);
    }

    private static void TryPromoteFromPassiveView(
        HyParViewState state,
        List<OutAction> actions,
        Random rng)
    {
        if (state.PassiveView.Count == 0)
        {
            EmitLog(state, actions, ProtocolLogLevel.Warning, "Impossible de promouvoir un remplaçant : PassiveView est vide !");
            if (state.ActiveView.Count == 0 && state.PendingNeighbors.Count == 0)
            {
                actions.AddRange(PeerCacheEngine.InitiateReconnection(state, DateTimeOffset.UtcNow));
            }
            return;
        }

        bool highPriority = state.ActiveView.Count == 0;
        // Sélectionner en priorité les pairs les plus récents/fiables selon l'ordre du Peer Cache (plus anciens en dernier)
        NodeId candidate = state.PassiveView
            .OrderByDescending(p => state.KnownRoles.GetValueOrDefault(p, NodeRole.Peer) == NodeRole.MeetingPoint ? 1 : 0)
            .ThenBy(p => state.PeerCacheMetadata.TryGetValue(p, out var m) ? m.ConsecutiveFailures : 0)
            .ThenByDescending(p => state.PeerCacheMetadata.TryGetValue(p, out var m) ? m.LastSeenUtc : DateTimeOffset.MinValue)
            .ThenByDescending(p => state.PeerCacheMetadata.TryGetValue(p, out var m) ? m.SuccessCount : 0)
            .First();
        state.PassiveView.Remove(candidate);
        state.PendingNeighbors.Add(candidate);

        EmitLog(state, actions, ProtocolLogLevel.Info, $"Promotion du candidat {candidate} depuis PassiveView -> envoi NEIGHBOR(highPriority={highPriority}).");
        actions.Add(new OutAction.Connect(candidate));
        actions.Add(new OutAction.Send(
            candidate,
            new NetworkMessage.HyParView(
                new HyParViewMessage.Neighbor(state.SelfId, highPriority, state.SelfRole))));
    }

    private static void MergeSampleIntoPassiveView(
        HyParViewState state,
        IEnumerable<NodeId> incomingSample,
        IEnumerable<NodeId> priorityEvictionPool,
        List<OutAction> actions,
        Random rng)
    {
        var evictQueue = new Queue<NodeId>(
            priorityEvictionPool.Where(p => state.PassiveView.Contains(p)));

        foreach (NodeId peer in incomingSample)
        {
            state.SeenNodesInCurrentEpoch.Add(peer);
            if (peer == state.SelfId || state.ActiveView.Contains(peer) || state.PassiveView.Contains(peer))
            {
                continue;
            }

            if (state.PassiveView.Count >= state.Config.MaxPassiveViewSize)
            {
                if (evictQueue.TryDequeue(out NodeId prioritized) && state.PassiveView.Remove(prioritized))
                {
                    state.PassiveView.Add(peer);
                    PeerCacheEngine.ObserveDiscoveredPeer(state, peer, DateTimeOffset.UtcNow, actions);
                }
                else
                {
                    AddNodeToPassiveView(state, peer, actions, rng);
                }
            }
            else
            {
                state.PassiveView.Add(peer);
                PeerCacheEngine.ObserveDiscoveredPeer(state, peer, DateTimeOffset.UtcNow, actions);
            }
        }
    }

    private static NodeId PickRandom(ICollection<NodeId> set, Random rng)
    {
        int index = rng.Next(set.Count);
        foreach (NodeId item in set)
        {
            if (index-- == 0)
            {
                return item;
            }
        }
        throw new InvalidOperationException("Cannot pick from an empty collection.");
    }

    private static List<NodeId> PickRandomSubset(ICollection<NodeId> source, int maxCount, Random rng)
    {
        if (source.Count == 0 || maxCount <= 0)
        {
            return new List<NodeId>();
        }

        return source
            .OrderBy(_ => rng.Next())
            .Take(maxCount)
            .ToList();
    }
}
