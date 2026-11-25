namespace Whispernet.Core;

/// <summary>
/// État global unifié d'un nœud Whispernet combinant HyParView et Plumtree.
/// Garantit à tout moment l'invariant de cohérence :
/// <c>Plumtree.EagerPeers U Plumtree.LazyPeers == HyParView.ActiveView</c>
/// et <c>Plumtree.EagerPeers ∩ Plumtree.LazyPeers == ∅</c>.
/// </summary>
public sealed class NodeState
{
    public HyParViewState HyParView { get; }
    public PlumtreeState Plumtree { get; }

    public NodeState(
        NodeId selfId,
        NodeRole selfRole = NodeRole.Peer,
        HyParViewConfig? hyParViewConfig = null,
        PlumtreeConfig? plumtreeConfig = null)
    {
        HyParView = new HyParViewState(selfId, selfRole, hyParViewConfig);
        Plumtree = new PlumtreeState(selfId, plumtreeConfig);
    }

    /// <summary>
    /// Vérifie l'invariant critique de cohérence entre la Vue Active HyParView et les ensembles Eager/Lazy de Plumtree.
    /// </summary>
    public bool CheckConsistencyInvariant()
    {
        if (Plumtree.EagerPeers.Overlaps(Plumtree.LazyPeers))
        {
            return false;
        }

        int unionCount = Plumtree.EagerPeers.Count + Plumtree.LazyPeers.Count;
        if (unionCount != HyParView.ActiveView.Count)
        {
            return false;
        }

        foreach (NodeId peer in HyParView.ActiveView)
        {
            if (!Plumtree.EagerPeers.Contains(peer) && !Plumtree.LazyPeers.Contains(peer))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Orchestrateur Sans-I/O couplant automatiquement <see cref="HyParViewEngine"/> et <see cref="PlumtreeEngine"/>.
/// Intercepte les actions <see cref="OutAction.NeighborUp"/> et <see cref="OutAction.NeighborDown"/> émises par HyParView
/// pour synchroniser immédiatement les ensembles <c>EagerPeers</c> et <c>LazyPeers</c> de Plumtree.
/// </summary>
public static class NodeOrchestrator
{
    public static IReadOnlyList<OutAction> InitiateJoin(NodeState state, NodeId contactNode)
        => ApplyCoupling(state, HyParViewEngine.InitiateJoin(state.HyParView, contactNode));

    public static IReadOnlyList<OutAction> HydrateFromPeerCache(
        NodeState state,
        IEnumerable<CachedPeerEntry> loadedEntries,
        DateTimeOffset nowUtc)
        => PeerCacheEngine.HydrateFromPeerCache(state.HyParView, loadedEntries, nowUtc);

    public static IReadOnlyList<OutAction> InitiateReconnection(
        NodeState state,
        DateTimeOffset nowUtc)
        => ApplyCoupling(state, PeerCacheEngine.InitiateReconnection(state.HyParView, nowUtc));

    public static IReadOnlyList<OutAction> BroadcastMessage(NodeState state, MsgId messageId, byte[] payload)
        => PlumtreeEngine.BroadcastMessage(state.Plumtree, messageId, payload);

    public static IReadOnlyList<OutAction> OnReceiveMessage(
        NodeState state,
        NodeId from,
        NetworkMessage message,
        Random? rng = null)
    {
        return message switch
        {
            NetworkMessage.HyParView hpv => ApplyCoupling(state, HyParViewEngine.HandleMessage(state.HyParView, hpv.Message, rng)),
            NetworkMessage.Plumtree pt => PlumtreeEngine.HandleMessage(state.Plumtree, from, pt.Message),
            _ => Array.Empty<OutAction>()
        };
    }

    public static IReadOnlyList<OutAction> OnConnectionLost(
        NodeState state,
        NodeId deadPeer,
        Random? rng = null)
        => ApplyCoupling(state, HyParViewEngine.OnConnectionLost(state.HyParView, deadPeer, rng));

    public static IReadOnlyList<OutAction> OnTimerExpired(
        NodeState state,
        TimerKind timer,
        Random? rng = null)
    {
        return timer switch
        {
            TimerKind.ShuffleTimer => ApplyCoupling(state, HyParViewEngine.Shuffle(state.HyParView, rng)),
            TimerKind.LazyPushTimer => PlumtreeEngine.FlushLazyQueue(state.Plumtree),
            TimerKind.IHaveTimer ihave => PlumtreeEngine.OnIHaveTimeout(state.Plumtree, ihave.MessageId),
            TimerKind.ReconnectTimer => ApplyCoupling(state, PeerCacheEngine.InitiateReconnection(state.HyParView, DateTimeOffset.UtcNow)),
            TimerKind.PartitionHealTimer => ApplyCoupling(state, PeerCacheEngine.ProbeForPartitionHeal(state.HyParView, DateTimeOffset.UtcNow, rng)),
            _ => Array.Empty<OutAction>()
        };
    }

    private static IReadOnlyList<OutAction> ApplyCoupling(
        NodeState state,
        IReadOnlyList<OutAction> hyParViewActions)
    {
        var allActions = new List<OutAction>(hyParViewActions.Count + 2);
        foreach (OutAction action in hyParViewActions)
        {
            allActions.Add(action);
            switch (action)
            {
                case OutAction.NeighborUp up:
                    allActions.AddRange(PlumtreeEngine.OnNeighborUp(state.Plumtree, up.Peer, up.Role));
                    break;
                case OutAction.NeighborDown down:
                    allActions.AddRange(PlumtreeEngine.OnNeighborDown(state.Plumtree, down.Peer));
                    break;
            }
        }
        return allActions;
    }
}
