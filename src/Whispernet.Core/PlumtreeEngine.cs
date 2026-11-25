namespace Whispernet.Core;

/// <summary>
/// Implémentation Sans-I/O complète du protocole Plumtree (diffusion épidémique en arbre
/// Eager/Lazy, élagage par Prune, auto-réparation par IHave/Graft et couplage avec HyParView).
/// Toutes les fonctions sont pures vis-à-vis du réseau et retournent la liste des <see cref="OutAction"/>.
/// </summary>
public static class PlumtreeEngine
{
    private const string LogCat = "Plumtree";

    private static void EmitLog(
        PlumtreeState state,
        List<OutAction> actions,
        ProtocolLogLevel level,
        string message)
    {
        string summary = $"[Eager={state.EagerPeers.Count}, Lazy={state.LazyPeers.Count}, History={state.ReceivedMessages.Count}, Missing={state.MissingIHaves.Count}, LazyQueue={state.LazyQueue.Count}]";
        actions.Add(new OutAction.Log(level, LogCat, $"[Self={state.SelfId}] {message} {summary}"));
    }

    /// <summary>
    /// Point d'entrée unique pour le traitement d'un message Plumtree reçu depuis un pair.
    /// </summary>
    public static IReadOnlyList<OutAction> HandleMessage(
        PlumtreeState state,
        NodeId from,
        PlumtreeMessage message)
    {
        return message switch
        {
            PlumtreeMessage.Gossip gossip => OnReceiveGossip(state, from, gossip.MessageId, gossip.Round, gossip.Payload),
            PlumtreeMessage.Prune => OnReceivePrune(state, from),
            PlumtreeMessage.IHave ihave => OnReceiveIHave(state, from, ihave.Entries),
            PlumtreeMessage.Graft graft => OnReceiveGraft(state, from, graft.MessageId, graft.Round),
            _ => Array.Empty<OutAction>()
        };
    }

    /// <summary>
    /// Publie un nouveau message applicatif à l'initiative de ce nœud (round = 0).
    /// </summary>
    public static IReadOnlyList<OutAction> BroadcastMessage(
        PlumtreeState state,
        MsgId messageId,
        byte[] payload)
    {
        var actions = new List<OutAction>();

        if (state.ReceivedMessages.ContainsKey(messageId))
        {
            EmitLog(state, actions, ProtocolLogLevel.Warning, $"BroadcastMessage ignoré : {messageId} a déjà été publié.");
            return actions;
        }

        StoreMessage(state, messageId, payload);
        EmitLog(state, actions, ProtocolLogLevel.Info, $"Publication locale de {messageId} ({payload.Length} octets) -> diffusion vers {state.EagerPeers.Count} pairs Eager et {state.LazyPeers.Count} pairs Lazy.");

        ForwardToEagerAndQueueLazy(state, excludeSender: null, messageId, nextRound: 0, payload, actions);
        return actions;
    }

    /// <summary>
    /// Traitement de <c>GOSSIP(msgId, round, payload)</c> (<c>on_receive_gossip</c> du rapport) :
    /// - Si message inédit :
    ///   1. L'enregistre dans <c>ReceivedMessages</c> et émet <c>DeliverToApp</c>.
    ///   2. Annule un éventuel <c>IHaveTimer(msgId)</c> en cours.
    ///   3. Promeut <c>from</c> en <c>EagerPeers</c> s'il était dans <c>LazyPeers</c>.
    ///   4. Relaie <c>GOSSIP(msgId, round + 1)</c> aux autres <c>EagerPeers</c> et met en file <c>IHAVE</c> pour les <c>LazyPeers</c>.
    /// - Si doublon :
    ///   1. Déplace <c>from</c> de <c>EagerPeers</c> vers <c>LazyPeers</c>.
    ///   2. Envoie <c>PRUNE</c> à <c>from</c>.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveGossip(
        PlumtreeState state,
        NodeId from,
        MsgId messageId,
        uint round,
        byte[] payload)
    {
        var actions = new List<OutAction>();

        if (!state.ReceivedMessages.ContainsKey(messageId))
        {
            StoreMessage(state, messageId, payload);
            EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception GOSSIP inédit {messageId} (round={round}, {payload.Length} octets) depuis {from} -> livraison à l'application.");
            actions.Add(new OutAction.DeliverToApp(messageId, payload));

            if (state.MissingIHaves.Remove(messageId))
            {
                EmitLog(state, actions, ProtocolLogLevel.Debug, $"Annulation de IHaveTimer({messageId}) car le message vient d'arriver via {from}.");
                actions.Add(new OutAction.CancelTimer(new TimerKind.IHaveTimer(messageId)));
            }

            if (state.LazyPeers.Remove(from))
            {
                state.EagerPeers.Add(from);
                EmitLog(state, actions, ProtocolLogLevel.Info, $"Promotion de {from} de Lazy -> Eager suite à la réception de {messageId}.");
            }

            ForwardToEagerAndQueueLazy(state, excludeSender: from, messageId, round + 1, payload, actions);
        }
        else
        {
            // Doublon : élagage de la branche redondante
            bool wasEager = state.EagerPeers.Remove(from);
            state.LazyPeers.Add(from);

            EmitLog(state, actions, ProtocolLogLevel.Debug, $"Réception doublon GOSSIP {messageId} (round={round}) depuis {from} (wasEager={wasEager}) -> passage en Lazy et envoi PRUNE.");
            actions.Add(new OutAction.Send(
                from,
                new NetworkMessage.Plumtree(new PlumtreeMessage.Prune())));
        }

        return actions;
    }

    /// <summary>
    /// Traitement de <c>PRUNE</c> : déplace l'émetteur de <c>EagerPeers</c> vers <c>LazyPeers</c>.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceivePrune(
        PlumtreeState state,
        NodeId from)
    {
        var actions = new List<OutAction>();
        bool moved = state.EagerPeers.Remove(from);
        state.LazyPeers.Add(from);

        EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception PRUNE de {from} (movedFromEager={moved}) -> déplacé dans LazyPeers.");
        return actions;
    }

    /// <summary>
    /// Traitement de <c>IHAVE(entries)</c> (<c>on_receive_ihave</c> du rapport) :
    /// Pour chaque <c>(msgId, round)</c> non présent dans <c>ReceivedMessages</c>,
    /// enregistre l'annonce dans <c>MissingIHaves[msgId]</c> et démarre <c>IHaveTimer(msgId)</c> s'il s'agit de la première annonce.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveIHave(
        PlumtreeState state,
        NodeId from,
        IReadOnlyList<(MsgId MessageId, uint Round)> entries)
    {
        var actions = new List<OutAction>();

        foreach (var (messageId, round) in entries)
        {
            if (state.ReceivedMessages.ContainsKey(messageId))
            {
                EmitLog(state, actions, ProtocolLogLevel.Trace, $"IHAVE({messageId}, round={round}) de {from} ignoré : message déjà reçu.");
                continue;
            }

            bool isFirstAnnouncement = !state.MissingIHaves.TryGetValue(messageId, out var queue);
            if (queue is null)
            {
                queue = new Queue<(NodeId Peer, uint Round)>();
                state.MissingIHaves[messageId] = queue;
            }

            queue.Enqueue((from, round));

            if (isFirstAnnouncement)
            {
                EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception IHAVE pour message manquant {messageId} (round={round}) depuis {from} -> démarrage IHaveTimer ({state.Config.IHaveTimeout.TotalMilliseconds} ms).");
                actions.Add(new OutAction.StartTimer(
                    new TimerKind.IHaveTimer(messageId),
                    state.Config.IHaveTimeout));
            }
            else
            {
                EmitLog(state, actions, ProtocolLogLevel.Debug, $"Réception IHAVE supplémentaire pour {messageId} (round={round}) depuis {from} (en file d'attente de secours).");
            }
        }

        return actions;
    }

    /// <summary>
    /// Traitement de l'expiration de <c>IHaveTimer(messageId)</c> (<c>on_ihave_timeout</c> du rapport) :
    /// Si le message n'est toujours pas reçu, dépile le premier pair ayant annoncé <c>messageId</c>,
    /// le promeut en <c>EagerPeers</c>, lui envoie <c>GRAFT(messageId, round)</c> et relance le timer
    /// au cas où ce pair ne répondrait pas.
    /// </summary>
    public static IReadOnlyList<OutAction> OnIHaveTimeout(
        PlumtreeState state,
        MsgId messageId)
    {
        var actions = new List<OutAction>();

        if (state.ReceivedMessages.ContainsKey(messageId))
        {
            state.MissingIHaves.Remove(messageId);
            return actions;
        }

        if (!state.MissingIHaves.TryGetValue(messageId, out var queue) || queue.Count == 0)
        {
            state.MissingIHaves.Remove(messageId);
            EmitLog(state, actions, ProtocolLogLevel.Warning, $"Expiration IHaveTimer({messageId}), mais plus aucun pair candidat dans MissingIHaves.");
            return actions;
        }

        var (candidatePeer, round) = queue.Dequeue();
        if (queue.Count == 0)
        {
            state.MissingIHaves.Remove(messageId);
        }

        state.LazyPeers.Remove(candidatePeer);
        state.EagerPeers.Add(candidatePeer);

        EmitLog(state, actions, ProtocolLogLevel.Warning, $"Timeout IHaveTimer({messageId}) -> greffe (GRAFT) vers {candidatePeer} (round={round}) et promotion en Eager.");
        actions.Add(new OutAction.Send(
            candidatePeer,
            new NetworkMessage.Plumtree(new PlumtreeMessage.Graft(messageId, round))));

        // Si d'autres pairs ont annoncé ce message, relancer le timer pour essayer le suivant si candidatePeer ne répond pas
        if (state.MissingIHaves.ContainsKey(messageId))
        {
            actions.Add(new OutAction.StartTimer(
                new TimerKind.IHaveTimer(messageId),
                state.Config.IHaveTimeout));
        }

        return actions;
    }

    /// <summary>
    /// Traitement de <c>GRAFT(messageId, round)</c> :
    /// 1. Promeut <c>from</c> de <c>LazyPeers</c> vers <c>EagerPeers</c>.
    /// 2. Si <c>messageId</c> est dans <c>ReceivedMessages</c>, renvoie immédiatement <c>GOSSIP(messageId, round, payload)</c> à <c>from</c>.
    /// </summary>
    public static IReadOnlyList<OutAction> OnReceiveGraft(
        PlumtreeState state,
        NodeId from,
        MsgId messageId,
        uint round)
    {
        var actions = new List<OutAction>();

        state.LazyPeers.Remove(from);
        state.EagerPeers.Add(from);

        if (state.ReceivedMessages.TryGetValue(messageId, out byte[]? payload))
        {
            EmitLog(state, actions, ProtocolLogLevel.Info, $"Réception GRAFT({messageId}) de {from} -> promotion en Eager et renvoi immédiat de GOSSIP({messageId}).");
            actions.Add(new OutAction.Send(
                from,
                new NetworkMessage.Plumtree(
                    new PlumtreeMessage.Gossip(messageId, round, payload))));
        }
        else
        {
            EmitLog(state, actions, ProtocolLogLevel.Warning, $"Réception GRAFT({messageId}) de {from} -> promotion en Eager, mais {messageId} est introuvable dans le cache local.");
        }

        return actions;
    }

    /// <summary>
    /// Traitement de <c>LazyPushTimer</c> :
    /// Regroupe toutes les annonces <c>IHAVE</c> en attente dans <c>LazyQueue</c> par destinataire
    /// et émet un seul message <c>IHAVE</c> groupé par pair Lazy.
    /// </summary>
    public static IReadOnlyList<OutAction> FlushLazyQueue(PlumtreeState state)
    {
        var actions = new List<OutAction>();
        if (state.LazyQueue.Count == 0)
        {
            return actions;
        }

        var groupedByPeer = state.LazyQueue
            .Where(item => state.LazyPeers.Contains(item.TargetPeer) || state.EagerPeers.Contains(item.TargetPeer))
            .GroupBy(item => item.TargetPeer)
            .ToList();

        state.LazyQueue.Clear();

        foreach (var group in groupedByPeer)
        {
            NodeId targetPeer = group.Key;
            var entries = group
                .Select(g => (g.MessageId, g.Round))
                .Distinct()
                .ToList();

            EmitLog(state, actions, ProtocolLogLevel.Trace, $"Envoi groupé IHAVE ({entries.Count} entrées) vers {targetPeer}.");
            actions.Add(new OutAction.Send(
                targetPeer,
                new NetworkMessage.Plumtree(new PlumtreeMessage.IHave(entries))));
        }

        return actions;
    }

    // =========================================================================
    // Couplage direct avec HyParView (maintien de l'invariant ActiveView == Eager U Lazy)
    // =========================================================================

    /// <summary>
    /// Appelé lorsqu'un pair entre dans la Vue Active de HyParView (<c>NeighborUp</c>).
    /// Par défaut, il est ajouté à <c>EagerPeers</c> (sauf si <c>PreferLazyForNewNeighbors</c> ou si le pair est un <c>MeetingPoint</c> en mode Lazy).
    /// </summary>
    public static IReadOnlyList<OutAction> OnNeighborUp(
        PlumtreeState state,
        NodeId peer,
        NodeRole role = NodeRole.Peer)
    {
        var actions = new List<OutAction>();
        if (peer == state.SelfId || state.EagerPeers.Contains(peer) || state.LazyPeers.Contains(peer))
        {
            return actions;
        }

        if (state.Config.PreferLazyForNewNeighbors)
        {
            state.LazyPeers.Add(peer);
            EmitLog(state, actions, ProtocolLogLevel.Info, $"NeighborUp({peer}, Role={role}) -> ajouté à LazyPeers (PreferLazyForNewNeighbors=true).");
        }
        else
        {
            state.EagerPeers.Add(peer);
            EmitLog(state, actions, ProtocolLogLevel.Info, $"NeighborUp({peer}, Role={role}) -> ajouté à EagerPeers.");
        }

        return actions;
    }

    /// <summary>
    /// Appelé lorsqu'un pair quitte la Vue Active de HyParView (<c>NeighborDown</c>).
    /// Retire immédiatement le pair de <c>EagerPeers</c>, <c>LazyPeers</c> et nettoie <c>LazyQueue</c>.
    /// </summary>
    public static IReadOnlyList<OutAction> OnNeighborDown(
        PlumtreeState state,
        NodeId peer)
    {
        var actions = new List<OutAction>();
        bool wasEager = state.EagerPeers.Remove(peer);
        bool wasLazy = state.LazyPeers.Remove(peer);
        state.LazyQueue.RemoveAll(item => item.TargetPeer == peer);

        if (wasEager || wasLazy)
        {
            EmitLog(state, actions, ProtocolLogLevel.Info, $"NeighborDown({peer}) -> retiré de Plumtree (wasEager={wasEager}, wasLazy={wasLazy}).");
        }

        return actions;
    }

    // =========================================================================
    // Fonctions utilitaires internes
    // =========================================================================

    private static void ForwardToEagerAndQueueLazy(
        PlumtreeState state,
        NodeId? excludeSender,
        MsgId messageId,
        uint nextRound,
        byte[] payload,
        List<OutAction> actions)
    {
        foreach (NodeId peer in state.EagerPeers)
        {
            if (excludeSender is null || peer != excludeSender.Value)
            {
                actions.Add(new OutAction.Send(
                    peer,
                    new NetworkMessage.Plumtree(
                        new PlumtreeMessage.Gossip(messageId, nextRound, payload))));
            }
        }

        foreach (NodeId peer in state.LazyPeers)
        {
            if (excludeSender is null || peer != excludeSender.Value)
            {
                state.LazyQueue.Add((peer, messageId, nextRound));
            }
        }
    }

    private static void StoreMessage(PlumtreeState state, MsgId messageId, byte[] payload)
    {
        if (state.ReceivedMessages.ContainsKey(messageId))
        {
            return;
        }

        while (state.ReceivedMessages.Count >= state.Config.MaxReceivedMessagesHistory && state.ReceivedOrder.Count > 0)
        {
            MsgId oldest = state.ReceivedOrder.Dequeue();
            state.ReceivedMessages.Remove(oldest);
        }

        state.ReceivedMessages[messageId] = payload;
        state.ReceivedOrder.Enqueue(messageId);
    }
}
