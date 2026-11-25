using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Whispernet.Core;
using Whispernet.Transport.Iroh;

namespace Whispernet.App;

/// <summary>
/// Message applicatif échangé dans une conversation 1-to-1 ou un petit groupe privé.
/// </summary>
public sealed record DirectChatMessage(
    string ConversationId,
    NodeId SenderId,
    string SenderName,
    string Content,
    DateTimeOffset SentAtUtc);

/// <summary>
/// Cas 1 — Service de messagerie directe 1-to-1 et petits groupes (&lt;= 10 membres) :
/// s'appuie sur la résolution Mainline DHT (par <see cref="NodeId"/> seul) lors du premier contact,
/// puis réutilise directement les connexions QUIC ouvertes sans la surcharge de HyParView/Plumtree.
/// </summary>
public sealed class DirectMeshService
{
    public const int MaxSmallGroupMembers = 10;

    private readonly IrohEndpointAdapter _transport;
    private readonly IPeerCacheRepository? _peerCache;
    private readonly ConcurrentDictionary<string, HashSet<NodeId>> _conversations = new();

    public event Action<DirectChatMessage>? MessageReceived;

    public DirectMeshService(IrohEndpointAdapter transport, IPeerCacheRepository? peerCache = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _peerCache = peerCache;
    }

    /// <summary>
    /// Enregistre ou met à jour la liste des participants d'une conversation 1-to-1 ou d'un petit groupe.
    /// </summary>
    public void RegisterConversation(string conversationId, IEnumerable<NodeId> memberIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var distinctMembers = memberIds
            .Where(id => id != _transport.SelfId)
            .Distinct()
            .ToHashSet();

        if (distinctMembers.Count > MaxSmallGroupMembers)
        {
            throw new ArgumentException(
                $"Une conversation directe est limitée à {MaxSmallGroupMembers} membres. Au-delà, utilisez HyParView + Plumtree.",
                nameof(memberIds));
        }

        _conversations[conversationId] = distinctMembers;
    }

    /// <summary>
    /// Envoie un message à tous les membres de la conversation en résolvant chaque pair absent du pool
    /// via son <see cref="NodeId"/> sur la Mainline DHT (et ticket en cache s'il existe).
    /// </summary>
    public async Task<IReadOnlyDictionary<NodeId, bool>> SendMessageAsync(
        string conversationId,
        string senderName,
        string content,
        CancellationToken ct = default)
    {
        if (!_conversations.TryGetValue(conversationId, out HashSet<NodeId>? members) || members.Count == 0)
        {
            throw new InvalidOperationException($"Conversation '{conversationId}' inconnue ou sans destinataires.");
        }

        var msg = new DirectChatMessage(
            ConversationId: conversationId,
            SenderId: _transport.SelfId,
            SenderName: senderName,
            Content: content,
            SentAtUtc: DateTimeOffset.UtcNow);

        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));

        // Charger les éventuels tickets déjà en cache SQLite pour accélérer la connexion avant le fallback DHT
        Dictionary<NodeId, CachedPeerEntry> cachedLookup = new();
        if (_peerCache is not null)
        {
            var cached = await _peerCache.LoadCachedPeersAsync(100, ct).ConfigureAwait(false);
            foreach (CachedPeerEntry entry in cached)
            {
                cachedLookup[entry.NodeId] = entry;
            }
        }

        var results = new ConcurrentDictionary<NodeId, bool>();
        var sendTasks = members.Select(async memberId =>
        {
            cachedLookup.TryGetValue(memberId, out CachedPeerEntry? cachedEntry);
            try
            {
                await _transport.SendUniAsync(
                    target: memberId,
                    payload: payload,
                    cachedTicket: cachedEntry?.EndpointTicket,
                    allowDhtLookup: true,
                    ct: ct).ConfigureAwait(false);

                results[memberId] = true;

                if (_peerCache is not null)
                {
                    var updated = cachedEntry is not null
                        ? cachedEntry with
                        {
                            LastSeenUtc = DateTimeOffset.UtcNow,
                            SuccessCount = cachedEntry.SuccessCount + 1,
                            ConsecutiveFailures = 0,
                            LastAttemptUtc = DateTimeOffset.UtcNow
                        }
                        : new CachedPeerEntry(memberId, NodeRole.Peer, null, DateTimeOffset.UtcNow, SuccessCount: 1);

                    await _peerCache.ApplyCacheUpdateAsync(new[] { updated }, Array.Empty<NodeId>(), ct).ConfigureAwait(false);
                }
            }
            catch
            {
                results[memberId] = false;
            }
        });

        await Task.WhenAll(sendTasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Traite un payload entrant reçu par la couche transport pour cette messagerie directe.
    /// </summary>
    public void HandleIncomingPayload(byte[] payload)
    {
        DirectChatMessage? msg = JsonSerializer.Deserialize<DirectChatMessage>(payload);
        if (msg is not null)
        {
            MessageReceived?.Invoke(msg);
        }
    }
}
