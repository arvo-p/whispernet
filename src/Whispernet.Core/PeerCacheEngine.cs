namespace Whispernet.Core;

/// <summary>
/// Moteur Sans-I/O de gestion du Peer Cache, de la tolérance aux échecs consécutifs,
/// de la purge d'historique périmé (TTL + LRU) et de la reconnexion automatique au réseau.
/// </summary>
public static class PeerCacheEngine
{
    private const string LogCat = "PeerCache";

    private static void EmitLog(
        HyParViewState state,
        List<OutAction> actions,
        ProtocolLogLevel level,
        string message)
    {
        string summary = $"[Active={state.ActiveView.Count}, Passive={state.PassiveView.Count}, CacheMeta={state.PeerCacheMetadata.Count}, MeetingPoints={state.BootstrapMeetingPoints.Count}]";
        actions.Add(new OutAction.Log(level, LogCat, $"[Self={state.SelfId}] {message} {summary}"));
    }

    /// <summary>
    /// Ordonne les entrées du cache selon la hiérarchie de priorité :
    /// 1. En 1er : Points de Meeting 24/24 (<see cref="NodeRole.MeetingPoint"/>),
    /// 2. En 2e : Pairs frais et fidèles (<c>ConsecutiveFailures == 0</c>), triés par fraîcheur (<c>LastSeenUtc</c> le plus récent d'abord)
    ///    et fidélité (<c>SuccessCount</c> décroissant),
    /// 3. En 3e : Pairs en probation (<c>0 &lt; ConsecutiveFailures &lt; Max</c>), du moins d'échecs au plus d'échecs,
    /// 4. En dernier : Pairs les plus anciens au sens de <c>LastSeenUtc</c> (politique LRU).
    /// </summary>
    public static IReadOnlyList<CachedPeerEntry> SortByPriority(IEnumerable<CachedPeerEntry> entries)
    {
        return entries
            .OrderByDescending(e => e.Role == NodeRole.MeetingPoint ? 1 : 0)
            .ThenBy(e => e.ConsecutiveFailures == 0 ? 0 : 1)
            .ThenBy(e => e.ConsecutiveFailures)
            .ThenByDescending(e => e.LastSeenUtc)
            .ThenByDescending(e => e.SuccessCount)
            .ToList();
    }

    /// <summary>
    /// Hydrate <see cref="HyParViewState"/> à partir du cache SQLite chargé au démarrage :
    /// - Évince les pairs ayant dépassé <c>MaxConsecutiveFailuresBeforeEviction</c>.
    /// - Sépare les pairs frais des pairs périmés (<c>LastSeenUtc</c> &gt; <c>PeerCacheMaxAge</c>).
    /// - Si tous les pairs standards sont périmés (retour après une longue absence) et que <c>AllowStaleFallbackWhenCacheEmpty</c>
    ///   est actif, accorde un sursis d'1 seul essai (<c>ConsecutiveFailures = Max - 1</c>) aux anciens pairs les plus fiables.
    /// - Pré-remplit <c>PassiveView</c>, <c>KnownRoles</c> et <c>BootstrapMeetingPoints</c>.
    /// </summary>
    public static IReadOnlyList<OutAction> HydrateFromPeerCache(
        HyParViewState state,
        IEnumerable<CachedPeerEntry> loadedEntries,
        DateTimeOffset nowUtc)
    {
        var actions = new List<OutAction>();
        var evictedNodes = new List<NodeId>();
        var upsertedEntries = new List<CachedPeerEntry>();

        var validFreshPeers = new List<CachedPeerEntry>();
        var stalePeers = new List<CachedPeerEntry>();

        foreach (CachedPeerEntry entry in loadedEntries)
        {
            if (entry.NodeId == state.SelfId)
            {
                continue;
            }

            if (entry.Role == NodeRole.MeetingPoint)
            {
                state.BootstrapMeetingPoints.Add(entry.NodeId);
                state.KnownRoles[entry.NodeId] = NodeRole.MeetingPoint;
                state.PeerCacheMetadata[entry.NodeId] = entry;
                validFreshPeers.Add(entry);
                continue;
            }

            if (entry.ConsecutiveFailures >= state.Config.MaxConsecutiveFailuresBeforeEviction)
            {
                evictedNodes.Add(entry.NodeId);
                EmitLog(state, actions, ProtocolLogLevel.Info,
                    $"Hydratation : éviction immédiate de {entry.NodeId} (ConsecutiveFailures={entry.ConsecutiveFailures} >= {state.Config.MaxConsecutiveFailuresBeforeEviction}).");
                continue;
            }

            if (entry.IsStale(nowUtc, state.Config.PeerCacheMaxAge))
            {
                stalePeers.Add(entry);
            }
            else
            {
                validFreshPeers.Add(entry);
            }
        }

        // Cas où des pairs frais existent : on évince les pairs périmés (> PeerCacheMaxAge)
        bool hasFreshRegularPeers = validFreshPeers.Any(e => e.Role == NodeRole.Peer);
        if (hasFreshRegularPeers || !state.Config.AllowStaleFallbackWhenCacheEmpty)
        {
            foreach (CachedPeerEntry stale in stalePeers)
            {
                evictedNodes.Add(stale.NodeId);
                EmitLog(state, actions, ProtocolLogLevel.Debug,
                    $"Hydratation : purge du pair périmé {stale.NodeId} (LastSeenUtc={stale.LastSeenUtc:O}).");
            }
        }
        else if (stalePeers.Count > 0)
        {
            // Historique 100 % périmé : mode Grace Fallback (1 seule chance avant éviction)
            int probationFailures = Math.Max(1, state.Config.MaxConsecutiveFailuresBeforeEviction - 1);
            var graceCandidates = stalePeers
                .OrderByDescending(e => e.SuccessCount)
                .ThenByDescending(e => e.LastSeenUtc)
                .Take(state.Config.MaxPassiveViewSize)
                .ToList();

            EmitLog(state, actions, ProtocolLogLevel.Warning,
                $"Historique périmé détecté (aucun pair standard frais < {state.Config.PeerCacheMaxAge.TotalDays}j) -> activation du mode Grace Fallback pour {graceCandidates.Count} anciens pairs (1 essai accordé avant éviction).");

            var keptIds = new HashSet<NodeId>();
            foreach (CachedPeerEntry candidate in graceCandidates)
            {
                var onProbation = candidate with { ConsecutiveFailures = probationFailures };
                validFreshPeers.Add(onProbation);
                upsertedEntries.Add(onProbation);
                keptIds.Add(candidate.NodeId);
            }

            foreach (CachedPeerEntry droppedStale in stalePeers.Where(s => !keptIds.Contains(s.NodeId)))
            {
                evictedNodes.Add(droppedStale.NodeId);
            }
        }

        // Tri selon la hiérarchie stricte (MeetingPoint en 1er, plus anciens LastSeenUtc en dernier)
        var orderedPeers = SortByPriority(validFreshPeers);

        foreach (CachedPeerEntry entry in orderedPeers)
        {
            state.KnownRoles[entry.NodeId] = entry.Role;
            state.PeerCacheMetadata[entry.NodeId] = entry;

            if (!state.ActiveView.Contains(entry.NodeId) && state.PassiveView.Count < state.Config.MaxPassiveViewSize)
            {
                state.PassiveView.Add(entry.NodeId);
            }
        }

        EmitLog(state, actions, ProtocolLogLevel.Info,
            $"Cache hydraté : {state.PassiveView.Count} pairs chargés dans PassiveView ({state.BootstrapMeetingPoints.Count} MeetingPoints), {evictedNodes.Count} pairs périmés/en échec purgés.");

        if (upsertedEntries.Count > 0 || evictedNodes.Count > 0)
        {
            actions.Add(new OutAction.PersistPeerCache(upsertedEntries, evictedNodes));
        }

        return actions;
    }

    /// <summary>
    /// Enregistre une interaction réussie avec un pair (connexion acceptée, JOIN, NeighborUp) :
    /// remet <c>ConsecutiveFailures</c> à 0, incrémente <c>SuccessCount</c> et rafraîchit <c>LastSeenUtc</c>.
    /// </summary>
    public static CachedPeerEntry RecordPeerSuccess(
        HyParViewState state,
        NodeId peer,
        DateTimeOffset nowUtc,
        List<OutAction>? actions = null,
        string? endpointTicket = null)
    {
        NodeRole role = state.KnownRoles.GetValueOrDefault(peer, NodeRole.Peer);
        if (role == NodeRole.MeetingPoint)
        {
            state.BootstrapMeetingPoints.Add(peer);
        }

        CachedPeerEntry updated;
        if (state.PeerCacheMetadata.TryGetValue(peer, out CachedPeerEntry? existing))
        {
            updated = existing with
            {
                Role = role != NodeRole.Peer ? role : existing.Role,
                EndpointTicket = endpointTicket ?? existing.EndpointTicket,
                LastSeenUtc = nowUtc,
                SuccessCount = existing.SuccessCount + 1,
                ConsecutiveFailures = 0,
                LastAttemptUtc = nowUtc
            };
        }
        else
        {
            updated = new CachedPeerEntry(
                NodeId: peer,
                Role: role,
                EndpointTicket: endpointTicket,
                LastSeenUtc: nowUtc,
                SuccessCount: 1,
                ConsecutiveFailures: 0,
                TotalFailureCount: 0,
                LastAttemptUtc: nowUtc);
        }

        state.PeerCacheMetadata[peer] = updated;
        actions?.Add(new OutAction.PersistPeerCache(new[] { updated }, Array.Empty<NodeId>()));
        return updated;
    }

    /// <summary>
    /// Observe un pair découvert passivement (via Shuffle, ShuffleReply ou ForwardJoin) sans encore l'avoir contacté directement.
    /// Si le cache dépasse sa capacité, évince les pairs les plus anciens au sens de <c>LastSeenUtc</c> (LRU).
    /// </summary>
    public static void ObserveDiscoveredPeer(
        HyParViewState state,
        NodeId peer,
        DateTimeOffset nowUtc,
        List<OutAction>? actions = null,
        string? endpointTicket = null)
    {
        if (peer == state.SelfId)
        {
            return;
        }

        NodeRole role = state.KnownRoles.GetValueOrDefault(peer, NodeRole.Peer);
        if (role == NodeRole.MeetingPoint)
        {
            state.BootstrapMeetingPoints.Add(peer);
        }

        if (state.PeerCacheMetadata.TryGetValue(peer, out CachedPeerEntry? existing))
        {
            if (endpointTicket is not null && existing.EndpointTicket != endpointTicket)
            {
                var updated = existing with { EndpointTicket = endpointTicket };
                state.PeerCacheMetadata[peer] = updated;
                actions?.Add(new OutAction.PersistPeerCache(new[] { updated }, Array.Empty<NodeId>()));
            }
            return;
        }

        var evicted = new List<NodeId>();
        int maxCacheCapacity = state.Config.MaxPassiveViewSize * 2;
        if (state.PeerCacheMetadata.Count >= maxCacheCapacity)
        {
            // Politique LRU : évincer le pair standard le plus ancien (LastSeenUtc le plus vieux / en fin de liste triée)
            var oldestStandardPeer = SortByPriority(state.PeerCacheMetadata.Values)
                .LastOrDefault(e => e.Role != NodeRole.MeetingPoint && !state.ActiveView.Contains(e.NodeId));

            if (oldestStandardPeer is not null)
            {
                state.PeerCacheMetadata.Remove(oldestStandardPeer.NodeId);
                evicted.Add(oldestStandardPeer.NodeId);
            }
        }

        var entry = new CachedPeerEntry(
            NodeId: peer,
            Role: role,
            EndpointTicket: endpointTicket,
            LastSeenUtc: nowUtc,
            SuccessCount: 0,
            ConsecutiveFailures: 0,
            TotalFailureCount: 0);

        state.PeerCacheMetadata[peer] = entry;
        actions?.Add(new OutAction.PersistPeerCache(new[] { entry }, evicted));
    }

    /// <summary>
    /// Enregistre un échec de connexion ou un rejet (<c>OnConnectionLost</c> / <c>NeighborAck(false)</c>) :
    /// - Incrémente <c>ConsecutiveFailures</c> et <c>TotalFailureCount</c>.
    /// - Si <c>ConsecutiveFailures &gt;= MaxConsecutiveFailuresBeforeEviction</c> (et que ce n'est pas un <c>MeetingPoint</c>),
    ///   évince définitivement le pair du cache et de la <c>PassiveView</c>.
    /// </summary>
    public static void RecordPeerFailure(
        HyParViewState state,
        NodeId peer,
        DateTimeOffset nowUtc,
        List<OutAction> actions)
    {
        if (peer == state.SelfId)
        {
            return;
        }

        NodeRole role = state.KnownRoles.GetValueOrDefault(peer, NodeRole.Peer);
        state.PeerCacheMetadata.TryGetValue(peer, out CachedPeerEntry? existing);

        int newConsecutive = (existing?.ConsecutiveFailures ?? 0) + 1;
        int newTotal = (existing?.TotalFailureCount ?? 0) + 1;

        var updated = new CachedPeerEntry(
            NodeId: peer,
            Role: existing?.Role ?? role,
            EndpointTicket: existing?.EndpointTicket,
            LastSeenUtc: existing?.LastSeenUtc ?? nowUtc,
            SuccessCount: existing?.SuccessCount ?? 0,
            ConsecutiveFailures: newConsecutive,
            TotalFailureCount: newTotal,
            LastAttemptUtc: nowUtc);

        int maxFailures = state.Config.MaxConsecutiveFailuresBeforeEviction;
        if (updated.ShouldEvict(nowUtc, maxFailures, state.Config.PeerCacheMaxAge))
        {
            state.PeerCacheMetadata.Remove(peer);
            state.PassiveView.Remove(peer);
            state.PendingNeighbors.Remove(peer);

            EmitLog(state, actions, ProtocolLogLevel.Warning,
                $"Seuil de tolérance dépassé pour {peer} (ConsecutiveFailures={newConsecutive}/{maxFailures}) -> ÉVICTION définitive du Peer Cache.");
            actions.Add(new OutAction.PersistPeerCache(Array.Empty<CachedPeerEntry>(), new[] { peer }));
        }
        else
        {
            state.PeerCacheMetadata[peer] = updated;
            int remaining = updated.Role == NodeRole.MeetingPoint
                ? int.MaxValue
                : Math.Max(0, maxFailures - newConsecutive);

            string remainingLabel = updated.Role == NodeRole.MeetingPoint ? "immunisé (MeetingPoint)" : $"{remaining} essai(s) restant(s) avant éviction";
            EmitLog(state, actions, ProtocolLogLevel.Debug,
                $"Échec enregistré pour {peer} (ConsecutiveFailures={newConsecutive}, TotalFailures={newTotal}) -> conservé en cache ({remainingLabel}).");
            actions.Add(new OutAction.PersistPeerCache(new[] { updated }, Array.Empty<NodeId>()));
        }
    }

    /// <summary>
    /// Exporte un snapshot complet et trié du cache de pairs (les plus récents/fiables en premier, les plus anciens en dernier).
    /// </summary>
    public static IReadOnlyList<CachedPeerEntry> ExportPeerCacheSnapshot(
        HyParViewState state,
        DateTimeOffset nowUtc)
    {
        foreach (NodeId activePeer in state.ActiveView)
        {
            if (!state.PeerCacheMetadata.ContainsKey(activePeer))
            {
                RecordPeerSuccess(state, activePeer, nowUtc);
            }
        }

        foreach (NodeId passivePeer in state.PassiveView)
        {
            if (!state.PeerCacheMetadata.ContainsKey(passivePeer))
            {
                ObserveDiscoveredPeer(state, passivePeer, nowUtc);
            }
        }

        return SortByPriority(state.PeerCacheMetadata.Values);
    }

    /// <summary>
    /// Déclenche une tentative de reconnexion au réseau lorsque <c>ActiveView</c> est vide :
    /// 1. Parcourt le cache dans l'ordre strict de priorité (MeetingPoints -> pairs frais et fidèles -> pairs en probation -> plus anciens en dernier).
    /// 2. Ignore les pairs actuellement en période de refroidissement (<c>IsInCooldown</c>), sauf si aucun autre candidat n'est disponible.
    /// 3. Contacte jusqu'à <c>MaxReconnectCandidatesPerAttempt</c> pairs et arme <c>ReconnectTimer</c>.
    /// </summary>
    public static IReadOnlyList<OutAction> InitiateReconnection(
        HyParViewState state,
        DateTimeOffset nowUtc)
    {
        var actions = new List<OutAction>();
        if (state.ActiveView.Count > 0)
        {
            return actions;
        }

        // S'assurer que tous les pairs de PassiveView et BootstrapMeetingPoints sont référencés dans PeerCacheMetadata
        foreach (NodeId mp in state.BootstrapMeetingPoints)
        {
            state.KnownRoles[mp] = NodeRole.MeetingPoint;
            ObserveDiscoveredPeer(state, mp, nowUtc);
        }
        foreach (NodeId p in state.PassiveView)
        {
            ObserveDiscoveredPeer(state, p, nowUtc);
        }

        var allOrdered = SortByPriority(state.PeerCacheMetadata.Values)
            .Where(e => e.NodeId != state.SelfId && !state.PendingNeighbors.Contains(e.NodeId))
            .ToList();

        if (allOrdered.Count == 0)
        {
            EmitLog(state, actions, ProtocolLogLevel.Error,
                "Reconnexion impossible : le Peer Cache et la PassiveView sont totalement vides.");
            actions.Add(new OutAction.PeerCacheExhausted("Aucun pair ni Point de Meeting disponible dans le cache."));
            return actions;
        }

        // Filtrer les pairs hors cooldown (ou prendre les premiers si tous sont en cooldown)
        var readyCandidates = allOrdered
            .Where(e => !e.IsInCooldown(nowUtc, state.Config.RetryCooldownBase))
            .Take(state.Config.MaxReconnectCandidatesPerAttempt)
            .ToList();

        if (readyCandidates.Count == 0)
        {
            EmitLog(state, actions, ProtocolLogLevel.Warning,
                $"Tous les candidats du cache ({allOrdered.Count}) sont en période de cooldown -> planification de ReconnectTimer dans {state.Config.ReconnectRetryInterval.TotalSeconds}s.");
            actions.Add(new OutAction.StartTimer(new TimerKind.ReconnectTimer(), state.Config.ReconnectRetryInterval));
            return actions;
        }

        foreach (CachedPeerEntry candidate in readyCandidates)
        {
            state.PendingNeighbors.Add(candidate.NodeId);
            actions.Add(new OutAction.Connect(candidate.NodeId, AllowDhtLookup: true));

            if (candidate.Role == NodeRole.MeetingPoint)
            {
                EmitLog(state, actions, ProtocolLogLevel.Info,
                    $"Tentative de reconnexion prioritaire via Point de Meeting {candidate.NodeId} (DHT/Pkarr autorisé, envoi JOIN).");
                actions.Add(new OutAction.Send(
                    candidate.NodeId,
                    new NetworkMessage.HyParView(new HyParViewMessage.Join(state.SelfId, state.SelfRole))));
            }
            else
            {
                EmitLog(state, actions, ProtocolLogLevel.Info,
                    $"Tentative de reconnexion via pair en cache {candidate.NodeId} (LastSeen={candidate.LastSeenUtc:O}, Success={candidate.SuccessCount}, Failures={candidate.ConsecutiveFailures}, AllowDht=true) -> envoi NEIGHBOR(highPriority=true).");
                actions.Add(new OutAction.Send(
                    candidate.NodeId,
                    new NetworkMessage.HyParView(new HyParViewMessage.Neighbor(state.SelfId, HighPriority: true, state.SelfRole))));
            }
        }

        actions.Add(new OutAction.StartTimer(new TimerKind.ReconnectTimer(), state.Config.ReconnectRetryInterval));
        return actions;
    }

    /// <summary>
    /// Cas 3 — Fusion des partitions réseau (Anti-îlots) déclenchée par <c>PartitionHealTimer</c> :
    /// Sélectionne un « Pair Pont » (Bridge Peer) absent de la <c>ActiveView</c> actuelle
    /// (en priorisant un <c>MeetingPoint</c> non connecté, puis un pair du cache non vu dans l'époque courante),
    /// ouvre une connexion via Mainline DHT (<c>AllowDhtLookup: true</c>) et envoie un <c>SHUFFLE</c> ou <c>JOIN</c>
    /// pour fusionner les Vues Passives des deux îlots.
    /// </summary>
    public static IReadOnlyList<OutAction> ProbeForPartitionHeal(
        HyParViewState state,
        DateTimeOffset nowUtc,
        Random? rng = null)
    {
        rng ??= Random.Shared;
        var actions = new List<OutAction>();

        if (state.ActiveView.Count == 0)
        {
            // Si on est totalement déconnecté, InitiateReconnection prend le relais
            return InitiateReconnection(state, nowUtc);
        }

        // 1. Chercher un Point de Meeting non encore dans notre ActiveView
        CachedPeerEntry? bridgeCandidate = SortByPriority(state.PeerCacheMetadata.Values)
            .FirstOrDefault(e =>
                e.NodeId != state.SelfId
                && e.Role == NodeRole.MeetingPoint
                && !state.ActiveView.Contains(e.NodeId)
                && !e.IsInCooldown(nowUtc, state.Config.RetryCooldownBase));

        // 2. Sinon, chercher un pair du cache absent de notre ActiveView et de notre îlot courant (SeenNodesInCurrentEpoch)
        bridgeCandidate ??= SortByPriority(state.PeerCacheMetadata.Values)
            .FirstOrDefault(e =>
                e.NodeId != state.SelfId
                && !state.ActiveView.Contains(e.NodeId)
                && !state.SeenNodesInCurrentEpoch.Contains(e.NodeId)
                && !e.IsInCooldown(nowUtc, state.Config.RetryCooldownBase));

        // 3. En dernier recours si ActiveView est sous le seuil sain, prendre n'importe quel pair du cache hors ActiveView
        if (bridgeCandidate is null && state.ActiveView.Count < state.Config.MinHealthyActiveViewForNoHeal)
        {
            bridgeCandidate = SortByPriority(state.PeerCacheMetadata.Values)
                .FirstOrDefault(e =>
                    e.NodeId != state.SelfId
                    && !state.ActiveView.Contains(e.NodeId)
                    && !e.IsInCooldown(nowUtc, state.Config.RetryCooldownBase));
        }

        if (bridgeCandidate is not null)
        {
            actions.Add(new OutAction.Connect(bridgeCandidate.NodeId, AllowDhtLookup: true));

            if (bridgeCandidate.Role == NodeRole.MeetingPoint)
            {
                EmitLog(state, actions, ProtocolLogLevel.Info,
                    $"Sonde Anti-Îlots (PartitionHeal) -> pont DHT vers MeetingPoint {bridgeCandidate.NodeId} (envoi JOIN).");
                actions.Add(new OutAction.Send(
                    bridgeCandidate.NodeId,
                    new NetworkMessage.HyParView(new HyParViewMessage.Join(state.SelfId, state.SelfRole))));
            }
            else
            {
                var sample = new List<NodeId> { state.SelfId };
                sample.AddRange(state.ActiveView.Take(state.Config.ShuffleActiveSampleSize));
                sample.AddRange(state.PassiveView.Take(state.Config.ShufflePassiveSampleSize));

                EmitLog(state, actions, ProtocolLogLevel.Info,
                    $"Sonde Anti-Îlots (PartitionHeal) -> pont DHT vers {bridgeCandidate.NodeId} (envoi SHUFFLE inter-îlots avec {sample.Count} pairs).");
                actions.Add(new OutAction.Send(
                    bridgeCandidate.NodeId,
                    new NetworkMessage.HyParView(new HyParViewMessage.Shuffle(
                        Origin: state.SelfId,
                        Sample: sample.Distinct().ToList(),
                        Ttl: 1,
                        Sender: state.SelfId))));
            }
        }
        else
        {
            EmitLog(state, actions, ProtocolLogLevel.Trace,
                "Sonde Anti-Îlots (PartitionHeal) : aucun Pair Pont externe disponible pour le moment.");
        }

        // Réinitialiser l'époque et réarmer le timer périodique
        state.SeenNodesInCurrentEpoch.Clear();
        foreach (NodeId active in state.ActiveView)
        {
            state.SeenNodesInCurrentEpoch.Add(active);
        }

        actions.Add(new OutAction.StartTimer(new TimerKind.PartitionHealTimer(), state.Config.PartitionHealInterval));
        return actions;
    }
}
