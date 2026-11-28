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
    private Element BuildMainChatColumn()
    {
        _channelTopTitleLabel = new Label()
            .Text("# general-chat")
            .FontSize(16)
            .Bold()
            .Foreground(ColTextHeader)
            .CenterVertical();

        _channelTopTopicLabel = new Label()
            .Text("Causal DAG · Lamport Clock L=0")
            .FontSize(12)
            .Foreground(ColTextMuted)
            .CenterVertical();

        var topChannelBar = new Border()
            .Background(ColChatBg)
            .BorderBrush(ColDivider)
            .BorderThickness(1)
            .Padding(16, 10)
            .Child(
                new DockPanel()
                    .Children(
                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button()
                                    .Content("Sync DAG (0x02)")
                                    .Background(ColButtonGray)
                                    .Foreground(ColTextHeader)
                                    .FontSize(11)
                                    .Bold()
                                    .CornerRadius(5)
                                    .Padding(8, 4)
                                    .OnClick(() => _ = HandleManualDagSyncAsync()),
                                new Button()
                                    .Content("Compact Log")
                                    .Background(ColButtonGray)
                                    .Foreground(ColTextHeader)
                                    .FontSize(11)
                                    .CornerRadius(5)
                                    .Padding(8, 4)
                                    .OnClick(() => _ = ExecuteSlashCommandAsync("mod compact 50")),
                                new Button()
                                    .Content("Terminate Group")
                                    .Background(ColRed)
                                    .Foreground(ColTextHeader)
                                    .FontSize(11)
                                    .Bold()
                                    .CornerRadius(5)
                                    .Padding(8, 4)
                                    .OnClick(() => _ = ExecuteSlashCommandAsync("group terminate"))
                            )
                            .DockRight(),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(12)
                            .Children(_channelTopTitleLabel, _channelTopTopicLabel)
                    )
            )
            .DockTop();

        _statusBannerLabel = new Label()
            .Text(_statusBanner)
            .FontSize(11)
            .Foreground(ColAmber)
            .TextWrapping(TextWrapping.Wrap);

        var statusStrip = new Border()
            .Background(ColUserBar)
            .Padding(14, 6)
            .Child(_statusBannerLabel)
            .DockTop();

        // Zone de saisie de message (#383A40)
        _chatInputBox = new TextBox()
            .Placeholder("Message #general-chat or type /command (/account, /group, /mod, /mesh)...")
            .OnTextChanged(txt =>
            {
                if (_activeTab == GuiChannelTab.DirectMessage &&
                    _activeDmPeerId is AuthorId dmPeer &&
                    _dmService is not null)
                {
                    _ = _dmService.SendTypingIndicatorAsync(dmPeer, isTyping: !string.IsNullOrEmpty(txt));
                }
            })
            .Background(ColInputBg)
            .Foreground(ColTextHeader)
            .FontSize(13)
            .OnKeyDown(e =>
            {
                if (e.Key.ToString().Equals("Enter", StringComparison.OrdinalIgnoreCase) ||
                    e.Key.ToString().Equals("Return", StringComparison.OrdinalIgnoreCase))
                {
                    e.Handled = true;
                    _ = SubmitChatInputAsync();
                }
            });

        _sendBtn = new Button()
            .Content("Send")
            .Background(ColBlurple)
            .Foreground(ColTextHeader)
            .FontSize(13)
            .Bold()
            .CornerRadius(6)
            .Padding(16, 8)
            .DockRight()
            .OnClick(() => _ = SubmitChatInputAsync());

        var bottomComposer = new Border()
            .Background(ColChatBg)
            .Padding(16, 12)
            .Child(
                new Border()
                    .Background(ColInputBg)
                    .CornerRadius(8)
                    .Padding(12, 8)
                    .Child(
                        new DockPanel()
                            .Spacing(8)
                            .Children(_sendBtn, _chatInputBox)
                    )
            )
            .DockBottom();

        _feedItemsStack = new StackPanel()
            .Vertical()
            .StretchHorizontal()
            .Spacing(10)
            .Padding(16, 14);

        _feedScrollViewer = new ScrollViewer()
            .AutoVerticalScroll()
            .NoHorizontalScroll()
            .Content(_feedItemsStack);

        return new Border()
            .Background(ColChatBg)
            .Child(
                new DockPanel()
                    .Children(topChannelBar, statusStrip, bottomComposer, _feedScrollViewer)
            );
    }

    // 4. COLONNE DROITE (272px) — MEMBRES, MODÉRATION & PAIRS IROH EN LIGNE

    private void RebuildDirectMessageFeed(string peerName, IReadOnlyList<DirectMessageRecord> messages, bool isTyping)
    {
        if (_feedItemsStack is null)
        {
            return;
        }

        _feedItemsStack.Clear();
        if (messages.Count == 0)
        {
            _feedItemsStack.Add(
                new Border()
                    .Background(ColSidebar)
                    .CornerRadius(8)
                    .Padding(16)
                    .Child(
                        new StackPanel()
                            .Vertical()
                            .Spacing(4)
                            .Children(
                                new Label().Text($"Direct 1-on-1 thread with @{peerName}").FontSize(16).Bold().Foreground(ColTextHeader),
                                new Label().Text("Live QUIC stream (0x03) with immediate ACK & typing indicators. Automatic 10s fallback to blind-tagged PRoPHET DTN relay.").FontSize(12).Foreground(ColTextMuted).TextWrapping(TextWrapping.Wrap)
                            )
                    )
            );
        }

        double dmWrapWidth = _feedScrollViewer is { Bounds.Width: > 120 } sv ? sv.Bounds.Width - 64 : 640;
        string selfName = _activeSession?.Profile.Username ?? "Me";
        foreach (DirectMessageRecord msg in messages)
        {
            string author = msg.IsOutgoing ? selfName : peerName;
            string timeText = msg.SentAtUtc.ToLocalTime().ToString("HH:mm:ss");
            (string statusText, Color statusColor) = msg.Status switch
            {
                DirectMessageStatus.Delivered => ("Delivered ✓", ColGreen),
                DirectMessageStatus.QueuedViaRelay => ("✉ Queued via relay", ColAmber),
                DirectMessageStatus.Sending => ("✉ Delivering...", ColTextMuted),
                _ => ("Failed", ColRed)
            };

            var card = new Border()
                .Background(ColChatBg)
                .CornerRadius(6)
                .Padding(8, 6)
                .StretchHorizontal()
                .Child(
                    new StackPanel()
                        .Vertical()
                        .StretchHorizontal()
                        .Spacing(3)
                        .Children(
                            new StackPanel()
                                .Horizontal()
                                .Spacing(8)
                                .Children(
                                    new Label().Text(author).FontSize(14).Bold().Foreground(ColTextHeader),
                                    new Label().Text(timeText).FontSize(11).Foreground(ColTextMuted),
                                    new Label().Text(msg.IsOutgoing ? statusText : "Verified E2EE").FontSize(11).Foreground(msg.IsOutgoing ? statusColor : ColBlurple)
                                ),
                            new Label()
                                .Text(msg.Content)
                                .FontSize(13)
                                .Foreground(ColTextBody)
                                .TextWrapping(TextWrapping.Wrap)
                                .MaxWidth(dmWrapWidth)
                        )
                );
            _feedItemsStack.Add(card);
        }

        if (isTyping)
        {
            _feedItemsStack.Add(
                new Label()
                    .Text($"  ● {peerName} is typing...")
                    .FontSize(12)
                    .Foreground(ColTextMuted)
            );
        }
    }

    private void RebuildChatFeed(IReadOnlyList<GuiChatMessage> messages)
    {
        if (_feedItemsStack is null)
        {
            return;
        }

        _feedItemsStack.Clear();

        if (messages.Count == 0)
        {
            _feedItemsStack.Add(
                new Border()
                    .Background(ColSidebar)
                    .CornerRadius(8)
                    .Padding(16)
                    .Child(
                        new StackPanel()
                            .Vertical()
                            .Spacing(4)
                            .Children(
                                new Label()
                                    .Text($"Welcome to #{Slugify(_activeGroupTitle)}!")
                                    .FontSize(18)
                                    .Bold()
                                    .Foreground(ColTextHeader),
                                new Label()
                                    .Text("This is the start of the cryptographically signed Causal DAG history for this room.")
                                    .FontSize(13)
                                    .Foreground(ColTextMuted)
                                    .TextWrapping(TextWrapping.Wrap)
                            )
                    )
            );
            return;
        }

        double chatWrapWidth = _feedScrollViewer is { Bounds.Width: > 160 } sv ? sv.Bounds.Width - 110 : 590;
        foreach (GuiChatMessage msg in messages)
        {
            Color avatarColor = PickAvatarColor(msg.AuthorName);
            string initials = BuildAcronym(msg.AuthorName);
            string timeText = msg.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

            var msgCard = new Border()
                .Background(msg.IsSystem ? ColSidebar : ColChatBg)
                .CornerRadius(6)
                .Padding(8, 6)
                .StretchHorizontal()
                .Child(
                    new DockPanel()
                        .Spacing(12)
                        .Children(
                            new Border()
                                .Size(38, 38)
                                .CornerRadius(19)
                                .Background(msg.IsSystem ? ColButtonGray : avatarColor)
                                .Child(
                                    new Label()
                                        .Text(initials)
                                        .FontSize(13)
                                        .Bold()
                                        .Foreground(ColTextHeader)
                                        .Center()
                                )
                                .Top()
                                .DockLeft(),
                            new StackPanel()
                                .Vertical()
                                .StretchHorizontal()
                                .Spacing(3)
                                .Children(
                                    new StackPanel()
                                        .Horizontal()
                                        .Spacing(8)
                                        .Children(
                                            new Label()
                                                .Text(msg.AuthorName)
                                                .FontSize(14)
                                                .Bold()
                                                .Foreground(msg.IsSystem ? ColAmber : ColTextHeader),
                                            new Label()
                                                .Text(timeText)
                                                .FontSize(11)
                                                .Foreground(ColTextMuted)
                                                .CenterVertical(),
                                            new Label()
                                                .Text(msg.DagBadge)
                                                .FontSize(11)
                                                .Foreground(ColBlurple)
                                                .CenterVertical()
                                        ),
                                    new Label()
                                        .Text(msg.Content)
                                        .FontSize(13)
                                        .Foreground(ColTextBody)
                                        .TextWrapping(TextWrapping.Wrap)
                                        .MaxWidth(chatWrapWidth)
                                )
                        )
                );

            _feedItemsStack.Add(msgCard);
        }
    }

    private void RebuildGovernanceLogFeed(GroupGovernanceState? govState)
    {
        if (_feedItemsStack is null)
        {
            return;
        }

        _feedItemsStack.Clear();

        if (govState is null)
        {
            _feedItemsStack.Add(
                new Label()
                    .Text("Active room is not a governed group. Create or select a governed group from the left rail.")
                    .FontSize(13)
                    .Foreground(ColTextMuted)
            );
            return;
        }

        List<ModerationAction> entries = govState.GovernanceLog.Values
            .OrderBy(static a => a.LamportClock)
            .ThenBy(static a => a.IssuedAtUtc)
            .ThenBy(static a => a.ComputeActionId(), StringComparer.Ordinal)
            .ToList();

        foreach (ModerationAction act in entries)
        {
            string actId = ShortId(act.ComputeActionId());
            string target = ShortId(act.TargetAuthorId.Value);
            string signer = ShortId(act.SignerAuthorId.Value);
            int parents = act.Parents?.Count ?? 0;

            Color badgeCol = act.NewStatus switch
            {
                MemberStatus.Admitted => ColGreen,
                MemberStatus.Silenced => ColAmber,
                MemberStatus.Banned => ColRed,
                _ => ColBlurple
            };

            var card = new Border()
                .Background(ColSidebar)
                .CornerRadius(8)
                .Padding(12, 8)
                .Child(
                    new StackPanel()
                        .Vertical()
                        .Spacing(4)
                        .Children(
                            new StackPanel()
                                .Horizontal()
                                .Spacing(8)
                                .Children(
                                    new Label().Text($"[L={act.LamportClock}]").FontSize(12).Bold().Foreground(ColBlurple),
                                    new Label().Text($"{act.Kind} → {act.NewStatus}").FontSize(13).Bold().Foreground(badgeCol),
                                    new Label().Text($"id={actId} · parents={parents}").FontSize(11).Foreground(ColTextMuted)
                                ),
                            new Label()
                                .Text($"Target: @{target} | Signer: @{signer} | Reason: \"{act.Reason}\"")
                                .FontSize(12)
                                .Foreground(ColTextBody)
                                .TextWrapping(TextWrapping.Wrap)
                        )
                );

            _feedItemsStack.Add(card);
        }
    }

    private void RebuildTransactionLogsFeed(IReadOnlyList<GuiLogEntry> logs)
    {
        if (_feedItemsStack is null)
        {
            return;
        }

        _feedItemsStack.Clear();

        var filterBar = new StackPanel().Horizontal().Spacing(6);
        foreach (GuiLogTheme theme in Enum.GetValues<GuiLogTheme>())
        {
            GuiLogTheme captured = theme;
            bool active = _logFilter == captured;
            filterBar.Add(
                new Button()
                    .Content(captured.ToString().ToUpperInvariant())
                    .Background(active ? ColBlurple : ColSidebar)
                    .Foreground(ColTextHeader)
                    .FontSize(11)
                    .Bold()
                    .CornerRadius(5)
                    .Padding(8, 4)
                    .OnClick(() =>
                    {
                        _logFilter = captured;
                        _uiDirty = true;
                    })
            );
        }
        _feedItemsStack.Add(filterBar);

        foreach (GuiLogEntry entry in logs)
        {
            string time = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
            _feedItemsStack.Add(
                new Border()
                    .Background(ColSidebar)
                    .CornerRadius(5)
                    .Padding(8, 5)
                    .Child(
                        new Label()
                            .Text($"[{time}] [{entry.Theme}] {entry.Message}")
                            .FontSize(12)
                            .Foreground(ColTextBody)
                            .TextWrapping(TextWrapping.Wrap)
                    )
            );
        }
    }


    private async Task SubmitChatInputAsync()
    {
        if (_chatInputBox is null)
        {
            return;
        }

        string text = _chatInputBox.Text?.Trim() ?? string.Empty;
        _chatInputBox.Text(string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.StartsWith('/'))
        {
            await ExecuteSlashCommandAsync(text[1..]).ConfigureAwait(false);
        }
        else if (_transport is not DaemonMeshTransport)
        {
            SetStatus("whispernetd is not running — start the daemon first.");
            return;
        }
        else if (_activeTab == GuiChannelTab.DirectMessage && _activeDmPeerId is AuthorId dmPeer)
        {
            await EnsureDirectMessagingInitializedAsync().ConfigureAwait(false);
            if (_dmService is null)
            {
                SetStatus("Unlock an account and start the mesh before sending 1-on-1 DMs.");
                return;
            }

            _ = _dmService.SendTypingIndicatorAsync(dmPeer, isTyping: false);
            DirectMessageRecord rec = await _dmService.SendDirectMessageAsync(dmPeer, text).ConfigureAwait(false);
            lock (_stateLock)
            {
                if (!_dmMessages.Any(m => m.MessageUuid == rec.MessageUuid))
                {
                    _dmMessages.Add(rec);
                }
            }
            _uiDirty = true;
        }
        else
        {
            await SendChatMessageInActiveGroupAsync(text).ConfigureAwait(false);
        }
    }


    private async Task HandleManualDagSyncAsync()
    {
        GroupGovernanceState? gov;
        lock (_stateLock)
        {
            _governedGroups.TryGetValue(_activeGroupId, out gov);
        }

        if (gov is null || _directMesh is null || _transport is null)
        {
            SetStatus("Start mesh and select a governed group to sync its Causal DAG (stream 0x02).");
            return;
        }

        TriggerBackgroundGroupSync(gov);
        SetStatus($"Sent Causal DAG sync request (stream 0x02) to peers in '{gov.CurrentManifest.Title}'.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // =========================================================================
    // LOGIQUE MÉTIER & MESH P2P (PARTAGÉE AVEC WHISPERNET.APP.MESSAGING)
    // =========================================================================
}
