namespace Whispernet.Core;

/// <summary>
/// Paramètres de configuration du protocole HyParView.
/// </summary>
public sealed record HyParViewConfig
{
    /// <summary>Capacité maximale de la Vue Active (k_active, ex: 5 pour un pair standard).</summary>
    public int MaxActiveViewSize { get; init; } = 5;

    /// <summary>Capacité maximale de la Vue Passive (k_passive, ex: 30 pour un pair standard, 200+ pour un MeetingPoint).</summary>
    public int MaxPassiveViewSize { get; init; } = 30;

    /// <summary>Active Random Walk Length (ARWL) : TTL initial de ForwardJoin (ex: 4 à 6 sauts).</summary>
    public byte ActiveRandomWalkLength { get; init; } = 4;

    /// <summary>Passive Random Walk Length (PRWL) : seuil de TTL où le nouveau nœud est ajouté à la Vue Passive (ex: 2 sauts).</summary>
    public byte PassiveRandomWalkLength { get; init; } = 2;

    /// <summary>TTL d'une marche aléatoire de Shuffle (ex: 3 sauts).</summary>
    public byte ShuffleWalkLength { get; init; } = 3;

    /// <summary>Nombre de pairs tirés de la Vue Active lors d'un Shuffle (k_active_sample).</summary>
    public int ShuffleActiveSampleSize { get; init; } = 2;

    /// <summary>Nombre de pairs tirés de la Vue Passive lors d'un Shuffle (k_passive_sample).</summary>
    public int ShufflePassiveSampleSize { get; init; } = 4;

    /// <summary>Seuil de tolérance d'échecs consécutifs avant d'évincer définitivement un pair standard du cache (défaut : 5).</summary>
    public int MaxConsecutiveFailuresBeforeEviction { get; init; } = 5;

    /// <summary>Durée maximale d'inactivité (TTL) avant qu'un pair standard du cache soit considéré comme périmé (défaut : 7 jours).</summary>
    public TimeSpan PeerCacheMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Délai minimal de refroidissement (backoff exponentiel) avant de réessayer un pair qui vient d'échouer (défaut : 10 s).</summary>
    public TimeSpan RetryCooldownBase { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Intervalle du ReconnectTimer lorsque la Vue Active est vide (défaut : 5 s).</summary>
    public TimeSpan ReconnectRetryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Nombre maximal de candidats contactés en parallèle lors d'une tentative de reconnexion (défaut : 3).</summary>
    public int MaxReconnectCandidatesPerAttempt { get; init; } = 3;

    /// <summary>
    /// Si vrai et que tous les pairs standards du cache sont périmés (> PeerCacheMaxAge) après une longue absence,
    /// accorde une dernière tentative (sursis d'1 essai) aux anciens pairs les plus fiables avant de les purger.
    /// </summary>
    public bool AllowStaleFallbackWhenCacheEmpty { get; init; } = true;

    /// <summary>Intervalle périodique du PartitionHealTimer (anti-îlots via pont Mainline DHT, défaut : 3 min).</summary>
    public TimeSpan PartitionHealInterval { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Seuil de Vue Active en dessous duquel un nœud force une sonde anti-îlots même sans détection de boucle.</summary>
    public int MinHealthyActiveViewForNoHeal { get; init; } = 3;

    /// <summary>Configuration préconisée pour un Raspberry Pi Point de Meeting 24/24.</summary>
    public static HyParViewConfig MeetingPointDefaults => new()
    {
        MaxActiveViewSize = 12,
        MaxPassiveViewSize = 250,
        ActiveRandomWalkLength = 4,
        PassiveRandomWalkLength = 2,
        ShuffleWalkLength = 3,
        ShuffleActiveSampleSize = 3,
        ShufflePassiveSampleSize = 8
    };
}

/// <summary>
/// État interne en mémoire d'un nœud pour le protocole HyParView (Sans-I/O).
/// </summary>
public sealed class HyParViewState
{
    public NodeId SelfId { get; }
    public NodeRole SelfRole { get; }
    public HyParViewConfig Config { get; }

    /// <summary>Vue Active : voisins connectés en permanence (capacité max Config.MaxActiveViewSize).</summary>
    public HashSet<NodeId> ActiveView { get; } = new();

    /// <summary>Vue Passive : répertoire de secours (capacité max Config.MaxPassiveViewSize).</summary>
    public HashSet<NodeId> PassiveView { get; } = new();

    /// <summary>Demandes Neighbor envoyées en attente de NeighborAck.</summary>
    public HashSet<NodeId> PendingNeighbors { get; } = new();

    /// <summary>Rôles connus des pairs (permet d'identifier les Points de Meeting 24/24).</summary>
    public Dictionary<NodeId, NodeRole> KnownRoles { get; } = new();

    /// <summary>Dernier échantillon envoyé lors d'un Shuffle pour prioriser les remplacements au retour de ShuffleReply.</summary>
    public HashSet<NodeId> LastSentShuffleSample { get; } = new();

    /// <summary>
    /// Pairs actuellement en train de transmettre ou recevoir un flux de données actif.
    /// HyParView évite de les évincer en priorité lors d'un remplacement dans la Vue Active.
    /// </summary>
    public HashSet<NodeId> BusyPeers { get; } = new();

    /// <summary>
    /// Métadonnées de cache par pair (rôle, ticket Iroh, fraîcheur LastSeenUtc, fidélité SuccessCount, échecs consécutifs).
    /// </summary>
    public Dictionary<NodeId, CachedPeerEntry> PeerCacheMetadata { get; } = new();

    /// <summary>
    /// Ensemble des Points de Meeting 24/24 connus (ancres permanentes immunisées contre l'éviction).
    /// </summary>
    public HashSet<NodeId> BootstrapMeetingPoints { get; } = new();

    /// <summary>
    /// Pairs vus dans l'époque courante (ActiveView + échanges Shuffle) afin de détecter
    /// les îlots isolés et choisir des Pairs Ponts externes lors de <c>PartitionHealTimer</c>.
    /// </summary>
    public HashSet<NodeId> SeenNodesInCurrentEpoch { get; } = new();

    public HyParViewState(NodeId selfId, NodeRole selfRole = NodeRole.Peer, HyParViewConfig? config = null)
    {
        SelfId = selfId;
        SelfRole = selfRole;
        Config = config ?? (selfRole == NodeRole.MeetingPoint
            ? HyParViewConfig.MeetingPointDefaults
            : new HyParViewConfig());
    }
}
