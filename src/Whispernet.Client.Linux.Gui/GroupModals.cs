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
    private Element BuildMainWorkspaceGrid()
    {
        var columnsGrid = new Grid()
            .Columns("72,256,*,272")
            .Rows("*")
            .Children(
                BuildServerRailColumn().Column(0),
                BuildChannelSidebarColumn().Column(1),
                BuildMainChatColumn().Column(2),
                BuildRightMembersColumn().Column(3)
            );

        _groupModalCardHost = new TransitionContentControl()
            .Transition(ContentTransition.CreateScale(220));

        _groupModalOverlay = new Border()
            .Background(Color.FromHex("#000000").WithAlpha(175))
            .Child(
                new Border()
                    .Width(440)
                    .Background(ColSidebar)
                    .BorderBrush(ColDivider)
                    .BorderThickness(1)
                    .Padding(22)
                    .Child(_groupModalCardHost)
                    .Center()
            );
        _groupModalOverlay.IsVisible = false;

        return new Grid()
            .Columns("*")
            .Rows("*")
            .Children(columnsGrid, _groupModalOverlay);
    }

    private void OpenGroupModal()
    {
        _groupModalMode = GroupModalMode.Choice;
        _groupModalAnimProgress = 1.0;
        _modalCreateChannels.Clear();
        _modalCreateChannels.Add(ChannelId.DefaultGeneralName);
        if (_groupModalOverlay is not null)
        {
            _groupModalOverlay.IsVisible = true;
        }
        RefreshGroupModalContent();
    }

    private void CloseGroupModal()
    {
        _groupModalMode = GroupModalMode.Closed;
        if (_groupModalOverlay is not null)
        {
            _groupModalOverlay.IsVisible = false;
        }
    }

    private void OpenMemberProfileModal(
        AuthorId memberId,
        string displayName,
        MemberStatus status,
        bool isOnline,
        NodeId? nodeId,
        bool canModerate,
        bool isSelf)
    {
        if (_groupModalCardHost is null || _groupModalOverlay is null)
        {
            return;
        }

        string irohTicketOrNode = isSelf && _transport is not null && !string.IsNullOrWhiteSpace(_transport.SelfTicket)
            ? _transport.SelfTicket
            : (nodeId is NodeId nid && !string.IsNullOrWhiteSpace(nid.Value)
                ? nid.Value
                : "Not yet seen on mesh");

        var body = new StackPanel()
            .Vertical()
            .Spacing(10)
            .Children(
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(
                        new Label().Text(isOnline ? "●" : "○").FontSize(16).Foreground(isOnline ? ColGreen : ColTextMuted),
                        new Label().Text(displayName).FontSize(18).Bold().Foreground(ColTextHeader),
                        new Label().Text($"[{status}]").FontSize(12).Bold().Foreground(status switch
                        {
                            MemberStatus.Admitted => ColGreen,
                            MemberStatus.Silenced => ColAmber,
                            MemberStatus.Banned => ColRed,
                            _ => ColTextMuted
                        }).CenterVertical()
                    ),
                new Label().Text(isOnline ? "Online (seen < 15m)" : "Offline").FontSize(12).Foreground(isOnline ? ColGreen : ColTextMuted),
                new Label().Text("AUTHOR ID").FontSize(10).Bold().Foreground(ColTextMuted),
                new Border()
                    .Background(ColCardBg)
                    .CornerRadius(6)
                    .Padding(8, 6)
                    .Child(new Label().Text(memberId.Value).FontSize(11).Foreground(ColTextBody).TextWrapping(TextWrapping.Wrap)),
                new Label().Text(isSelf ? "CURRENT IROH TICKET" : "CURRENT IROH NODE / TICKET").FontSize(10).Bold().Foreground(ColTextMuted),
                new Border()
                    .Background(ColCardBg)
                    .CornerRadius(6)
                    .Padding(8, 6)
                    .Child(new Label().Text(irohTicketOrNode).FontSize(11).Foreground(ColTextBody).TextWrapping(TextWrapping.Wrap))
            );

        var actionsRow = new StackPanel().Horizontal().Spacing(8);
        actionsRow.Add(
            new Button()
                .Content("Copy AuthorId")
                .Background(ColButtonGray)
                .Foreground(ColTextHeader)
                .FontSize(11)
                .CornerRadius(5)
                .Padding(10, 5)
                .OnClick(() =>
                {
                    TryCopyToClipboard(memberId.Value);
                    SetStatus($"Copied AuthorId for {displayName}.");
                })
        );
        actionsRow.Add(
            new Button()
                .Content("Copy Iroh")
                .Background(ColButtonGray)
                .Foreground(ColTextHeader)
                .FontSize(11)
                .CornerRadius(5)
                .Padding(10, 5)
                .OnClick(() =>
                {
                    TryCopyToClipboard(irohTicketOrNode);
                    SetStatus($"Copied Iroh identifier for {displayName}.");
                })
        );
        if(!isSelf){
            actionsRow.Add(
                new Button()
                    .Content("Message")
                    .Background(ColBlurple)
                    .Foreground(ColTextHeader)
                    .FontSize(11)
                    .Bold()
                    .CornerRadius(5)
                    .Padding(10, 5)
                    .OnClick(() =>
                    {
                        lock (_stateLock)
                        {
                            _activeDmPeerId = memberId;
                            _activeTab = GuiChannelTab.DirectMessage;
                        }
                        CloseGroupModal();
                        _uiDirty = true;
                    })
            );

            FriendStatus fStatus = _friendService.GetStatus(memberId);
            if (fStatus == FriendStatus.Accepted)
            {
                actionsRow.Add(
                    new Button()
                        .Content("Remove Friend")
                        .Background(ColRed)
                        .Foreground(ColTextHeader)
                        .FontSize(11)
                        .CornerRadius(5)
                        .Padding(10, 5)
                        .OnClick(() => OpenConfirmModal(
                            "Remove Friend?",
                            $"Remove '{displayName}' from your friends?",
                            "Remove",
                            ColRed,
                            () =>
                            {
                                CloseGroupModal();
                                _ = _friendService.RemoveFriendAsync(memberId);
                            }))
                );
            }
            else if (fStatus == FriendStatus.PendingIncoming)
            {
                actionsRow.Add(
                    new Button()
                        .Content("Accept Friend")
                        .Background(ColGreen)
                        .Foreground(ColTextHeader)
                        .FontSize(11)
                        .Bold()
                        .CornerRadius(5)
                        .Padding(10, 5)
                        .OnClick(() =>
                        {
                            CloseGroupModal();
                            _ = _friendService.AcceptFriendRequestAsync(memberId);
                        })
                );
            }
            else if (fStatus == FriendStatus.PendingOutgoing)
            {
                actionsRow.Add(
                    new Button()
                        .Content("Request Sent")
                        .Background(ColButtonGray)
                        .Foreground(ColTextMuted)
                        .FontSize(11)
                        .CornerRadius(5)
                        .Padding(10, 5)
                        .IsEnabled(false)
                );
            }
            else
            {
                actionsRow.Add(
                    new Button()
                        .Content("Add Friend")
                        .Background(ColGreen)
                        .Foreground(ColTextHeader)
                        .FontSize(11)
                        .Bold()
                        .CornerRadius(5)
                        .Padding(10, 5)
                        .OnClick(() =>
                        {
                            string? targetSpki = null;
                            lock (_stateLock)
                            {
                                if (_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? gov) &&
                                    gov.MemberStates.TryGetValue(memberId, out ModerationAction? act) &&
                                    !string.IsNullOrWhiteSpace(act.TargetPublicKeySpkiBase64))
                                {
                                    targetSpki = act.TargetPublicKeySpkiBase64;
                                }
                            }
                            if (!string.IsNullOrWhiteSpace(targetSpki))
                            {
                                _ = _friendService.SendFriendRequestAsync(memberId, targetSpki, displayName, nodeId);
                                SetStatus($"Sent friend request to {displayName}.");
                            }
                            else
                            {
                                SetStatus($"Missing public key for {displayName}. Use a whispernet://friend/... link instead.");
                            }
                            CloseGroupModal();
                        })
                );
            }
        }
        body.Add(actionsRow);

        if(canModerate && !isSelf){
            string targetHex = memberId.Value;
            body.Add(new Label().Text("MODERATOR ACTIONS").FontSize(10).Bold().Foreground(ColAmber));
            body.Add(
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(
                        new Button()
                            .Content("Admit")
                            .Background(ColGreen)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(5)
                            .Padding(12, 6)
                            .OnClick(() =>
                            {
                                CloseGroupModal();
                                _ = ExecuteSlashCommandAsync($"mod admit {targetHex}");
                            }),
                        new Button()
                            .Content("Silence")
                            .Background(ColAmber)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(5)
                            .Padding(12, 6)
                            .OnClick(() =>
                            {
                                CloseGroupModal();
                                _ = ExecuteSlashCommandAsync($"mod silence {targetHex}");
                            }),
                        new Button()
                            .Content("Ban")
                            .Background(ColRed)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(5)
                            .Padding(12, 6)
                            .OnClick(() =>
                            {
                                CloseGroupModal();
                                _ = ExecuteSlashCommandAsync($"mod ban {targetHex}");
                            })
                    )
            );
        }

        body.Add(
            new Button()
                .Content("Close")
                .Background(ColButtonGray)
                .Foreground(ColTextHeader)
                .FontSize(12)
                .CornerRadius(6)
                .Padding(14, 6)
                .OnClick(CloseGroupModal)
        );

        _groupModalMode = GroupModalMode.Choice;
        _groupModalOverlay.IsVisible = true;
        _groupModalCardHost.Content(body);
    }

    private void OpenAddFriendModal()
    {
        if (_groupModalCardHost is null || _groupModalOverlay is null)
        {
            return;
        }

        string myLink = _activeSession is not null && _transport is not null
            ? _friendService.CreateFriendLink(_activeSession, _transport.SelfId)
            : "Unlock account & start mesh to generate your friend link.";

        var linkBox = new TextBox()
            .Placeholder("Paste whispernet://friend/... link")
            .Background(ColServerRail)
            .Foreground(ColTextHeader)
            .FontSize(12);

        var errLabel = new Label()
            .Text(string.Empty)
            .FontSize(12)
            .Foreground(ColRed)
            .TextWrapping(TextWrapping.Wrap);

        var body = new StackPanel()
            .Vertical()
            .Spacing(10)
            .Children(
                new Label().Text("Add Friend").FontSize(18).Bold().Foreground(ColTextHeader),
                new Label().Text("YOUR FRIEND LINK").FontSize(10).Bold().Foreground(ColTextMuted),
                new Border()
                    .Background(ColCardBg)
                    .CornerRadius(6)
                    .Padding(8, 6)
                    .Child(new Label().Text(myLink).FontSize(11).Foreground(ColTextBody).TextWrapping(TextWrapping.Wrap)),
                new Button()
                    .Content("Copy My Friend Link")
                    .Background(ColButtonGray)
                    .Foreground(ColTextHeader)
                    .FontSize(11)
                    .CornerRadius(5)
                    .Padding(10, 5)
                    .OnClick(() =>
                    {
                        TryCopyToClipboard(myLink);
                        SetStatus("Copied your whispernet://friend/... link to clipboard.");
                    }),
                new Label().Text("SEND FRIEND REQUEST BY LINK").FontSize(10).Bold().Foreground(ColTextMuted),
                linkBox,
                errLabel,
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(
                        new Button()
                            .Content("Send Request")
                            .Background(ColBlurple)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(async () =>
                            {
                                try
                                {
                                    FriendInvitationData data = FriendService.ParseFriendLink(linkBox.Text ?? string.Empty);
                                    await _friendService.SendFriendRequestAsync(
                                        data.AuthorId,
                                        data.PublicKeySpkiBase64,
                                        data.DisplayName,
                                        data.BootstrapNodeId).ConfigureAwait(false);
                                    SetStatus($"Sent friend request to {data.DisplayName}.");
                                    CloseGroupModal();
                                }
                                catch (Exception ex)
                                {
                                    errLabel.Text(ex.Message);
                                }
                            }),
                        new Button()
                            .Content("Close")
                            .Background(ColButtonGray)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(CloseGroupModal)
                    )
            );

        _groupModalMode = GroupModalMode.Choice;
        _groupModalOverlay.IsVisible = true;
        _groupModalCardHost.Content(body);
    }

    private void OpenGroupInfoModal(GroupGovernanceState gov){
        if (_groupModalCardHost is null || _groupModalOverlay is null)
            return;

        GroupManifest m = gov.CurrentManifest;
        IReadOnlyList<GroupChannel> channels = m.GetEffectiveChannels();
        string channelsText = string.Join(", ", channels.Select(static c => $"#{c.Name}"));

        var body = new StackPanel()
            .Vertical()
            .Spacing(10)
            .Children(
                new Label().Text($"Info — {m.Title}").FontSize(18).Bold().Foreground(ColTextHeader),
                new Label().Text("GROUP ID").FontSize(10).Bold().Foreground(ColTextMuted),
                new Border()
                    .Background(ColCardBg)
                    .Padding(8, 6)
                    .Child(new Label().Text(m.GroupId.Value).FontSize(11).Foreground(ColTextBody).TextWrapping(TextWrapping.Wrap)),
                new Label().Text($"Manifest Version: v{m.Version} · Members: {gov.MemberStates.Count} · Terminated: {m.IsTerminated}").FontSize(12).Foreground(ColTextBody),
                new Label().Text($"Password Protected: {(m.IsPasswordProtected ? "Yes" : "No")}").FontSize(12).Foreground(ColTextBody),
                new Label().Text($"Channels ({channels.Count}): {channelsText}").FontSize(12).Foreground(ColTextBody).TextWrapping(TextWrapping.Wrap),
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(
                        new Button()
                            .Content("Copy GroupId")
                            .Background(ColBlurple)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(() =>
                            {
                                TryCopyToClipboard(m.GroupId.Value);
                                SetStatus($"Copied GroupId for '{m.Title}'.");
                            }),
                        new Button()
                            .Content("Close")
                            .Background(ColButtonGray)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(CloseGroupModal)
                    )
            );

        _groupModalMode = GroupModalMode.Choice;
        _groupModalOverlay.IsVisible = true;
        _groupModalCardHost.Content(body);
    }

    private void OpenGroupConfigModal(GroupGovernanceState gov)
    {
        if (_groupModalCardHost is null || _groupModalOverlay is null)
        {
            return;
        }

        var titleBox = new TextBox()
            .Text(gov.CurrentManifest.Title)
            .Background(ColServerRail)
            .Foreground(ColTextHeader)
            .FontSize(13);

        var passBox = new PasswordBox()
            .Placeholder("New join password (leave blank to keep unchanged)")
            .Background(ColServerRail)
            .Foreground(ColTextHeader)
            .FontSize(13);

        var newChannelBox = new TextBox()
            .Placeholder("new-channel-name")
            .Background(ColServerRail)
            .Foreground(ColTextHeader)
            .FontSize(13);

        var errLabel = new Label()
            .Text(string.Empty)
            .FontSize(12)
            .Foreground(ColRed)
            .TextWrapping(TextWrapping.Wrap);

        List<GroupChannel> workingChannels = gov.CurrentManifest.GetEffectiveChannels().ToList();
        var channelsListStack = new StackPanel()
            .Vertical()
            .Spacing(4);

        void RebuildChannelsList()
        {
            channelsListStack.Clear();
            bool canRemove = workingChannels.Count > 1;
            foreach (GroupChannel ch in workingChannels)
            {
                GroupChannel capturedCh = ch;
                var row = new Grid()
                    .Columns("*,Auto")
                    .Children(
                        new Label()
                            .Text($"#{ch.Name}")
                            .FontSize(12)
                            .Foreground(ColTextBody)
                            .CenterVertical()
                            .Column(0),
                        new Button()
                            .Content("✕")
                            .Background(canRemove ? ColRed : ColButtonGray)
                            .Foreground(ColTextHeader)
                            .FontSize(11)
                            .CornerRadius(4)
                            .Padding(7, 2)
                            .IsEnabled(canRemove)
                            .OnClick(() =>
                            {
                                if (workingChannels.Count <= 1)
                                {
                                    errLabel.Text("Cannot remove the last remaining channel.");
                                    return;
                                }
                                workingChannels.RemoveAll(c => c.ChannelId == capturedCh.ChannelId);
                                errLabel.Text(string.Empty);
                                RebuildChannelsList();
                            })
                            .Column(1)
                    );
                channelsListStack.Add(row);
            }
        }

        RebuildChannelsList();

        var addChannelRow = new Grid()
            .Columns("*,Auto")
            .Spacing(8)
            .Children(
                newChannelBox.Column(0),
                new Button()
                    .Content("Add Channel")
                    .Background(ColButtonGray)
                    .Foreground(ColTextHeader)
                    .FontSize(11)
                    .Bold()
                    .CornerRadius(5)
                    .Padding(10, 5)
                    .OnClick(() =>
                    {
                        string raw = newChannelBox.Text ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            errLabel.Text("Enter a channel name to add.");
                            return;
                        }
                        try
                        {
                            string normalized = ChannelId.NormalizeName(raw);
                            ChannelId channelId = ChannelId.FromGroupAndName(gov.GroupId, normalized);
                            if (workingChannels.Any(c => c.ChannelId == channelId || c.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                            {
                                errLabel.Text($"Channel '#{normalized}' already exists.");
                                return;
                            }
                            workingChannels.Add(new GroupChannel(channelId, normalized, DateTimeOffset.UtcNow));
                            newChannelBox.Text(string.Empty);
                            errLabel.Text(string.Empty);
                            RebuildChannelsList();
                        }
                        catch (Exception ex)
                        {
                            errLabel.Text(ex.Message);
                        }
                    })
                    .Column(1)
            );

        var body = new StackPanel()
            .Vertical()
            .Spacing(10)
            .Children(
                new Label().Text($"Configuration — {gov.CurrentManifest.Title}").FontSize(18).Bold().Foreground(ColTextHeader),
                new Label().Text("TITLE").FontSize(10).Bold().Foreground(ColTextMuted),
                titleBox,
                new Label().Text("NEW JOIN PASSWORD").FontSize(10).Bold().Foreground(ColTextMuted),
                passBox,
                new Label().Text("CHANNELS").FontSize(10).Bold().Foreground(ColTextMuted),
                new Border()
                    .Background(ColServerRail)
                    .CornerRadius(6)
                    .Padding(8, 6)
                    .Child(channelsListStack),
                addChannelRow,
                errLabel,
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(
                        new Button()
                            .Content("Sign & publish")
                            .Background(ColBlurple)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(() => _ = SaveGroupConfigAsync(gov, titleBox.Text, passBox.Password, workingChannels.ToList(), errLabel)),
                        new Button()
                            .Content("Cancel")
                            .Background(ColButtonGray)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(CloseGroupModal),
                        new Button()
                            .Content("Terminate")
                            .Background(ColRed)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(() => OpenConfirmModal(
                                "Terminate Group?",
                                $"Are you sure you want to permanently terminate '{gov.CurrentManifest.Title}'? This action cannot be undone.",
                                "Terminate",
                                ColRed,
                                () =>
                                {
                                    SwitchActiveGroup(gov.GroupId.Value, gov.CurrentManifest.Title);
                                    CloseGroupModal();
                                    _ = ExecuteSlashCommandAsync("group terminate");
                                },
                                () => OpenGroupConfigModal(gov)))
                    )
            );

        _groupModalMode = GroupModalMode.Choice;
        _groupModalOverlay.IsVisible = true;
        _groupModalCardHost.Content(body);
    }

    private void OpenConfirmModal(
        string heading,
        string message,
        string confirmText,
        Color confirmBg,
        Action onConfirm,
        Action? onCancel = null)
    {
        if (_groupModalCardHost is null || _groupModalOverlay is null)
        {
            return;
        }

        var body = new StackPanel()
            .Vertical()
            .Spacing(12)
            .Children(
                new Label().Text(heading).FontSize(18).Bold().Foreground(ColTextHeader),
                new Label().Text(message).FontSize(13).Foreground(ColTextBody).TextWrapping(TextWrapping.Wrap),
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(
                        new Button()
                            .Content(confirmText)
                            .Background(confirmBg)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(onConfirm),
                        new Button()
                            .Content("Cancel")
                            .Background(ColButtonGray)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(onCancel ?? CloseGroupModal)
                    )
            );

        _groupModalMode = GroupModalMode.Choice;
        _groupModalOverlay.IsVisible = true;
        _groupModalCardHost.Content(body);
    }

    private void OpenLeaveGroupConfirmModal(GroupGovernanceState gov)
    {
        OpenConfirmModal(
            "Leave Group?",
            $"Are you sure you want to leave '{gov.CurrentManifest.Title}'? It will be removed from your local vault.",
            "Leave",
            ColRed,
            () =>
            {
                LeaveGroup(gov);
                CloseGroupModal();
            });
    }

    private async Task SaveGroupConfigAsync(
        GroupGovernanceState gov,
        string? newTitle,
        string? newPassword,
        IReadOnlyList<GroupChannel> newChannels,
        Label errLabel)
    {
        if (_activeSession is null)
        {
            errLabel.Text("Unlock an account first.");
            return;
        }
        if (newChannels.Count == 0)
        {
            errLabel.Text("Group must have at least one channel.");
            return;
        }

        try
        {
            string? titleToSet = string.IsNullOrWhiteSpace(newTitle) ? null : newTitle.Trim();
            string? pwdToSet = string.IsNullOrWhiteSpace(newPassword) ? null : newPassword;
            await _govService.UpdateGroupManifestAsync(
                _activeSession,
                gov,
                newTitle: titleToSet,
                newJoinPassword: pwdToSet,
                newChannels: newChannels).ConfigureAwait(false);

            RegisterGovernedGroup(gov);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(gov);
                await _directMesh.BroadcastSyncPayloadAsync(gov.GroupId).ConfigureAwait(false);
            }
            SetStatus($"Updated group config for '{gov.CurrentManifest.Title}' (v{gov.CurrentManifest.Version}).");
            CloseGroupModal();
        }
        catch (Exception ex)
        {
            errLabel.Text(ex.Message);
        }
    }

    private void LeaveGroup(GroupGovernanceState gov)
    {
        string gid = gov.GroupId.Value;
        string title = gov.CurrentManifest.Title;
        if (_activeSession is not null)
        {
            _govService.RemoveGroupFromVault(_activeSession, gov.GroupId);
        }

        lock (_stateLock)
        {
            _governedGroups.Remove(gid);
            _groupTitles.Remove(gid);
            if (_activeGroupId.Equals(gid, StringComparison.Ordinal))
            {
                GroupGovernanceState? next = _governedGroups.Values.FirstOrDefault();
                if (next is not null)
                {
                    _activeGroupId = next.GroupId.Value;
                    _activeGroupTitle = next.CurrentManifest.Title;
                    GroupChannel ch = next.CurrentManifest.GetEffectiveChannels()[0];
                    _activeChannelId = ch.ChannelId.Value;
                    _activeChannelName = ch.Name;
                }
                else
                {
                    _activeGroupId = "general";
                    _activeGroupTitle = "General Mesh";
                    _activeChannelId = "general";
                    _activeChannelName = "general";
                }
            }
        }

        SetStatus($"Left group '{title}'.");
        AddLog(GuiLogTheme.Group, $"Left group '{title}' (#{ShortId(gid)}).");
        _uiDirty = true;
    }

    private void RefreshGroupModalContent(string? errorText = null)
    {
        if (_groupModalCardHost is null || _groupModalMode == GroupModalMode.Closed)
        {
            return;
        }

        _modalErrorLabel = new Label()
            .Text(errorText ?? string.Empty)
            .FontSize(12)
            .Foreground(ColRed)
            .TextWrapping(TextWrapping.Wrap);

        if (_groupModalMode == GroupModalMode.Choice)
        {
            _modalCreateChoiceCard = new Border()
                .Background(ColCardBg)
                .BorderBrush(ColDivider)
                .BorderThickness(1)
                .CornerRadius(8)
                .Padding(14, 12)
                .Child(
                    new StackPanel()
                        .Vertical()
                        .Spacing(6)
                        .Children(
                            new Label().Text("Create a Group").FontSize(15).Bold().Foreground(ColTextHeader),
                            new Label().Text("Start a new governed group with #general and custom channels.").FontSize(12).Foreground(ColTextMuted).TextWrapping(TextWrapping.Wrap),
                            new Button()
                                .Content("Create Group →")
                                .Background(ColBlurple)
                                .Foreground(ColTextHeader)
                                .FontSize(12)
                                .Bold()
                                .CornerRadius(6)
                                .Padding(12, 6)
                                .OnClick(() =>
                                {
                                    _groupModalAnimProgress = 1.0;
                                    _groupModalMode = GroupModalMode.AnimatingToCreate;
                                })
                        )
                );

            _modalJoinChoiceCard = new Border()
                .Background(ColCardBg)
                .BorderBrush(ColDivider)
                .BorderThickness(1)
                .CornerRadius(8)
                .Padding(14, 12)
                .Child(
                    new StackPanel()
                        .Vertical()
                        .Spacing(6)
                        .Children(
                            new Label().Text("Join a Group").FontSize(15).Bold().Foreground(ColTextHeader),
                            new Label().Text("Paste a whispernet://invite/... link from a group moderator.").FontSize(12).Foreground(ColTextMuted).TextWrapping(TextWrapping.Wrap),
                            new Button()
                                .Content("Join via Invite →")
                                .Background(ColGreen)
                                .Foreground(ColTextHeader)
                                .FontSize(12)
                                .Bold()
                                .CornerRadius(6)
                                .Padding(12, 6)
                                .OnClick(() =>
                                {
                                    _groupModalAnimProgress = 1.0;
                                    _groupModalMode = GroupModalMode.AnimatingToJoin;
                                })
                        )
                );

            _groupModalCardHost.Content(
                new StackPanel()
                    .Vertical()
                    .Spacing(12)
                    .Children(
                        new Label().Text("Add a Group").FontSize(18).Bold().Foreground(ColTextHeader),
                        _modalCreateChoiceCard,
                        _modalJoinChoiceCard,
                        new Button()
                            .Content("Cancel")
                            .Background(ColButtonGray)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .CornerRadius(6)
                            .Padding(12, 6)
                            .OnClick(CloseGroupModal)
                    )
            );
            return;
        }

        if (_groupModalMode == GroupModalMode.CreateForm)
        {
            string prevTitle = _modalGroupTitleInput?.Text ?? string.Empty;
            string prevPwd = _modalGroupPasswordInput?.Password ?? string.Empty;

            _modalGroupTitleInput = new TextBox()
                .Text(prevTitle)
                .Placeholder("Group title (e.g. Whispernet Devs)")
                .Background(ColServerRail)
                .Foreground(ColTextHeader)
                .FontSize(13);

            _modalGroupPasswordInput = new PasswordBox()
                .Placeholder("Join password (optional)")
                .Background(ColServerRail)
                .Foreground(ColTextHeader)
                .FontSize(13);
            if (!string.IsNullOrEmpty(prevPwd))
            {
                _modalGroupPasswordInput.Password = prevPwd;
            }

            _modalNewChannelInput = new TextBox()
                .Placeholder("Add channel (e.g. announcements)")
                .Background(ColServerRail)
                .Foreground(ColTextHeader)
                .FontSize(12)
                .Width(280);

            var channelsStack = new StackPanel().Vertical().Spacing(5);
            for (int i = 0; i < _modalCreateChannels.Count; i++)
            {
                int idx = i;
                string chName = _modalCreateChannels[i];
                var row = new DockPanel()
                    .Spacing(8)
                    .Children(
                        new Button()
                            .Content("-")
                            .Background(ColRed)
                            .Foreground(ColTextHeader)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(4)
                            .Padding(10, 3)
                            .DockRight()
                            .OnClick(() =>
                            {
                                if (_modalCreateChannels.Count > 1 && idx < _modalCreateChannels.Count)
                                {
                                    _modalCreateChannels.RemoveAt(idx);
                                    RefreshGroupModalContent();
                                }
                            }),
                        new Border()
                            .Background(ColCardBg)
                            .CornerRadius(5)
                            .Padding(10, 5)
                            .Child(new Label().Text($"# {chName}").FontSize(12).Bold().Foreground(ColTextHeader))
                    );
                channelsStack.Add(row);
            }

            _groupModalCardHost.Content(
                new StackPanel()
                    .Vertical()
                    .Spacing(10)
                    .Children(
                        new Label().Text("Create a Governed Group").FontSize(18).Bold().Foreground(ColTextHeader),
                        new Label().Text("TITLE").FontSize(10).Bold().Foreground(ColTextMuted),
                        _modalGroupTitleInput,
                        new Label().Text("PASSWORD (OPTIONAL)").FontSize(10).Bold().Foreground(ColTextMuted),
                        _modalGroupPasswordInput,
                        new Label().Text("CHANNELS").FontSize(10).Bold().Foreground(ColTextMuted),
                        channelsStack,
                        new StackPanel()
                            .Horizontal()
                            .Spacing(6)
                            .Children(
                                _modalNewChannelInput,
                                new Button()
                                    .Content("+")
                                    .Background(ColGreen)
                                    .Foreground(ColTextHeader)
                                    .FontSize(14)
                                    .Bold()
                                    .CornerRadius(5)
                                    .Padding(12, 4)
                                    .OnClick(() =>
                                    {
                                        string raw = _modalNewChannelInput?.Text?.Trim() ?? string.Empty;
                                        if (string.IsNullOrWhiteSpace(raw))
                                        {
                                            return;
                                        }
                                        try
                                        {
                                            string norm = ChannelId.NormalizeName(raw);
                                            if (!_modalCreateChannels.Contains(norm, StringComparer.OrdinalIgnoreCase))
                                            {
                                                _modalCreateChannels.Add(norm);
                                            }
                                            RefreshGroupModalContent();
                                        }
                                        catch (Exception ex)
                                        {
                                            RefreshGroupModalContent(ex.Message);
                                        }
                                    })
                            ),
                        _modalErrorLabel,
                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button()
                                    .Content("Create Group")
                                    .Background(ColBlurple)
                                    .Foreground(ColTextHeader)
                                    .FontSize(13)
                                    .Bold()
                                    .CornerRadius(6)
                                    .Padding(14, 7)
                                    .OnClick(() => _ = SubmitModalCreateGroupAsync()),
                                new Button()
                                    .Content("Back")
                                    .Background(ColButtonGray)
                                    .Foreground(ColTextHeader)
                                    .FontSize(12)
                                    .CornerRadius(6)
                                    .Padding(12, 7)
                                    .OnClick(OpenGroupModal),
                                new Button()
                                    .Content("Cancel")
                                    .Background(ColCardBg)
                                    .Foreground(ColTextMuted)
                                    .FontSize(12)
                                    .CornerRadius(6)
                                    .Padding(12, 7)
                                    .OnClick(CloseGroupModal)
                            )
                    )
            );
            return;
        }

        if (_groupModalMode == GroupModalMode.JoinForm)
        {
            _modalJoinLinkInput = new TextBox()
                .Placeholder("whispernet://invite/...")
                .Background(ColServerRail)
                .Foreground(ColTextHeader)
                .FontSize(13);

            _modalJoinPasswordInput = new PasswordBox()
                .Placeholder("Group password (if required)")
                .Background(ColServerRail)
                .Foreground(ColTextHeader)
                .FontSize(13);

            _groupModalCardHost.Content(
                new StackPanel()
                    .Vertical()
                    .Spacing(10)
                    .Children(
                        new Label().Text("Join a Governed Group").FontSize(18).Bold().Foreground(ColTextHeader),
                        new Label().Text("INVITE LINK").FontSize(10).Bold().Foreground(ColTextMuted),
                        _modalJoinLinkInput,
                        new Label().Text("PASSWORD (OPTIONAL)").FontSize(10).Bold().Foreground(ColTextMuted),
                        _modalJoinPasswordInput,
                        _modalErrorLabel,
                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button()
                                    .Content("Join Group")
                                    .Background(ColGreen)
                                    .Foreground(ColTextHeader)
                                    .FontSize(13)
                                    .Bold()
                                    .CornerRadius(6)
                                    .Padding(14, 7)
                                    .OnClick(() => _ = SubmitModalJoinGroupAsync()),
                                new Button()
                                    .Content("Back")
                                    .Background(ColButtonGray)
                                    .Foreground(ColTextHeader)
                                    .FontSize(12)
                                    .CornerRadius(6)
                                    .Padding(12, 7)
                                    .OnClick(OpenGroupModal),
                                new Button()
                                    .Content("Cancel")
                                    .Background(ColCardBg)
                                    .Foreground(ColTextMuted)
                                    .FontSize(12)
                                    .CornerRadius(6)
                                    .Padding(12, 7)
                                    .OnClick(CloseGroupModal)
                            )
                    )
            );
        }
    }

    private async Task SubmitModalCreateGroupAsync()
    {
        if (_activeSession is null)
        {
            RefreshGroupModalContent("Unlock an account first.");
            return;
        }

        string title = _modalGroupTitleInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            RefreshGroupModalContent("Please enter a group title.");
            return;
        }

        string? pwd = string.IsNullOrWhiteSpace(_modalGroupPasswordInput?.Password)
            ? null
            : _modalGroupPasswordInput.Password;

        try
        {
            GroupGovernanceState govState = await _govService.CreateGroupAsync(
                _activeSession, title, pwd, creatorNodeId: _transport?.SelfId).ConfigureAwait(false);

            foreach (string chName in _modalCreateChannels)
            {
                if (!chName.Equals(ChannelId.DefaultGeneralName, StringComparison.OrdinalIgnoreCase))
                {
                    await _govService.CreateChannelAsync(_activeSession, govState, chName).ConfigureAwait(false);
                }
            }

            RegisterGovernedGroup(govState);
            SetStatus($"Created governed group '{title}' with {_modalCreateChannels.Count} channel(s).");
            AddLog(GuiLogTheme.Group, $"Created governed group '{title}' ({govState.GroupId}) with channels: {string.Join(", ", _modalCreateChannels.Select(static c => $"#{c}"))}");
            CloseGroupModal();
            _uiDirty = true;
        }
        catch (Exception ex)
        {
            RefreshGroupModalContent(ex.Message);
        }
    }

    private async Task SubmitModalJoinGroupAsync()
    {
        string link = _modalJoinLinkInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(link))
        {
            RefreshGroupModalContent("Paste a whispernet://invite/... link.");
            return;
        }

        string pass = _modalJoinPasswordInput?.Password ?? string.Empty;
        string cmd = string.IsNullOrWhiteSpace(pass)
            ? $"group join {link}"
            : $"group join {link} {pass}";
        CloseGroupModal();
        await ExecuteSlashCommandAsync(cmd).ConfigureAwait(false);
    }

    // 1. COLONNE GAUCHE (72px) — RAIL DES SERVEURS / GROUPES
}
