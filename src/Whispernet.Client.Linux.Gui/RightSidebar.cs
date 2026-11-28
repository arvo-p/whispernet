using System.Diagnostics;
using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Whispernet.App.Messaging;
using Whispernet.Core;
using Whispernet.Daemon.Client;
using Whispernet.Transport.Iroh;

namespace Whispernet.Client.Linux.Gui;

public sealed partial class WhispernetGuiWindow
{
    private Element BuildRightMembersColumn()
    {
        _rightTabMembersBtn = new Button()
            .Content("Members & Peers")
            .Background(ColChannelActive)
            .Foreground(ColTextHeader)
            .FontSize(11)
            .Bold()
            .CornerRadius(5)
            .Padding(8, 5)
            .OnClick(() =>
            {
                _activeRightTab = RightSidebarTab.Members;
                _uiDirty = true;
            });

        _rightTabPendingBtn = new Button()
            .Content("Pending (0)")
            .Background(ColChannelInactive)
            .Foreground(ColTextMuted)
            .FontSize(11)
            .Bold()
            .CornerRadius(5)
            .Padding(8, 5)
            .OnClick(() =>
            {
                _activeRightTab = RightSidebarTab.PendingMod;
                _uiDirty = true;
            });

        _rightHeaderTitleLabel = new Label()
            .Text("MEMBERS & MESH PEERS")
            .FontSize(12)
            .Bold()
            .Foreground(ColTextHeader);

        _rightTabsRow = new StackPanel()
            .Horizontal()
            .Spacing(6)
            .Children(_rightTabMembersBtn, _rightTabPendingBtn);

        var rightHeader = new Border()
            .Background(ColSidebar)
            .BorderBrush(ColDivider)
            .BorderThickness(1)
            .Padding(10, 10)
            .Child(
                new StackPanel()
                    .Vertical()
                    .Spacing(8)
                    .Children(_rightHeaderTitleLabel, _rightTabsRow)
            )
            .DockTop();

        _pendingListStack = new StackPanel().Vertical().Spacing(6);
        _membersListStack = new StackPanel().Vertical().Spacing(6);

        var membersScrollPanel = new Border()
            .Background(ColCardBg)
            .BorderBrush(ColDivider)
            .BorderThickness(1)
            .CornerRadius(6)
            .Padding(8, 8)
            .Child(
                new ScrollViewer()
                    .AutoVerticalScroll()
                    .NoHorizontalScroll()
                    .Content(_membersListStack)
            );

        _preApproveThumb = new Border()
            .Size(16, 16)
            .CornerRadius(8)
            .Background(ColTextHeader)
            .Left()
            .Top()
            .Margin(3, 2, 0, 0);

        _preApproveTrack = new Border()
            .Size(40, 22)
            .CornerRadius(11)
            .Background(ColInputBg)
            .BorderBrush(ColDivider)
            .BorderThickness(1)
            .Child(_preApproveThumb)
            .OnMouseDown(e =>
            {
                _preApproveInvite = !_preApproveInvite;
                _uiDirty = true;
                e.Handled = true;
            });

        _preApproveAnimProgress = _preApproveInvite ? 1.0 : 0.0;
        ApplyPreApproveSwitchVisual(_preApproveAnimProgress);

        _preApproveRow = new Grid()
            .Columns("*,Auto")
            .Children(
                new Label()
                    .Text("Pre-approve")
                    .FontSize(12)
                    .Foreground(ColTextBody)
                    .CenterVertical()
                    .Column(0),
                _preApproveTrack
                    .CenterVertical()
                    .Column(1)
            );
        _preApproveRow.IsVisible = false;

        _rightInviteBtn = new Button()
            .Content("Invite")
            .Background(ColBlurple)
            .Foreground(ColTextHeader)
            .FontSize(12)
            .Bold()
            .CornerRadius(6)
            .Padding(12, 8)
            .HorizontalAlignment(HorizontalAlignment.Stretch)
            .OnClick(() => _ = HandleGuiCopyInviteAsync());

        _rightAddFriendBtn = new Button()
            .Content("+ Add Friend")
            .Background(ColBlurple)
            .Foreground(ColTextHeader)
            .FontSize(12)
            .Bold()
            .CornerRadius(6)
            .Padding(12, 8)
            .HorizontalAlignment(HorizontalAlignment.Stretch)
            .OnClick(OpenAddFriendModal);
        _rightAddFriendBtn.IsVisible = false;

        _rightDmRestrictBtn = new Button()
            .Content("Friends-only DMs: OFF")
            .Background(ColButtonGray)
            .Foreground(ColTextBody)
            .FontSize(11)
            .CornerRadius(6)
            .Padding(10, 6)
            .HorizontalAlignment(HorizontalAlignment.Stretch)
            .OnClick(() => _ = _friendService.SetRestrictDmsToFriendsAsync(!_friendService.RestrictDmsToFriends));
        _rightDmRestrictBtn.IsVisible = false;

        var membersBottomActions = new StackPanel()
            .Vertical()
            .Spacing(8)
            .Margin(0, 10, 0, 0)
            .Children(_preApproveRow, _rightInviteBtn, _rightDmRestrictBtn, _rightAddFriendBtn)
            .DockBottom();

        _rightMembersSectionTitleLabel = new Label()
            .Text("GROUP MEMBERS")
            .FontSize(11)
            .Bold()
            .Foreground(ColTextMuted)
            .Margin(0, 0, 0, 8)
            .DockTop();

        _rightMembersView = new DockPanel()
            .Padding(12, 12)
            .Children(
                _rightMembersSectionTitleLabel,
                membersBottomActions,
                membersScrollPanel
            );

        _rightPendingSectionTitleLabel = new Label()
            .Text("PENDING JOIN REQUESTS")
            .FontSize(11)
            .Bold()
            .Foreground(ColAmber)
            .Margin(0, 0, 0, 8)
            .DockTop();

        _rightPendingView = new DockPanel()
            .Padding(12, 12)
            .Children(
                _rightPendingSectionTitleLabel,
                new ScrollViewer()
                    .AutoVerticalScroll()
                    .NoHorizontalScroll()
                    .Content(_pendingListStack)
            );
        _rightPendingView.IsVisible = false;

        var rightContentHost = new Grid()
            .Children(_rightMembersView, _rightPendingView);

        return new Border()
            .Background(ColSidebar)
            .Child(
                new DockPanel()
                    .Children(rightHeader, rightContentHost)
            );
    }

    private void ApplyPreApproveSwitchVisual(double progress)
    {
        double t = Math.Clamp(progress, 0.0, 1.0);
        if (_preApproveTrack is not null)
        {
            byte r = (byte)Math.Round(ColInputBg.R + (ColBlurple.R - ColInputBg.R) * t);
            byte g = (byte)Math.Round(ColInputBg.G + (ColBlurple.G - ColInputBg.G) * t);
            byte b = (byte)Math.Round(ColInputBg.B + (ColBlurple.B - ColInputBg.B) * t);
            _preApproveTrack.Background(Color.FromRgb(r, g, b));
        }

        if (_preApproveThumb is not null)
        {
            double left = 3.0 + (18.0 * t);
            _preApproveThumb.Margin(left, 2, 0, 0);
        }
    }

    // =========================================================================
    // RAFRAÎCHISSEMENT DYNAMIQUE DE L'ESPACE PRINCIPAL
    // =========================================================================

    private void RebuildRightSidebar(
        GroupGovernanceState? activeGov,
        IReadOnlyList<GroupGovernanceState> groups,
        AccountSession? session,
        IReadOnlyList<GuiPeerStatus> recentPeers)
    {
        bool isHomeMode = activeGov is null;
        bool isModerator = activeGov is not null && session is not null &&
            (activeGov.ActiveModerators.ContainsKey(session.Profile.AuthorId) ||
             activeGov.CurrentManifest.ModeratorPublicKeysSpkiBase64.ContainsKey(session.Profile.AuthorId));
        bool canShowPendingTab = isHomeMode || isModerator;
        if (!canShowPendingTab && _activeRightTab == RightSidebarTab.PendingMod)
        {
            _activeRightTab = RightSidebarTab.Members;
        }

        _rightHeaderTitleLabel?.Text(isHomeMode ? "FRIENDS" : "MEMBERS & MESH PEERS");
        if (_rightTabsRow is not null)
        {
            _rightTabsRow.IsVisible = true;
        }
        _rightMembersSectionTitleLabel?.Text(isHomeMode ? "FRIENDS LIST" : "GROUP MEMBERS");
        _rightPendingSectionTitleLabel?.Text(isHomeMode ? "PENDING FRIEND REQUESTS" : "PENDING JOIN REQUESTS");

        IReadOnlyList<FriendEntry> incomingFriends = isHomeMode ? _friendService.GetIncomingRequests() : Array.Empty<FriendEntry>();
        IReadOnlyList<FriendEntry> outgoingFriends = isHomeMode ? _friendService.GetOutgoingRequests() : Array.Empty<FriendEntry>();
        int pendingCount = isHomeMode
            ? incomingFriends.Count
            : (activeGov is not null ? _govService.GetPendingJoinRequests(activeGov.GroupId).Count : 0);

        if (_rightTabPendingBtn is not null)
        {
            _rightTabPendingBtn.IsVisible = canShowPendingTab;
            _rightTabPendingBtn
                .Content($"Pending ({pendingCount})")
                .Background(_activeRightTab == RightSidebarTab.PendingMod ? ColChannelActive : ColChannelInactive)
                .Foreground(_activeRightTab == RightSidebarTab.PendingMod
                    ? ColTextHeader
                    : (pendingCount > 0 ? ColAmber : ColTextMuted));
        }

        _rightTabMembersBtn?
            .Content(isHomeMode ? "Friends" : "Members & Peers")
            .Background(_activeRightTab == RightSidebarTab.Members ? ColChannelActive : ColChannelInactive)
            .Foreground(_activeRightTab == RightSidebarTab.Members ? ColTextHeader : ColTextMuted);

        if (_rightMembersView is not null)
        {
            _rightMembersView.IsVisible = _activeRightTab == RightSidebarTab.Members;
        }
        if (_preApproveRow is not null)
        {
            _preApproveRow.IsVisible = isModerator;
        }
        if (_rightInviteBtn is not null)
        {
            bool canInvite = activeGov is not null;
            _rightInviteBtn.IsVisible = canInvite;
            _rightInviteBtn
                .IsEnabled(canInvite)
                .Background(canInvite ? ColBlurple : ColButtonGray);
        }
        if (_rightAddFriendBtn is not null)
        {
            _rightAddFriendBtn.IsVisible = isHomeMode;
        }
        if (_rightDmRestrictBtn is not null)
        {
            bool restrict = _friendService.RestrictDmsToFriends;
            _rightDmRestrictBtn.IsVisible = isHomeMode;
            _rightDmRestrictBtn
                .Content(restrict ? "Friends-only DMs: ON" : "Friends-only DMs: OFF")
                .Background(restrict ? ColGreen : ColButtonGray);
        }
        if (_rightPendingView is not null)
        {
            _rightPendingView.IsVisible = canShowPendingTab && _activeRightTab == RightSidebarTab.PendingMod;
        }

        if (_pendingListStack is not null)
        {
            _pendingListStack.Clear();
            if (activeGov is not null)
            {
                IReadOnlyList<JoinApprovalRequest> pending = _govService.GetPendingJoinRequests(activeGov.GroupId);
                if (pending.Count == 0)
                {
                    _pendingListStack.Add(new Label().Text("No pending requests.").FontSize(12).Foreground(ColTextMuted));
                }
                else
                {
                    foreach (JoinApprovalRequest req in pending)
                    {
                        AuthorId reqAuthor = req.RequesterAuthorId;
                        _pendingListStack.Add(
                            new Border()
                                .Background(ColCardBg)
                                .CornerRadius(6)
                                .Padding(8)
                                .Child(
                                    new StackPanel()
                                        .Vertical()
                                        .Spacing(4)
                                        .Children(
                                            new Label()
                                                .Text($"{req.RequesterUsername} (@{ShortId(reqAuthor.Value)})")
                                                .FontSize(12)
                                                .Bold()
                                                .Foreground(ColTextHeader),
                                            new StackPanel()
                                                .Horizontal()
                                                .Spacing(6)
                                                .Children(
                                                    new Button()
                                                        .Content("Accept")
                                                        .Background(ColGreen)
                                                        .Foreground(ColTextHeader)
                                                        .FontSize(11)
                                                        .Bold()
                                                        .CornerRadius(4)
                                                        .Padding(8, 3)
                                                        .OnClick(() => _ = ExecuteSlashCommandAsync($"mod accept {reqAuthor.Value}")),
                                                    new Button()
                                                        .Content("Ignore")
                                                        .Background(ColButtonGray)
                                                        .Foreground(ColTextHeader)
                                                        .FontSize(11)
                                                        .CornerRadius(4)
                                                        .Padding(8, 3)
                                                        .OnClick(() => _ = ExecuteSlashCommandAsync($"mod ignore {reqAuthor.Value}"))
                                                )
                                        )
                                )
                        );
                    }
                }
            }
            else
            {
                if (incomingFriends.Count == 0)
                {
                    _pendingListStack.Add(new Label().Text("No incoming friend requests.").FontSize(12).Foreground(ColTextMuted));
                }
                else
                {
                    foreach (FriendEntry req in incomingFriends)
                    {
                        AuthorId reqId = req.PeerAuthorId;
                        string reqName = req.DisplayName;
                        _pendingListStack.Add(
                            new Border()
                                .Background(ColCardBg)
                                .CornerRadius(6)
                                .Padding(8, 6)
                                .Child(
                                    new StackPanel()
                                        .Vertical()
                                        .Spacing(6)
                                        .Children(
                                            new Label().Text(reqName).FontSize(12).Bold().Foreground(ColTextHeader),
                                            new StackPanel()
                                                .Horizontal()
                                                .Spacing(6)
                                                .Children(
                                                    new Button()
                                                        .Content("Accept")
                                                        .Background(ColGreen)
                                                        .Foreground(ColTextHeader)
                                                        .FontSize(11)
                                                        .Bold()
                                                        .CornerRadius(4)
                                                        .Padding(8, 3)
                                                        .OnClick(() => _ = _friendService.AcceptFriendRequestAsync(reqId)),
                                                    new Button()
                                                        .Content("Reject")
                                                        .Background(ColRed)
                                                        .Foreground(ColTextHeader)
                                                        .FontSize(11)
                                                        .CornerRadius(4)
                                                        .Padding(8, 3)
                                                        .OnClick(() => _ = _friendService.RejectFriendRequestAsync(reqId))
                                                )
                                        )
                                )
                        );
                    }
                }
            }
        }

        if (_membersListStack is not null)
        {
            _membersListStack.Clear();
            if (activeGov is not null)
            {
                bool canModerate = session is not null &&
                    (activeGov.ActiveModerators.ContainsKey(session.Profile.AuthorId) ||
                     activeGov.CurrentManifest.ModeratorPublicKeysSpkiBase64.ContainsKey(session.Profile.AuthorId));

                var sortedMembers = activeGov.MemberStates
                    .Select(kv =>
                    {
                        AuthorId mId = kv.Key;
                        ModerationAction act = kv.Value;
                        MemberStatus status = activeGov.GetMemberStatus(mId);
                        string name;
                        lock (_stateLock)
                        {
                            name = _knownAuthorNames.TryGetValue(mId, out string? n) && !string.IsNullOrWhiteSpace(n)
                                ? n
                                : $"@{ShortId(mId.Value)}";
                        }
                        bool isSelfOnline = session is not null && session.Profile.AuthorId == mId && _transport is not null;
                        bool seenOnline = isSelfOnline ||
                            (act.TargetLastKnownNodeId is NodeId nid && recentPeers.Any(p => p.NodeId == nid));
                        return (MemberId: mId, Status: status, DisplayName: name, IsOnline: seenOnline, NodeId: act.TargetLastKnownNodeId);
                    })
                    .OrderByDescending(static m => m.IsOnline)
                    .ThenBy(static m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var entry in sortedMembers)
                {
                    AuthorId memberId = entry.MemberId;
                    MemberStatus st = entry.Status;
                    string displayName = entry.DisplayName;
                    bool isOnline = entry.IsOnline;
                    NodeId? memberNodeId = entry.NodeId;
                    bool isSelf = session is not null && session.Profile.AuthorId == memberId;
                    string statusDot = isOnline ? "● " : "○ ";

                    _membersListStack.Add(
                        new Button()
                            .Content($"{statusDot}{displayName}{(st == MemberStatus.Admitted ? string.Empty : $"  [{st}]")}")
                            .StretchHorizontal()
                            .Background(ColCardBg)
                            .Foreground(isOnline ? ColGreen : ColTextMuted)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(6)
                            .Padding(10, 7)
                            .ToolTip("Click to view AuthorId, Iroh ticket, and moderation options")
                            .OnClick(() => OpenMemberProfileModal(memberId, displayName, st, isOnline, memberNodeId, canModerate, isSelf))
                    );
                }
            }
            else
            {
                IReadOnlyList<FriendEntry> accepted = _friendService.GetAcceptedFriends();
                if (outgoingFriends.Count > 0)
                {
                    _membersListStack.Add(new Label().Text($"PENDING SENT ({outgoingFriends.Count})").FontSize(10).Bold().Foreground(ColTextMuted));
                    foreach (FriendEntry outReq in outgoingFriends)
                    {
                        _membersListStack.Add(
                            new Label()
                                .Text($"⏳ {outReq.DisplayName}")
                                .FontSize(11)
                                .Foreground(ColTextMuted)
                        );
                    }
                }

                if (accepted.Count == 0 && outgoingFriends.Count == 0)
                {
                    _membersListStack.Add(new Label().Text("No friends yet. Click '+ Add Friend' below.").FontSize(12).Foreground(ColTextMuted).TextWrapping(TextWrapping.Wrap));
                }
                else if (accepted.Count > 0)
                {
                    if (outgoingFriends.Count > 0)
                    {
                        _membersListStack.Add(new Label().Text($"FRIENDS ({accepted.Count})").FontSize(10).Bold().Foreground(ColTextMuted));
                    }
                    foreach (FriendEntry friend in accepted)
                    {
                        AuthorId capturedId = friend.PeerAuthorId;
                        string capturedName = friend.DisplayName;
                        bool isOnline = friend.LastKnownNodeId is NodeId nid && recentPeers.Any(p => p.NodeId == nid);
                        string statusDot = isOnline ? "● " : "○ ";

                        _membersListStack.Add(
                            new Grid()
                                .Columns("*,Auto")
                                .Spacing(4)
                                .Children(
                                    new Button()
                                        .Content($"{statusDot}{capturedName}")
                                        .StretchHorizontal()
                                        .Background(ColCardBg)
                                        .Foreground(isOnline ? ColGreen : ColTextMuted)
                                        .FontSize(12)
                                        .Bold()
                                        .CornerRadius(6)
                                        .Padding(10, 7)
                                        .ToolTip($"Open direct message with @{capturedName}")
                                        .OnClick(() => OpenDirectMessageChat(capturedId, capturedName))
                                        .Column(0),
                                    new Button()
                                        .Content("✕")
                                        .Background(ColButtonGray)
                                        .Foreground(ColTextHeader)
                                        .FontSize(11)
                                        .CornerRadius(6)
                                        .Padding(7, 6)
                                        .ToolTip($"Remove {capturedName} from friends")
                                        .OnClick(() => OpenConfirmModal(
                                            "Remove Friend?",
                                            $"Are you sure you want to remove '{capturedName}' from your friends?",
                                            "Remove",
                                            ColRed,
                                            () =>
                                            {
                                                CloseGroupModal();
                                                _ = _friendService.RemoveFriendAsync(capturedId);
                                            }))
                                        .Column(1)
                                )
                        );
                    }
                }
            }
        }
    }

    // =========================================================================
    // ACTIONS UTILISATEUR & COMMANDES P2P
    // =========================================================================
}
