namespace Whispernet.Core;

/// <summary>
/// Paramètres de configuration du protocole Plumtree.
/// </summary>
public sealed record PlumtreeConfig
{
    /// <summary>Délai de grâce avant d'envoyer un GRAFT après réception d'un IHAVE (ex: 150 ms).</summary>
    public TimeSpan IHaveTimeout { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Capacité maximale de l'historique borné (LRU/FIFO) des messages reçus pour déduplication et GRAFT.</summary>
    public int MaxReceivedMessagesHistory { get; init; } = 1000;

    /// <summary>
    /// Si vrai (cas d'un Raspberry Pi Point de Meeting en mode HyParView-only / Lazy),
    /// les nouveaux voisins sont placés en Lazy plutôt qu'en Eager pour économiser la bande passante.
    /// </summary>
    public bool PreferLazyForNewNeighbors { get; init; } = false;
}

/// <summary>
/// État interne en mémoire d'un nœud pour le protocole Plumtree (Sans-I/O).
/// </summary>
public sealed class PlumtreeState
{
    public NodeId SelfId { get; }
    public PlumtreeConfig Config { get; }

    /// <summary>Sous-ensemble de la Vue Active HyParView recevant les messages complets (GOSSIP).</summary>
    public HashSet<NodeId> EagerPeers { get; } = new();

    /// <summary>Complément dans la Vue Active HyParView recevant uniquement les résumés (IHAVE).</summary>
    public HashSet<NodeId> LazyPeers { get; } = new();

    /// <summary>Historique borné des messages reçus (MsgId -> Payload) pour déduplication et réponse aux GRAFT.</summary>
    public Dictionary<MsgId, byte[]> ReceivedMessages { get; } = new();

    /// <summary>Ordre d'arrivée des MsgId pour purger les plus anciens lorsque MaxReceivedMessagesHistory est atteint.</summary>
    public Queue<MsgId> ReceivedOrder { get; } = new();

    /// <summary>Annonces IHAVE reçues pour des messages pas encore vus, en attente d'expiration de IHaveTimer.</summary>
    public Dictionary<MsgId, Queue<(NodeId Peer, uint Round)>> MissingIHaves { get; } = new();

    /// <summary>File d'attente des annonces IHAVE à expédier de manière groupée lors du LazyPushTimer.</summary>
    public List<(NodeId TargetPeer, MsgId MessageId, uint Round)> LazyQueue { get; } = new();

    public PlumtreeState(NodeId selfId, PlumtreeConfig? config = null)
    {
        SelfId = selfId;
        Config = config ?? new PlumtreeConfig();
    }
}
