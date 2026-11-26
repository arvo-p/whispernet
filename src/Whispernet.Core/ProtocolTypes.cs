using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whispernet.Core;

/// <summary>
/// Identifiant unique d'un nœud (ex: EndpointId / clé publique Base32 Iroh).
/// </summary>
public readonly record struct NodeId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Identifiant unique d'un message applicatif diffusé via Plumtree.
/// </summary>
public readonly record struct MsgId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Identifiant cryptographique d'un auteur (SHA-256 hexadécimal de sa clé publique SPKI).
/// Indépendant du <see cref="NodeId"/> réseau.
/// </summary>
[JsonConverter(typeof(AuthorIdJsonConverter))]
public readonly record struct AuthorId(string Value)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Value) &&
        Value.Length == 64 &&
        Value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public override string ToString() => Value;
}

public sealed class AuthorIdJsonConverter : JsonConverter<AuthorId>
{
    public override AuthorId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, AuthorId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    public override AuthorId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, AuthorId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value);
}

/// <summary>
/// Identifiant cryptographique d'un groupe (SHA-256 hexadécimal de la clé publique SPKI du créateur).
/// </summary>
public readonly record struct GroupId(string Value)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Value) &&
        Value.Length == 64 &&
        Value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static GroupId FromOwnerPublicKeySpki(ReadOnlySpan<byte> ownerPublicKeySpki)
        => new(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ownerPublicKeySpki)));

    public override string ToString() => Value;
}

/// <summary>
/// Rôle du nœud dans le réseau P2P (pair standard ou Point de Meeting 24/24 sur Raspberry Pi).
/// </summary>
public enum NodeRole
{
    Peer = 0,
    MeetingPoint = 1
}

/// <summary>
/// Types de timers utilisés par l'orchestration Sans-I/O (HyParView + Plumtree).
/// </summary>
public abstract record TimerKind
{
    private TimerKind() { }

    /// <summary>Timer périodique HyParView déclenchant un Shuffle.</summary>
    public sealed record ShuffleTimer : TimerKind;

    /// <summary>Timer périodique Plumtree regroupant et envoyant les IHAVE en file Lazy.</summary>
    public sealed record LazyPushTimer : TimerKind;

    /// <summary>Timer ponctuel Plumtree avant d'envoyer un GRAFT pour un message manquant.</summary>
    public sealed record IHaveTimer(MsgId MessageId) : TimerKind;

    /// <summary>Timer de reconnexion périodique lorsque la Vue Active HyParView est vide.</summary>
    public sealed record ReconnectTimer : TimerKind;

    /// <summary>Timer périodique anti-îlots déclenchant un pont DHT pour fusionner d'éventuelles partitions réseau.</summary>
    public sealed record PartitionHealTimer : TimerKind;
}

/// <summary>
/// Entrée persistée dans le cache de pairs (Peer Cache) permettant à un nœud de se reconnecter
/// au réseau après un redémarrage ou une coupure réseau.
/// </summary>
public sealed record CachedPeerEntry(
    NodeId NodeId,
    NodeRole Role,
    string? EndpointTicket,
    DateTimeOffset LastSeenUtc,
    int SuccessCount = 0,
    int ConsecutiveFailures = 0,
    int TotalFailureCount = 0,
    DateTimeOffset? LastAttemptUtc = null)
{
    /// <summary>
    /// Un pair standard est considéré comme périmé (stale) si sa dernière activité dépasse <paramref name="maxAge"/>.
    /// Les Points de Meeting 24/24 (<see cref="NodeRole.MeetingPoint"/>) ne sont jamais périmés.
    /// </summary>
    public bool IsStale(DateTimeOffset nowUtc, TimeSpan maxAge)
        => Role != NodeRole.MeetingPoint && (nowUtc - LastSeenUtc) > maxAge;

    /// <summary>
    /// Détermine si ce pair doit être évincé du cache (dépassement du seuil d'échecs consécutifs ou TTL expiré).
    /// Les Points de Meeting 24/24 sont immunisés contre l'éviction automatique.
    /// </summary>
    public bool ShouldEvict(DateTimeOffset nowUtc, int maxConsecutiveFailures, TimeSpan maxAge)
        => Role != NodeRole.MeetingPoint
           && (ConsecutiveFailures >= maxConsecutiveFailures || IsStale(nowUtc, maxAge));

    /// <summary>
    /// Indique si le pair est actuellement en période de refroidissement (cooldown backoff) après un échec récent.
    /// </summary>
    public bool IsInCooldown(DateTimeOffset nowUtc, TimeSpan baseCooldown)
    {
        if (ConsecutiveFailures <= 0 || LastAttemptUtc is null)
        {
            return false;
        }

        int exponent = Math.Min(ConsecutiveFailures - 1, 6); // Plafonne le multiplicateur à 2^6 = 64x
        TimeSpan requiredCooldown = TimeSpan.FromTicks(baseCooldown.Ticks * (1L << exponent));
        return (nowUtc - LastAttemptUtc.Value) < requiredCooldown;
    }
}

/// <summary>
/// Messages réseau propres au protocole HyParView.
/// </summary>
public abstract record HyParViewMessage
{
    private HyParViewMessage() { }

    public sealed record Join(NodeId Sender, NodeRole Role = NodeRole.Peer) : HyParViewMessage;

    public sealed record ForwardJoin(NodeId NewNode, byte Ttl, NodeId Sender, NodeRole Role = NodeRole.Peer) : HyParViewMessage;

    public sealed record Neighbor(NodeId Sender, bool HighPriority, NodeRole Role = NodeRole.Peer) : HyParViewMessage;

    public sealed record NeighborAck(NodeId Sender, bool Accepted, NodeRole Role = NodeRole.Peer) : HyParViewMessage;

    public sealed record Disconnect(NodeId Sender) : HyParViewMessage;

    public sealed record Shuffle(NodeId Origin, IReadOnlyList<NodeId> Sample, byte Ttl, NodeId Sender) : HyParViewMessage;

    public sealed record ShuffleReply(NodeId Sender, IReadOnlyList<NodeId> Sample) : HyParViewMessage;
}

/// <summary>
/// Messages réseau propres au protocole Plumtree.
/// </summary>
public abstract record PlumtreeMessage
{
    private PlumtreeMessage() { }

    public sealed record Gossip(MsgId MessageId, uint Round, byte[] Payload) : PlumtreeMessage;

    public sealed record Prune : PlumtreeMessage;

    public sealed record IHave(IReadOnlyList<(MsgId MessageId, uint Round)> Entries) : PlumtreeMessage;

    public sealed record Graft(MsgId MessageId, uint Round) : PlumtreeMessage;
}

/// <summary>
/// Enveloppe globale des messages réseau échangés entre pairs.
/// </summary>
public abstract record NetworkMessage
{
    private NetworkMessage() { }

    public sealed record HyParView(HyParViewMessage Message) : NetworkMessage;

    public sealed record Plumtree(PlumtreeMessage Message) : NetworkMessage;
}

/// <summary>
/// Effets de bord (actions de sortie) produits par la couche Sans-I/O
/// et destinés à être exécutés par Whispernet.Transport.Iroh ou testés en mémoire.
/// </summary>
public abstract record OutAction
{
    private OutAction() { }

    /// <summary>
    /// Demande à la couche transport d'ouvrir une connexion vers Target.
    /// Si <paramref name="AllowDhtLookup"/> est vrai, la couche Iroh peut résoudre Target par son NodeId seul via Mainline DHT / Pkarr.
    /// </summary>
    public sealed record Connect(NodeId Target, bool AllowDhtLookup = true) : OutAction;

    /// <summary>Demande à la couche transport de fermer la connexion active vers Target.</summary>
    public sealed record CloseConnection(NodeId Target) : OutAction;

    /// <summary>Demande l'envoi d'un message protocolaire à Target.</summary>
    public sealed record Send(NodeId Target, NetworkMessage Message) : OutAction;

    /// <summary>Livre un message applicatif reçu à la couche supérieure (Whispernet.App).</summary>
    public sealed record DeliverToApp(MsgId MessageId, byte[] Payload) : OutAction;

    /// <summary>Démarre ou redémarre un timer pour la durée indiquée.</summary>
    public sealed record StartTimer(TimerKind Timer, TimeSpan Duration) : OutAction;

    /// <summary>Annule un timer en cours.</summary>
    public sealed record CancelTimer(TimerKind Timer) : OutAction;

    /// <summary>Événement interne de couplage : un pair vient d'entrer dans la Vue Active HyParView.</summary>
    public sealed record NeighborUp(NodeId Peer, NodeRole Role = NodeRole.Peer) : OutAction;

    /// <summary>Événement interne de couplage : un pair vient de quitter la Vue Active HyParView.</summary>
    public sealed record NeighborDown(NodeId Peer) : OutAction;

    /// <summary>Trace de débogage produite par la couche Sans-I/O (état des vues, décisions prises, messages).</summary>
    public sealed record Log(ProtocolLogLevel Level, string Category, string Message) : OutAction;

    /// <summary>Demande à la couche de persistance (SQLite) de sauvegarder les entrées mises à jour et de supprimer les pairs évincés.</summary>
    public sealed record PersistPeerCache(
        IReadOnlyList<CachedPeerEntry> UpsertedEntries,
        IReadOnlyList<NodeId> EvictedNodes) : OutAction;

    /// <summary>Signale à l'application que tous les pairs du cache (ainsi que les Points de Meeting) sont actuellement injoignables.</summary>
    public sealed record PeerCacheExhausted(string Reason) : OutAction;
}

/// <summary>
/// Niveau de sévérité des traces de débogage Sans-I/O.
/// </summary>
public enum ProtocolLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4
}
