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
    private Element BuildChannelSidebarColumn()
    {
        _serverHeaderTitleLabel = new Label()
            .Text(_activeGroupTitle)
            .FontSize(15)
            .Bold()
            .Foreground(ColTextHeader);

        _serverHeaderRoleLabel = new Label()
            .Text(string.Empty)
            .FontSize(11)
            .Bold()
            .Foreground(ColBlurple);
        _serverHeaderRoleLabel.IsVisible = false;

        var topServerHeader = new Border()
            .Background(ColSidebar)
            .BorderBrush(ColDivider)
            .BorderThickness(1)
            .Padding(14, 10)
            .Child(
                new StackPanel()
                    .Vertical()
                    .Spacing(2)
                    .Children(_serverHeaderTitleLabel, _serverHeaderRoleLabel)
            )
            .DockTop();

        // Barre utilisateur inférieure (#232428)
        _userBarNameLabel = new Label()
            .Text("Locked")
            .FontSize(13)
            .Bold()
            .Foreground(ColTextHeader);

        _userBarSubLabel = new Label()
            .Text("No active account")
            .FontSize(11)
            .Foreground(ColTextMuted);

        _meshStatusLabel = new Label()
            .Text("● Mesh Offline")
            .FontSize(11)
            .Bold()
            .Foreground(ColTextMuted);

        _meshToggleBtn = new Button()
            .Content("Stop Mesh")
            .Background(ColRed)
            .Foreground(ColTextHeader)
            .FontSize(11)
            .Bold()
            .CornerRadius(6)
            .Padding(8, 4)
            .OnClick(() => _ = ToggleMeshAsync());

        var switchAccBtn = new Button()
            .Content("Switch Account")
            .Background(ColButtonGray)
            .Foreground(ColTextHeader)
            .FontSize(11)
            .CornerRadius(6)
            .Padding(8, 4)
            .OnClick(ReturnToLoginScreen);

        var bottomUserBar = new Border()
            .Background(ColUserBar)
            .BorderBrush(ColDivider)
            .BorderThickness(1)
            .Padding(10, 8)
            .Child(
                new StackPanel()
                    .Vertical()
                    .Spacing(6)
                    .Children(
                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Border()
                                    .Size(34, 34)
                                    .CornerRadius(17)
                                    .Background(ColBlurple)
                                    .Child(
                                        new Label()
                                            .Text("ID")
                                            .FontSize(12)
                                            .Bold()
                                            .Foreground(ColTextHeader)
                                            .Center()
                                    ),
                                new StackPanel()
                                    .Vertical()
                                    .Spacing(1)
                                    .Children(_userBarNameLabel, _userBarSubLabel)
                            ),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(6)
                            .Children(_meshStatusLabel.CenterVertical(), _meshToggleBtn, switchAccBtn)
                    )
            )
            .DockBottom();

        // Liste des salons textuels
        _textChannelsListStack = new StackPanel().Vertical().StretchHorizontal().Spacing(4);
        _dmPeersListStack = new StackPanel().Vertical().StretchHorizontal().Spacing(4);
        _tabChatBtn = CreateChannelTabButton("# general", GuiChannelTab.GeneralChat);
        _tabGovLogBtn = CreateChannelTabButton("# governance-log", GuiChannelTab.GovernanceLog);
        _tabTxLogsBtn = CreateChannelTabButton("# transaction-logs", GuiChannelTab.TransactionLogs);

        _groupChannelsSection = new StackPanel()
            .Vertical()
            .StretchHorizontal()
            .Spacing(10)
            .Children(
                new Label().Text("TEXT CHANNELS").FontSize(11).Bold().Foreground(ColTextMuted),
                _textChannelsListStack,
                new Label().Text("DIAGNOSTICS & LOGS").FontSize(11).Bold().Foreground(ColTextMuted),
                _tabGovLogBtn,
                _tabTxLogsBtn
            );

        _homeDirectMessagesSection = new StackPanel()
            .Vertical()
            .StretchHorizontal()
            .Spacing(10)
            .Children(
                new Label().Text("DIRECT MESSAGES").FontSize(11).Bold().Foreground(ColTextMuted),
                _dmPeersListStack
            );
        _homeDirectMessagesSection.IsVisible = false;

        var middleScroll = new ScrollViewer()
            .AutoVerticalScroll()
            .NoHorizontalScroll()
            .Content(
                new StackPanel()
                    .Vertical()
                    .StretchHorizontal()
                    .Spacing(10)
                    .Padding(10, 12)
                    .Children(
                        _groupChannelsSection,
                        _homeDirectMessagesSection
                    )
            );

        return new Border()
            .Background(ColSidebar)
            .Child(
                new DockPanel()
                    .Children(topServerHeader, bottomUserBar, middleScroll)
            );
    }

    private Button CreateChannelTabButton(string text, GuiChannelTab tab){
        return new Button()
            .Content(text)
            .StretchHorizontal()
            .Background(_activeTab == tab ? ColChannelActive : ColChannelInactive)
            .Foreground(_activeTab == tab ? ColTextHeader : ColTextMuted)
            .FontSize(13)
            .Bold()
            .CornerRadius(6)
            .Padding(10, 7)
            .OnClick(() =>
            {
                _activeTab = tab;
                _uiDirty = true;
            });
    }

    // 3. COLONNE CENTRALE (*) — FLUX DE CHAT & COMPOSITEUR DE MESSAGE

    private void SwitchActiveGroup(string groupId, string title)
    {
        GroupGovernanceState? gov;
        lock (_stateLock)
        {
            _activeGroupId = groupId;
            _activeGroupTitle = title;
            _groupTitles[groupId] = title;
            if (_governedGroups.TryGetValue(groupId, out gov))
            {
                IReadOnlyList<GroupChannel> channels = gov.CurrentManifest.GetEffectiveChannels();
                GroupChannel selected = channels.FirstOrDefault(c => c.ChannelId.Value.Equals(_activeChannelId, StringComparison.OrdinalIgnoreCase))
                    ?? channels[0];
                _activeChannelId = selected.ChannelId.Value;
                _activeChannelName = selected.Name;
            }
            else
            {
                var gid = new GroupId(groupId);
                _activeChannelId = gid.IsValid ? ChannelId.DefaultGeneralFor(gid).Value : groupId;
                _activeChannelName = ChannelId.DefaultGeneralName;
            }
        }
        SetStatus($"Switched to room '{title}' (#{_activeChannelName}).");
        _uiDirty = true;
    }

    private void SwitchActiveChannel(string channelId, string channelName)
    {
        GroupGovernanceState? gov;
        lock (_stateLock)
        {
            _activeChannelId = channelId;
            _activeChannelName = channelName;
            _activeTab = GuiChannelTab.GeneralChat;
            _governedGroups.TryGetValue(_activeGroupId, out gov);
        }
        if (gov is not null)
        {
            TriggerBackgroundGroupSync(gov);
        }
        SetStatus($"Switched to channel #{channelName} ({ShortId(channelId)}).");
        _uiDirty = true;
    }

    private async Task HandleGuiCopyInviteAsync()
    {
        string cmd = _preApproveInvite ? "group invite --preapprove" : "group invite";
        await ExecuteSlashCommandAsync(cmd).ConfigureAwait(false);
    }

    private async Task ToggleMeshAsync()
    {
        if (_transport is not null)
        {
            await ExecuteSlashCommandAsync("mesh stop").ConfigureAwait(false);
        }
        else
        {
            await ExecuteSlashCommandAsync("mesh start").ConfigureAwait(false);
        }
    }

}
