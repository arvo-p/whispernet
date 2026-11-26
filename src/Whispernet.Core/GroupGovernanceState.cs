using System.Security.Cryptography;
using System.Text;

namespace Whispernet.Core;

/// <summary>
/// État local de gouvernance et de modération d'un groupe (DAG causal + rejeu déterministe + compactage).
/// </summary>
public sealed class GroupGovernanceState
{
    public GroupId GroupId { get; }
    public GroupManifest CurrentManifest { get; internal set; }

    /// <summary>Journal des décisions de gouvernance indexé par ActionId (pour rejeu causal et synchro).</summary>
    public Dictionary<string, ModerationAction> GovernanceLog { get; } = new(StringComparer.Ordinal);

    /// <summary>Extrémités courantes du DAG de gouvernance.</summary>
    public HashSet<string> GovernanceHeadIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Modérateurs actuellement actifs après rejeu du DAG de gouvernance.</summary>
    public Dictionary<AuthorId, string> ActiveModerators { get; } = new();

    /// <summary>Dernière action gagnante par AuthorId après rejeu du DAG.</summary>
    public Dictionary<AuthorId, ModerationAction> MemberStates { get; } = new();

    /// <summary>Statuts de base issus du dernier checkpoint du manifeste (avant les actions du journal).</summary>
    internal Dictionary<AuthorId, MemberStatus> BaseCheckpointStatuses { get; } = new();

    /// <summary>Dernier NodeId (EndpointId) connu pour chaque membre non banni du salon.</summary>
    public Dictionary<AuthorId, NodeId> KnownAuthorNodes { get; } = new();

    public ulong MaxLamportClock { get; internal set; }

    public GroupGovernanceState(GroupManifest initialManifest)
    {
        ArgumentNullException.ThrowIfNull(initialManifest);
        if (!initialManifest.GroupId.IsValid)
        {
            throw new ArgumentException("Invalid GroupId in initial manifest.", nameof(initialManifest));
        }

        GroupId = initialManifest.GroupId;
        CurrentManifest = initialManifest;
        MaxLamportClock = initialManifest.CheckpointLamportClock;

        foreach ((AuthorId modId, string pubKey) in initialManifest.ModeratorPublicKeysSpkiBase64)
        {
            ActiveModerators[modId] = pubKey;
        }

        if (initialManifest.CheckpointMemberStates is not null)
        {
            foreach ((AuthorId mId, MemberStatus status) in initialManifest.CheckpointMemberStates)
            {
                BaseCheckpointStatuses[mId] = status;
            }
        }
    }

    public MemberStatus GetMemberStatus(AuthorId authorId)
    {
        if (MemberStates.TryGetValue(authorId, out ModerationAction? action))
        {
            return action.NewStatus;
        }

        return BaseCheckpointStatuses.TryGetValue(authorId, out MemberStatus baseStatus)
            ? baseStatus
            : MemberStatus.Unknown;
    }

    /// <summary>
    /// Sélectionne jusqu'à 15 parents et la prochaine horloge de Lamport pour une nouvelle décision de gouvernance.
    /// </summary>
    public (IReadOnlyList<string> Parents, ulong NextLamportClock) PrepareNextGovernanceContext()
    {
        List<string> parents = GovernanceHeadIds
            .OrderBy(static id => id, StringComparer.Ordinal)
            .Take(ModerationAction.MaxParents)
            .ToList();

        ulong nextClock = MaxLamportClock + 1;
        return (parents, nextClock);
    }

    /// <summary>
    /// Retourne les EndpointIds (NodeId) de tous les participants non bannis (Admitted ou Silenced)
    /// pour permettre un repli de reconnexion même si aucun ami direct n'est en ligne.
    /// </summary>
    public IReadOnlyList<NodeId> GetRoomMemberEndpoints(NodeId selfNodeId)
    {
        var endpoints = new HashSet<NodeId>();
        foreach ((AuthorId authorId, NodeId nodeId) in KnownAuthorNodes)
        {
            if (nodeId == selfNodeId)
            {
                continue;
            }

            if (GetMemberStatus(authorId) is MemberStatus.Admitted or MemberStatus.Silenced)
            {
                endpoints.Add(nodeId);
            }
        }

        return endpoints.ToList();
    }

    /// <summary>
    /// Calcule un condensat SHA-256 déterministe de l'état courant du registre des membres et modérateurs.
    /// </summary>
    public string ComputeModerationStateHash()
    {
        var sb = new StringBuilder();
        foreach (AuthorId modId in ActiveModerators.Keys.OrderBy(static k => k.Value, StringComparer.Ordinal))
        {
            sb.Append("M:").Append(modId.Value).Append(';');
        }

        var allAuthors = MemberStates.Keys
            .Concat(BaseCheckpointStatuses.Keys)
            .Distinct()
            .OrderBy(static k => k.Value, StringComparer.Ordinal);

        foreach (AuthorId authorId in allAuthors)
        {
            MemberStatus status = GetMemberStatus(authorId);
            ulong clock = MemberStates.TryGetValue(authorId, out ModerationAction? act)
                ? act.LamportClock
                : CurrentManifest.CheckpointLamportClock;

            sb.Append(authorId.Value)
              .Append(':')
              .Append(clock)
              .Append(':')
              .Append((int)status)
              .Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public GroupStateDigest GetDigest()
        => new(GroupId, CurrentManifest.Version, ComputeModerationStateHash());
}
