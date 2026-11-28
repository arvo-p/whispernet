using System.Diagnostics;
using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Whispernet.App.Messaging;
using Whispernet.Core;
using Whispernet.Daemon.Client;
using Whispernet.Transport.Iroh;

namespace Whispernet.Client.Linux.Gui;

public enum GuiChannelTab
{
    GeneralChat = 0,
    GovernanceLog = 1,
    TransactionLogs = 2,
    DirectMessage = 3
}

public enum GuiLogTheme
{
    All = 0,
    Auth = 1,
    Mesh = 2,
    Chat = 3,
    Group = 4,
    Cache = 5
}

public enum LoginScreenMode
{
    AccountList = 0,
    AnimatingToPassword = 1,
    PasswordPrompt = 2,
    CreateAccount = 3
}

public enum GroupModalMode
{
    Closed = 0,
    Choice = 1,
    AnimatingToCreate = 2,
    CreateForm = 3,
    AnimatingToJoin = 4,
    JoinForm = 5
}

public enum RightSidebarTab
{
    Members = 0,
    PendingMod = 1
}

public sealed record GuiChatMessage(
    DateTimeOffset TimestampUtc,
    string GroupId,
    string AuthorName,
    string DagBadge,
    string Content,
    bool IsSystem = false,
    string ChannelId = "");

public sealed record GuiLogEntry(
    DateTimeOffset TimestampUtc,
    GuiLogTheme Theme,
    string Message);

public sealed record GuiPeerStatus(
    NodeId NodeId,
    NodeRole Role,
    DateTimeOffset LastSeenUtc);

/// <summary>
/// Interface graphique Linux construite avec <c>Aprillz.MewUI</c>
/// et connectée à <c>Whispernet.App.Messaging</c>, <c>Whispernet.Core</c> et <c>Whispernet.Transport.Iroh</c>.
/// </summary>
public sealed partial class WhispernetGuiWindow
{
    // Palette Dark Theme
    private static readonly Color ColServerRail = Color.FromHex("#1E1F22");
    private static readonly Color ColSidebar = Color.FromHex("#2B2D31");
    private static readonly Color ColUserBar = Color.FromHex("#232428");
    private static readonly Color ColChatBg = Color.FromHex("#313338");
    private static readonly Color ColCardBg = Color.FromHex("#232428");
    private static readonly Color ColInputBg = Color.FromHex("#383A40");
    private static readonly Color ColChannelActive = Color.FromHex("#404249");
    private static readonly Color ColChannelInactive = Color.FromHex("#2B2D31");
    private static readonly Color ColDivider = Color.FromHex("#1F2023");
    private static readonly Color ColBlurple = Color.FromHex("#5865F2");
    private static readonly Color ColGreen = Color.FromHex("#23A55A");
    private static readonly Color ColAmber = Color.FromHex("#F0B232");
    private static readonly Color ColRed = Color.FromHex("#F23F43");
    private static readonly Color ColButtonGray = Color.FromHex("#4E5058");
    private static readonly Color ColTextHeader = Color.FromHex("#F2F3F5");
    private static readonly Color ColTextBody = Color.FromHex("#DBDEE1");
    private static readonly Color ColTextMuted = Color.FromHex("#949BA4");

    private static readonly Color[] AvatarPalette =
    {
        Color.FromHex("#5865F2"),
        Color.FromHex("#23A55A"),
        Color.FromHex("#EB459E"),
        Color.FromHex("#F0B232"),
        Color.FromHex("#ED4245"),
        Color.FromHex("#00A8FC")
    };

    private static readonly Command CmdGroupInfo = new("group.info", "Group Info");
    private static readonly Command CmdGroupConfig = new("group.config", "Group Config");
    private static readonly Command CmdGroupLeave = new("group.leave", "Leave");

    private readonly HashSet<string> _ownedGroupIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checkedOwnerGroupIds = new(StringComparer.Ordinal);

    private readonly string _effectiveRoot;
    private readonly IAccountService _accountService;
    private readonly ChatEventService _chatEventService;
    private readonly GroupGovernanceService _govService;
    private readonly FriendService _friendService;
    private readonly object _stateLock = new();

    private readonly List<GuiChatMessage> _messages = new();
    private readonly List<GuiLogEntry> _logs = new();
    private readonly Dictionary<NodeId, GuiPeerStatus> _peers = new();
    private readonly Dictionary<string, string> _groupTitles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatRoomDagState> _roomDags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupGovernanceState> _governedGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<AuthorId, string> _knownAuthorNames = new();
    private List<AccountProfile> _localAccounts = new();

    private AccountSession? _activeSession;
    private SqlitePeerCacheRepository? _peerCache;
    private SqliteChatHistoryRepository? _chatHistoryRepo;
    private SqliteDirectMessageRepository? _dmRepo;
    private DirectMessagingService? _dmService;
    private IMeshTransport? _transport;
    private GroupMeshService? _directMesh;
    private CancellationTokenSource? _meshCts;
    private readonly List<DirectMessageRecord> _dmMessages = new();
    private readonly Dictionary<AuthorId, DateTimeOffset> _dmTypingPeers = new();
    private AuthorId? _activeDmPeerId;
    private string? _boundSecretKeyB32;
    private string _boundAlpn = "/whispernet/direct/1";

    private string _activeGroupId = "general";
    private string _activeGroupTitle = "General Mesh";
    private string _activeChannelId = "general";
    private string _activeChannelName = "general";
    private GuiChannelTab _activeTab = GuiChannelTab.GeneralChat;
    private GuiLogTheme _logFilter = GuiLogTheme.All;
    private bool _preApproveInvite;
    private string? _lastInviteLink;
    private string _statusBanner = "Ready.";
    private volatile bool _uiDirty = true;

    public string ActiveChannelId { get { lock (_stateLock) return _activeChannelId; } }
    public string ActiveChannelName { get { lock (_stateLock) return _activeChannelName; } }

    // État de l'écran d'accueil / sélection de compte et de son animation
    private bool _isInMainWorkspace;
    private LoginScreenMode _loginMode = LoginScreenMode.AccountList;
    private AccountProfile? _selectedLoginAccount;
    private double _otherAccountsAnimProgress = 1.0; // 1.0 = visibles, 0.0 = disparus
    private readonly List<Border> _otherAccountCardElements = new();
    private string _loginErrorText = string.Empty;
    private bool _loginBusy;

    // État du popup modal Création / Rejoindre un groupe
    private GroupModalMode _groupModalMode = GroupModalMode.Closed;
    private double _groupModalAnimProgress = 1.0;
    private Border? _groupModalOverlay;
    private TransitionContentControl? _groupModalCardHost;
    private Border? _modalCreateChoiceCard;
    private Border? _modalJoinChoiceCard;
    private TextBox? _modalGroupTitleInput;
    private PasswordBox? _modalGroupPasswordInput;
    private TextBox? _modalNewChannelInput;
    private TextBox? _modalJoinLinkInput;
    private PasswordBox? _modalJoinPasswordInput;
    private Label? _modalErrorLabel;
    private readonly List<string> _modalCreateChannels = new() { ChannelId.DefaultGeneralName };

    // Conteneurs racine (Transition entre Écran Compte et Espace Principal)
    private TransitionContentControl? _rootScreenHost;
    private Element? _loginScreenElement;
    private Element? _mainWorkspaceElement;
    private TransitionContentControl? _loginCardHost;
    private Label? _loginErrorLabel;
    private PasswordBox? _loginPasswordInput;
    private TextBox? _createUsernameInput;
    private PasswordBox? _createPasswordInput;

    // Références aux conteneurs dynamiques de l'espace principal
    private Button? _homeRailBtn;
    private StackPanel? _serverRailIconsStack;
    private StackPanel? _groupChannelsSection;
    private StackPanel? _homeDirectMessagesSection;
    private StackPanel? _textChannelsListStack;
    private StackPanel? _dmPeersListStack;
    private Label? _serverHeaderTitleLabel;
    private Label? _serverHeaderRoleLabel;
    private Button? _tabChatBtn;
    private Button? _tabGovLogBtn;
    private Button? _tabTxLogsBtn;
    private Label? _userBarNameLabel;
    private Label? _userBarSubLabel;
    private Label? _meshStatusLabel;
    private Button? _meshToggleBtn;

    private Label? _channelTopTitleLabel;
    private Label? _channelTopTopicLabel;
    private Label? _statusBannerLabel;
    private StackPanel? _feedItemsStack;
    private ScrollViewer? _feedScrollViewer;
    private TextBox? _chatInputBox;
    private Button? _sendBtn;

    private StackPanel? _pendingListStack;
    private StackPanel? _membersListStack;
    private RightSidebarTab _activeRightTab = RightSidebarTab.Members;
    private Label? _rightHeaderTitleLabel;
    private StackPanel? _rightTabsRow;
    private Label? _rightMembersSectionTitleLabel;
    private Label? _rightPendingSectionTitleLabel;
    private Button? _rightTabMembersBtn;
    private Button? _rightTabPendingBtn;
    private DockPanel? _rightMembersView;
    private DockPanel? _rightPendingView;
    private Button? _rightInviteBtn;
    private Button? _rightAddFriendBtn;
    private Button? _rightDmRestrictBtn;
    private Grid? _preApproveRow;
    private Border? _preApproveTrack;
    private Border? _preApproveThumb;
    private double _preApproveAnimProgress;
    private DispatcherTimer? _refreshTimer;
    private DateTimeOffset _lastTestLoopbackPollUtc = DateTimeOffset.MinValue;

    public WhispernetGuiWindow(string effectiveRoot, IAccountService accountService)
    {
        _effectiveRoot = effectiveRoot;
        _accountService = accountService;
        _chatEventService = new ChatEventService(accountService);
        _govService = new GroupGovernanceService(accountService);
        _friendService = new FriendService(accountService);
        _friendService.FriendsChanged += () => _uiDirty = true;
        _friendService.FriendRequestsChanged += () => _uiDirty = true;
        _friendService.LogEmitted += msg => AddLog(GuiLogTheme.Group, msg);
        _groupTitles[_activeGroupId] = _activeGroupTitle;

        AddLog(GuiLogTheme.Group, $"Initialized default room '{_activeGroupTitle}' (#general).");
        AddLog(GuiLogTheme.Auth, $"Data root: {_effectiveRoot}");
    }

    public async Task InitializeAsync()
    {
        await TryHydratePeersFromSqliteAsync().ConfigureAwait(false);
        await TryHydrateChatHistoryFromSqliteAsync().ConfigureAwait(false);
        await RefreshAccountsListAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            if (_localAccounts.Count == 0)
            {
                _loginMode = LoginScreenMode.CreateAccount;
            }
        }
        _uiDirty = true;
    }

    public Window BuildWindow()
    {
        _ = Task.Run(InitializeAsync);

        _loginScreenElement = BuildLoginScreen();
        _mainWorkspaceElement = BuildMainWorkspaceGrid();

        _rootScreenHost = new TransitionContentControl()
            .Transition(ContentTransition.CreateScale(260))
            .Content(_loginScreenElement);

        _refreshTimer = new DispatcherTimer()
            .IntervalMs(25)
            .OnTick(OnUiTimerTick);
        _refreshTimer.Start();

        var window = new Window()
            .Title("Whispernet — P2P Encrypted Mesh & Causal DAG")
            .Resizable(1320, 820)
            .Background(ColServerRail)
            .Content(_rootScreenHost);

        window.Commands.Register<GroupGovernanceState>(CmdGroupInfo, OpenGroupInfoModal);
        window.Commands.Register<GroupGovernanceState>(CmdGroupConfig, OpenGroupConfigModal);
        window.Commands.Register<GroupGovernanceState>(CmdGroupLeave, OpenLeaveGroupConfirmModal);

        RefreshLoginCardContent();
        return window;
    }

    private void OnUiTimerTick()
    {
        // Animation fluide de disparition des autres comptes lorsqu'un compte protégé par mot de passe est cliqué
        if (_loginMode == LoginScreenMode.AnimatingToPassword)
        {
            _otherAccountsAnimProgress = Math.Max(0.0, _otherAccountsAnimProgress - 0.14);
            byte alpha = (byte)Math.Clamp((int)(255 * _otherAccountsAnimProgress), 0, 255);
            foreach (Border el in _otherAccountCardElements)
            {
                el.MaxHeight(Math.Max(1.0, 68.0 * _otherAccountsAnimProgress));
                el.Padding(14, Math.Max(0.0, 10.0 * _otherAccountsAnimProgress));
                el.Background(ColCardBg.WithAlpha(alpha));
                el.BorderBrush(ColDivider.WithAlpha(alpha));
            }

            if (_otherAccountsAnimProgress <= 0.01)
            {
                _loginMode = LoginScreenMode.PasswordPrompt;
                RefreshLoginCardContent();
            }
            return;
        }

        if (_groupModalMode is GroupModalMode.AnimatingToCreate or GroupModalMode.AnimatingToJoin)
        {
            _groupModalAnimProgress = Math.Max(0.0, _groupModalAnimProgress - 0.14);
            byte alpha = (byte)Math.Clamp((int)(255 * _groupModalAnimProgress), 0, 255);
            Border? fadingCard = _groupModalMode == GroupModalMode.AnimatingToCreate
                ? _modalJoinChoiceCard
                : _modalCreateChoiceCard;
            if (fadingCard is not null)
            {
                fadingCard.MaxHeight(Math.Max(1.0, 96.0 * _groupModalAnimProgress));
                fadingCard.Padding(14, Math.Max(0.0, 12.0 * _groupModalAnimProgress));
                fadingCard.Background(ColCardBg.WithAlpha(alpha));
                fadingCard.BorderBrush(ColDivider.WithAlpha(alpha));
            }

            if (_groupModalAnimProgress <= 0.01)
            {
                _groupModalMode = _groupModalMode == GroupModalMode.AnimatingToCreate
                    ? GroupModalMode.CreateForm
                    : GroupModalMode.JoinForm;
                RefreshGroupModalContent();
            }
            return;
        }

        lock (_stateLock)
        {
            if (_dmTypingPeers.Count > 0)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                List<AuthorId> expired = _dmTypingPeers
                    .Where(kv => (now - kv.Value) >= TimeSpan.FromSeconds(5))
                    .Select(static kv => kv.Key)
                    .ToList();
                if (expired.Count > 0)
                {
                    foreach (AuthorId id in expired)
                    {
                        _dmTypingPeers.Remove(id);
                    }
                    _uiDirty = true;
                }
            }
        }

        double targetPreApprove = _preApproveInvite ? 1.0 : 0.0;
        if (Math.Abs(_preApproveAnimProgress - targetPreApprove) > 0.001)
        {
            double step = 0.18;
            _preApproveAnimProgress = _preApproveAnimProgress < targetPreApprove
                ? Math.Min(targetPreApprove, _preApproveAnimProgress + step)
                : Math.Max(targetPreApprove, _preApproveAnimProgress - step);
            ApplyPreApproveSwitchVisual(_preApproveAnimProgress);
        }

        if (_isInMainWorkspace && _activeSession is not null &&
            (DateTimeOffset.UtcNow - _lastTestLoopbackPollUtc) >= TimeSpan.FromSeconds(1))
        {
            _lastTestLoopbackPollUtc = DateTimeOffset.UtcNow;
            _ = _friendService.ConsumeTestLoopbackInboxAsync();
            if (_dmService is not null)
            {
                _ = _dmService.ConsumeTestLoopbackInboxAsync();
            }
        }

        if (_uiDirty)
        {
            _uiDirty = false;
            if (_isInMainWorkspace)
            {
                RefreshDynamicPanels();
            }
            else
            {
                RefreshLoginCardContent();
            }
        }
    }

    public void Shutdown()
    {
        _refreshTimer?.Stop();
        _activeSession?.Dispose();
        StopMeshInternal();
    }

    // =========================================================================
    // ÉCRAN D'OUVERTURE : SÉLECTION / CRÉATION DE COMPTE AVEC ANIMATION
    // =========================================================================

    private void RefreshDynamicPanels()
    {
        string activeGid;
        string activeTitle;
        string activeChId;
        string activeChName;
        AccountSession? session;
        bool meshOnline;
        bool isDaemonConnected;
        NodeId? selfNodeId;
        List<GroupGovernanceState> groups;
        GroupGovernanceState? activeGov;
        ChatRoomDagState? activeDag;
        List<GuiChatMessage> roomMessages;
        List<GuiLogEntry> filteredLogs;
        List<GuiPeerStatus> recentPeers;

        lock (_stateLock)
        {
            activeGid = _activeGroupId;
            activeTitle = _activeGroupTitle;
            activeChId = _activeChannelId;
            activeChName = _activeChannelName;
            session = _activeSession;
            meshOnline = _transport is not null;
            isDaemonConnected = _transport is DaemonMeshTransport;
            selfNodeId = _transport?.SelfId;
            groups = _governedGroups.Values
                .OrderBy(static g => g.CurrentManifest.IssuedAtUtc)
                .ToList();
            _governedGroups.TryGetValue(activeGid, out activeGov);
            string dagKey = !string.IsNullOrWhiteSpace(activeChId) ? activeChId : activeGid;
            _roomDags.TryGetValue(dagKey, out activeDag);

            roomMessages = _messages
                .Where(m => m.GroupId.Equals(activeGid, StringComparison.Ordinal) &&
                            (m.IsSystem || string.IsNullOrEmpty(m.ChannelId) || m.ChannelId.Equals(activeChId, StringComparison.OrdinalIgnoreCase)))
                .TakeLast(120)
                .ToList();

            filteredLogs = _logs
                .Where(l => _logFilter == GuiLogTheme.All || l.Theme == _logFilter)
                .TakeLast(120)
                .ToList();

            DateTimeOffset now = DateTimeOffset.UtcNow;
            recentPeers = _peers.Values
                .Where(p => (now - p.LastSeenUtc) <= TimeSpan.FromMinutes(15))
                .OrderByDescending(static p => p.Role == NodeRole.MeetingPoint)
                .ThenByDescending(static p => p.LastSeenUtc)
                .ToList();
        }

        // 1. Rail des serveurs (icônes rondes / squircle)
        bool isHomeActive = activeGov is null;
        _homeRailBtn?
            .Background(isHomeActive ? ColBlurple : ColChatBg)
            .CornerRadius(isHomeActive ? 14 : 24);

        if (_serverRailIconsStack is not null)
        {
            _serverRailIconsStack.Clear();
            foreach (GroupGovernanceState g in groups)
            {
                GroupGovernanceState capturedGov = g;
                string gid = g.GroupId.Value;
                string title = g.CurrentManifest.Title;
                bool isSelected = gid.Equals(activeGid, StringComparison.Ordinal);
                string acronym = BuildAcronym(title);
                if (session is not null && _checkedOwnerGroupIds.Add(gid))
                {
                    _ = CheckAndCacheGroupOwnerAsync(session, g.GroupId);
                }
                bool isAdmin = _ownedGroupIds.Contains(gid);

                var menu = new ContextMenu();
                menu.Item("Group Info", CmdGroupInfo, capturedGov);
                if (isAdmin)
                {
                    menu.Item("Group Config", CmdGroupConfig, capturedGov);
                }
                menu.Separator();
                menu.Item("Leave", CmdGroupLeave, capturedGov);

                var btn = new Button()
                    .Content(acronym)
                    .Size(48, 48)
                    .Background(isSelected ? ColBlurple : ColChatBg)
                    .Foreground(ColTextHeader)
                    .FontSize(14)
                    .Bold()
                    .CornerRadius(isSelected ? 14 : 24)
                    .ToolTip($"{title} (#{ShortId(gid)})")
                    .ContextMenu(menu)
                    .OnClick(() => SwitchActiveGroup(gid, title));

                _serverRailIconsStack.Add(btn.CenterHorizontal());
            }
        }

        // 2. En-tête du serveur et barre utilisateur
        _serverHeaderTitleLabel?.Text(activeTitle);
        string roleText = string.Empty;
        if (activeGov is not null)
        {
            if (activeGov.CurrentManifest.IsTerminated)
            {
                roleText = "[TERMINATED]";
            }
            else if (session is not null)
            {
                MemberStatus st = activeGov.GetMemberStatus(session.Profile.AuthorId);
                if (st is MemberStatus.Silenced or MemberStatus.Banned)
                {
                    roleText = $"[{st.ToString().ToUpperInvariant()}]";
                }
            }
        }
        if (_serverHeaderRoleLabel is not null)
        {
            _serverHeaderRoleLabel.Text(roleText);
            _serverHeaderRoleLabel.IsVisible = !string.IsNullOrEmpty(roleText);
        }

        if (session is not null)
        {
            _userBarNameLabel?.Text(session.Profile.Username);
            _userBarSubLabel?.Text($"@{ShortId(session.Profile.AuthorId.Value)}");
        }
        else
        {
            _userBarNameLabel?.Text("Locked");
            _userBarSubLabel?.Text("No active session");
        }

        if (meshOnline && selfNodeId is not null)
        {
            if (isDaemonConnected)
            {
                _meshStatusLabel?.Text($"● {ShortId(selfNodeId.Value.Value)}").Foreground(ColGreen);
            }
            else
            {
                _meshStatusLabel?.Text("● no daemon").Foreground(ColRed);
            }
            _meshToggleBtn?.Content("Stop").Background(ColRed);
        }
        else
        {
            _meshStatusLabel?.Text("● no daemon").Foreground(ColRed);
            _meshToggleBtn?.Content("Start").Background(ColGreen);
        }

        if (_chatInputBox is not null)
        {
            _chatInputBox.IsEnabled = isDaemonConnected;
            _chatInputBox.Placeholder(isDaemonConnected
                ? "Message or type /command (/account, /group, /channel, /mod, /mesh)..."
                : "whispernetd is not running — start the daemon to send messages");
        }
        if (_sendBtn is not null)
        {
            _sendBtn.IsEnabled = isDaemonConnected;
        }

        // Onglets de salons
        if (_textChannelsListStack is not null)
        {
            _textChannelsListStack.Clear();
            if (activeGov is not null)
            {
                IReadOnlyList<GroupChannel> channels = activeGov.CurrentManifest.GetEffectiveChannels();
                foreach (GroupChannel ch in channels)
                {
                    string chIdVal = ch.ChannelId.Value;
                    string chNameVal = ch.Name;
                    bool isChSelected = _activeTab == GuiChannelTab.GeneralChat &&
                        chIdVal.Equals(activeChId, StringComparison.OrdinalIgnoreCase);

                    var chBtn = new Button()
                        .Content($"# {chNameVal}")
                        .StretchHorizontal()
                        .Background(isChSelected ? ColChannelActive : ColChannelInactive)
                        .Foreground(isChSelected ? ColTextHeader : ColTextMuted)
                        .FontSize(13)
                        .Bold()
                        .CornerRadius(6)
                        .Padding(10, 6)
                        .ToolTip($"#{chNameVal} (id={chIdVal})")
                        .OnClick(() => SwitchActiveChannel(chIdVal, chNameVal));

                    _textChannelsListStack.Add(chBtn);
                }
            }
            else if (_tabChatBtn is not null)
            {
                _tabChatBtn.Content($"# {Slugify(activeTitle)}")
                    .Background(_activeTab == GuiChannelTab.GeneralChat ? ColChannelActive : ColChannelInactive)
                    .Foreground(_activeTab == GuiChannelTab.GeneralChat ? ColTextHeader : ColTextMuted);
                _textChannelsListStack.Add(_tabChatBtn);
            }
        }

        _tabGovLogBtn?.Background(_activeTab == GuiChannelTab.GovernanceLog ? ColChannelActive : ColChannelInactive)
            .Foreground(_activeTab == GuiChannelTab.GovernanceLog ? ColTextHeader : ColTextMuted);
        _tabTxLogsBtn?.Background(_activeTab == GuiChannelTab.TransactionLogs ? ColChannelActive : ColChannelInactive)
            .Foreground(_activeTab == GuiChannelTab.TransactionLogs ? ColTextHeader : ColTextMuted);

        if (_groupChannelsSection is not null)
        {
            _groupChannelsSection.IsVisible = activeGov is not null;
        }
        if (_homeDirectMessagesSection is not null)
        {
            _homeDirectMessagesSection.IsVisible = activeGov is null;
        }

        if (_dmPeersListStack is not null)
        {
            _dmPeersListStack.Clear();
            List<(AuthorId Id, string Name, bool IsOnline)> mutualPeers = CollectKnownPeers(session, groups, recentPeers);

            if (mutualPeers.Count == 0)
            {
                _dmPeersListStack.Add(new Label().Text("No direct message chats yet.").FontSize(11).Foreground(ColTextMuted));
            }
            else
            {
                foreach ((AuthorId pid, string pName, bool _) in mutualPeers)
                {
                    AuthorId captured = pid;
                    string capturedName = pName;
                    bool isDmActive = _activeTab == GuiChannelTab.DirectMessage && _activeDmPeerId == captured;
                    _dmPeersListStack.Add(
                        new Button()
                            .Content($"@ {capturedName}")
                            .StretchHorizontal()
                            .Background(isDmActive ? ColChannelActive : ColChannelInactive)
                            .Foreground(isDmActive ? ColTextHeader : ColTextMuted)
                            .FontSize(12)
                            .Bold()
                            .CornerRadius(6)
                            .Padding(10, 6)
                            .OnClick(() => OpenDirectMessageChat(captured, capturedName))
                    );
                }
            }
        }

        // 3. Barre supérieure du salon et flux principal
        _statusBannerLabel?.Text(_statusBanner);
        ulong maxClock = activeDag?.MaxLogicalClock ?? 0;
        int headCount = activeDag?.HeadIds.Count ?? 0;

        if (_activeTab == GuiChannelTab.DirectMessage && _activeDmPeerId is AuthorId dmPeer)
        {
            string peerName;
            bool isTyping = false;
            List<DirectMessageRecord> dmThread;
            lock (_stateLock)
            {
                peerName = _knownAuthorNames.TryGetValue(dmPeer, out string? n) && !string.IsNullOrWhiteSpace(n)
                    ? n
                    : ShortId(dmPeer.Value);
                dmThread = _dmMessages.Where(m => m.PeerAuthorId == dmPeer).TakeLast(120).ToList();
                if (_dmTypingPeers.TryGetValue(dmPeer, out DateTimeOffset typingUtc) &&
                    (DateTimeOffset.UtcNow - typingUtc) < TimeSpan.FromSeconds(5))
                {
                    isTyping = true;
                }
            }

            _channelTopTitleLabel?.Text($"@ {peerName}");
            _channelTopTopicLabel?.Text(isTyping
                ? $"{peerName} is typing..."
                : $"1-on-1 E2EE (ECDH P-256) · ACK Required · 10s PRoPHET DTN Fallback");
            RebuildDirectMessageFeed(peerName, dmThread, isTyping);
        }
        else if (_activeTab == GuiChannelTab.GeneralChat)
        {
            string displayCh = activeGov is not null ? activeChName : Slugify(activeTitle);
            _channelTopTitleLabel?.Text($"# {displayCh}");
            _channelTopTopicLabel?.Text($"GroupId: {ShortId(activeGid)} · ChannelId: {ShortId(activeChId)} · Lamport L={maxClock} · Heads={headCount}");
            RebuildChatFeed(roomMessages);
        }
        else if (_activeTab == GuiChannelTab.GovernanceLog)
        {
            _channelTopTitleLabel?.Text("# governance-log");
            _channelTopTopicLabel?.Text(activeGov is null
                ? "Select a governed group to inspect its CompactGovernanceLog"
                : $"Entries={activeGov.GovernanceLog.Count} · MaxLamport={activeGov.MaxLamportClock} · Checkpoint={activeGov.CurrentManifest.CheckpointLamportClock}");
            RebuildGovernanceLogFeed(activeGov);
        }
        else
        {
            _channelTopTitleLabel?.Text("# transaction-logs");
            _channelTopTopicLabel?.Text($"Theme Filter: {_logFilter} ({filteredLogs.Count} entries)");
            RebuildTransactionLogsFeed(filteredLogs);
        }

        // 4. Colonne droite : Demandes d'admission, Membres CRDT, Pairs Iroh (ou Friends en mode Home)
        RebuildRightSidebar(activeGov, groups, session, recentPeers);
    }

    private void OpenDirectMessageChat(AuthorId peerId, string peerName)
    {
        lock (_stateLock)
        {
            _activeDmPeerId = peerId;
            _activeTab = GuiChannelTab.DirectMessage;
        }
        SetStatus($"Direct 1-on-1 chat with @{peerName} (ACK required, 10s DTN fallback).");
        _uiDirty = true;
    }

    private List<(AuthorId Id, string Name, bool IsOnline)> CollectKnownPeers(
        AccountSession? session,
        IReadOnlyList<GroupGovernanceState> groups,
        IReadOnlyList<GuiPeerStatus> recentPeers)
    {
        var result = new List<(AuthorId Id, string Name, bool IsOnline)>();
        if (session is null)
        {
            return result;
        }

        var seenIds = new HashSet<AuthorId>();
        var nodeMap = new Dictionary<AuthorId, NodeId>();
        foreach (FriendEntry friend in _friendService.GetAcceptedFriends())
        {
            if (friend.PeerAuthorId != session.Profile.AuthorId)
            {
                seenIds.Add(friend.PeerAuthorId);
                if (friend.LastKnownNodeId is NodeId fnid)
                {
                    nodeMap[friend.PeerAuthorId] = fnid;
                }
            }
        }
        foreach (GroupGovernanceState g in groups)
        {
            foreach (AuthorId mid in g.MemberStates.Keys)
            {
                if (mid != session.Profile.AuthorId)
                {
                    seenIds.Add(mid);
                    if (g.KnownAuthorNodes.TryGetValue(mid, out NodeId nid))
                    {
                        nodeMap[mid] = nid;
                    }
                    else if (g.MemberStates.TryGetValue(mid, out ModerationAction? act) && act.TargetLastKnownNodeId is NodeId lastNid)
                    {
                        nodeMap[mid] = lastNid;
                    }
                }
            }
        }

        lock (_stateLock)
        {
            foreach (DirectMessageRecord dm in _dmMessages)
            {
                if (dm.PeerAuthorId != session.Profile.AuthorId)
                {
                    seenIds.Add(dm.PeerAuthorId);
                }
            }

            foreach (AuthorId pid in seenIds)
            {
                string name = _knownAuthorNames.TryGetValue(pid, out string? n) && !string.IsNullOrWhiteSpace(n)
                    ? n
                    : ShortId(pid.Value);
                bool isOnline = nodeMap.TryGetValue(pid, out NodeId nid) && recentPeers.Any(p => p.NodeId == nid);
                result.Add((pid, name, isOnline));
            }
        }

        return result
            .OrderByDescending(static p => p.IsOnline)
            .ThenBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task CheckAndCacheGroupOwnerAsync(AccountSession session, GroupId groupId)
    {
        bool isOwner = await _govService.IsGroupOwnerAsync(session, groupId).ConfigureAwait(false);
        if (isOwner)
        {
            lock (_stateLock)
            {
                _ownedGroupIds.Add(groupId.Value);
                _uiDirty = true;
            }
        }
    }
}
