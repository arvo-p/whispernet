namespace Whispernet.Core;

/// <summary>
/// In-memory state of an account's friend graph, used by messaging and organic mesh overlays (e.g. App.Feed).
/// </summary>
public sealed class FriendGraphState
{
    public AuthorId SelfAuthorId { get; }
    public ulong MaxLogicalClock { get; set; }
    public Dictionary<AuthorId, FriendEntry> Entries { get; } = new();

    public FriendGraphState(AuthorId selfAuthorId, ulong maxLogicalClock = 0)
    {
        SelfAuthorId = selfAuthorId;
        MaxLogicalClock = maxLogicalClock;
    }

    public FriendStatus GetStatus(AuthorId peerAuthorId)
        => Entries.TryGetValue(peerAuthorId, out FriendEntry? entry)
            ? entry.Status
            : FriendStatus.None;

    public bool IsFriend(AuthorId peerAuthorId)
        => GetStatus(peerAuthorId) == FriendStatus.Accepted;

    public IReadOnlyList<FriendEntry> GetByStatus(FriendStatus status)
        => Entries.Values
            .Where(e => e.Status == status)
            .OrderBy(static e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<FriendEntry> GetAcceptedFriends()
        => GetByStatus(FriendStatus.Accepted);

    public IReadOnlyList<FriendEntry> GetIncomingRequests()
        => GetByStatus(FriendStatus.PendingIncoming);

    public IReadOnlyList<FriendEntry> GetOutgoingRequests()
        => GetByStatus(FriendStatus.PendingOutgoing);

    public IReadOnlyList<NodeId> GetFriendEndpoints(NodeId selfNodeId)
        => Entries.Values
            .Where(e => e.Status == FriendStatus.Accepted && e.LastKnownNodeId is NodeId nid && nid != selfNodeId)
            .Select(static e => e.LastKnownNodeId!.Value)
            .Distinct()
            .ToList();
}
