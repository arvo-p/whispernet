using System.Diagnostics;
using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Whispernet.App.Messaging;
using Whispernet.Core;
using Whispernet.Transport.Iroh;

namespace Whispernet.Client.Linux.Gui;

public enum GuiChannelTab
{
    GeneralChat = 0,
    GovernanceLog = 1,
    TransactionLogs = 2
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
public sealed class WhispernetGuiWindow
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

    private readonly string _effectiveRoot;
    private readonly IAccountService _accountService;
    private readonly ChatEventService _chatEventService;
    private readonly GroupGovernanceService _govService;
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
    private IrohEndpointAdapter? _transport;
    private GroupMeshService? _directMesh;
    private CancellationTokenSource? _meshCts;
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
    private StackPanel? _serverRailIconsStack;
    private StackPanel? _textChannelsListStack;
    private Border? _channelAdminCard;
    private TextBox? _newChannelNameBox;
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

    private TextBox? _groupTitleOrInviteBox;
    private PasswordBox? _groupPassBox;
    private Button? _preApproveToggleBtn;

    private StackPanel? _pendingListStack;
    private StackPanel? _membersListStack;
    private StackPanel? _peersListStack;
    private DispatcherTimer? _refreshTimer;

    public WhispernetGuiWindow(string effectiveRoot, IAccountService accountService)
    {
        _effectiveRoot = effectiveRoot;
        _accountService = accountService;
        _chatEventService = new ChatEventService(accountService);
        _govService = new GroupGovernanceService(accountService);
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
    private Element BuildLoginScreen()
    {
        _loginCardHost = new TransitionContentControl()
            .Transition(ContentTransition.CreateScale(220));

        _loginErrorLabel = new Label()
            .Text(string.Empty)
            .FontSize(12)
            .Bold()
            .Foreground(ColRed)
            .CenterHorizontal()
            .TextWrapping(TextWrapping.Wrap);

        var centerCard = new Border()
            .Width(480)
            .Background(ColSidebar)
            .BorderBrush(ColDivider)
            .BorderThickness(1)
            .CornerRadius(12)
            .Padding(28, 26)
            .Center()
            .Child(
                new StackPanel()
                    .Vertical()
                    .Spacing(16)
                    .Children(
                        new StackPanel()
                            .Vertical()
                            .Spacing(6)
                            .Children(
                                new Border()
                                    .Size(56, 56)
                                    .CornerRadius(18)
                                    .Background(ColBlurple)
                                    .CenterHorizontal()
                                    .Child(
                                        new Label()
                                            .Text("W")
                                            .FontSize(24)
                                            .Bold()
                                            .Foreground(ColTextHeader)
                                            .Center()
                                    ),
                                new Label()
                                    .Text("Welcome to Whispernet")
                                    .FontSize(22)
                                    .Bold()
                                    .Foreground(ColTextHeader)
                                    .CenterHorizontal(),
                                new Label()
                                    .Text("Select an identity or create a new account. The Iroh P2P mesh will start automatically.")
                                    .FontSize(12)
                                    .Foreground(ColTextMuted)
                                    .CenterHorizontal()
                                    .TextWrapping(TextWrapping.Wrap)
                            ),
                        _loginErrorLabel,
                        _loginCardHost
                    )
            );

        return new Border()
            .Background(ColServerRail)
            .Child(centerCard);
    }

    private void RefreshLoginCardContent()
    {
        if (_loginCardHost is null)
        {
            return;
        }

        _loginErrorLabel?.Text(_loginErrorText);

        List<AccountProfile> accounts;
        lock (_stateLock)
        {
            accounts = _localAccounts.ToList();
        }

        if (_loginMode is LoginScreenMode.AccountList or LoginScreenMode.AnimatingToPassword)
        {
            _otherAccountCardElements.Clear();
            var listStack = new StackPanel().Vertical().Spacing(10);

            if (accounts.Count == 0)
            {
                listStack.Add(
                    new Label()
                        .Text("No local account found yet.")
                        .FontSize(13)
                        .Foreground(ColTextMuted)
                        .CenterHorizontal()
                );
            }
            else
            {
                foreach (AccountProfile acc in accounts)
                {
                    AccountProfile captured = acc;
                    bool isSelectedTarget = _selectedLoginAccount?.AuthorId == captured.AuthorId;
                    Color avatarCol = PickAvatarColor(captured.Username);
                    string badge = captured.IsPasswordProtected ? "🔒 Password required" : "🔓 Instant unlock";

                    var card = new Border()
                        .Background(isSelectedTarget ? ColChannelActive : ColCardBg)
                        .BorderBrush(isSelectedTarget ? ColBlurple : ColDivider)
                        .BorderThickness(1)
                        .CornerRadius(8)
                        .Padding(14, 10)
                        .Child(
                            new DockPanel()
                                .Spacing(12)
                                .Children(
                                    new Button()
                                        .Content(captured.IsPasswordProtected ? "Select" : "Launch →")
                                        .Background(ColBlurple)
                                        .Foreground(ColTextHeader)
                                        .FontSize(12)
                                        .Bold()
                                        .CornerRadius(6)
                                        .Padding(12, 6)
                                        .DockRight()
                                        .CenterVertical()
                                        .OnClick(() => OnLoginAccountClicked(captured)),
                                    new StackPanel()
                                        .Horizontal()
                                        .Spacing(12)
                                        .Children(
                                            new Border()
                                                .Size(40, 40)
                                                .CornerRadius(20)
                                                .Background(avatarCol)
                                                .Child(
                                                    new Label()
                                                        .Text(BuildAcronym(captured.Username))
                                                        .FontSize(14)
                                                        .Bold()
                                                        .Foreground(ColTextHeader)
                                                        .Center()
                                                ),
                                            new StackPanel()
                                                .Vertical()
                                                .Spacing(2)
                                                .Children(
                                                    new Label()
                                                        .Text(captured.Username)
                                                        .FontSize(15)
                                                        .Bold()
                                                        .Foreground(ColTextHeader),
                                                    new Label()
                                                        .Text($"@{ShortId(captured.AuthorId.Value)} · {badge}")
                                                        .FontSize(11)
                                                        .Foreground(ColTextMuted)
                                                )
                                        )
                                )
                        );

                    if (!isSelectedTarget)
                    {
                        card.ClipToBounds(true);
                        _otherAccountCardElements.Add(card);
                    }
                    listStack.Add(card);
                }
            }

            var createBtn = new Button()
                .Content("+ Create a New Account")
                .StretchHorizontal()
                .Background(ColGreen)
                .Foreground(ColTextHeader)
                .FontSize(13)
                .Bold()
                .CornerRadius(8)
                .Padding(14, 10)
                .OnClick(() =>
                {
                    _loginErrorText = string.Empty;
                    _selectedLoginAccount = null;
                    _loginMode = LoginScreenMode.CreateAccount;
                    RefreshLoginCardContent();
                });

            var createBtnWrapper = new Border()
                .ClipToBounds(true)
                .Child(createBtn);
            _otherAccountCardElements.Add(createBtnWrapper);
            listStack.Add(createBtnWrapper);

            _loginCardHost.Content(
                new ScrollViewer()
                    .MaxHeight(380)
                    .AutoVerticalScroll()
                    .NoHorizontalScroll()
                    .Content(listStack)
            );
        }
        else if (_loginMode == LoginScreenMode.PasswordPrompt && _selectedLoginAccount is not null)
        {
            AccountProfile target = _selectedLoginAccount;
            Color avatarCol = PickAvatarColor(target.Username);

            _loginPasswordInput = new PasswordBox()
                .Placeholder($"Enter password for {target.Username}...")
                .Background(ColServerRail)
                .Foreground(ColTextHeader)
                .FontSize(14)
                .OnKeyDown(e =>
                {
                    if (e.Key.ToString().Equals("Enter", StringComparison.OrdinalIgnoreCase) ||
                        e.Key.ToString().Equals("Return", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Handled = true;
                        _ = SubmitSelectedAccountPasswordAsync();
                    }
                });

            var selectedCard = new Border()
                .Background(ColCardBg)
                .BorderBrush(ColBlurple)
                .BorderThickness(1)
                .CornerRadius(8)
                .Padding(14, 12)
                .Child(
                    new StackPanel()
                        .Horizontal()
                        .Spacing(12)
                        .Children(
                            new Border()
                                .Size(42, 42)
                                .CornerRadius(21)
                                .Background(avatarCol)
                                .Child(
                                    new Label()
                                        .Text(BuildAcronym(target.Username))
                                        .FontSize(15)
                                        .Bold()
                                        .Foreground(ColTextHeader)
                                        .Center()
                                ),
                            new StackPanel()
                                .Vertical()
                                .Spacing(2)
                                .Children(
                                    new Label()
                                        .Text(target.Username)
                                        .FontSize(16)
                                        .Bold()
                                        .Foreground(ColTextHeader),
                                    new Label()
                                        .Text($"@{ShortId(target.AuthorId.Value)} · 🔒 Encrypted Vault")
                                        .FontSize(11)
                                        .Foreground(ColTextMuted)
                                )
                        )
                );

            var promptStack = new StackPanel()
                .Vertical()
                .Spacing(12)
                .Children(
                    selectedCard,
                    new Label()
                        .Text("PASSWORD")
                        .FontSize(11)
                        .Bold()
                        .Foreground(ColTextMuted),
                    _loginPasswordInput,
                    new Button()
                        .Content(_loginBusy ? "Unlocking & Starting Mesh..." : "Unlock & Start Mesh →")
                        .StretchHorizontal()
                        .Background(ColBlurple)
                        .Foreground(ColTextHeader)
                        .FontSize(13)
                        .Bold()
                        .CornerRadius(8)
                        .Padding(14, 10)
                        .OnClick(() => _ = SubmitSelectedAccountPasswordAsync()),
                    new Button()
                        .Content("← Choose another account")
                        .StretchHorizontal()
                        .Background(ColButtonGray)
                        .Foreground(ColTextBody)
                        .FontSize(12)
                        .CornerRadius(8)
                        .Padding(12, 8)
                        .OnClick(() =>
                        {
                            _loginErrorText = string.Empty;
                            _selectedLoginAccount = null;
                            _otherAccountsAnimProgress = 1.0;
                            _loginMode = LoginScreenMode.AccountList;
                            RefreshLoginCardContent();
                        })
                );

            _loginCardHost.Content(promptStack);
        }
        else if (_loginMode == LoginScreenMode.CreateAccount)
        {
            _createUsernameInput = new TextBox()
                .Placeholder("Username (e.g. Alice)")
                .Background(ColServerRail)
                .Foreground(ColTextHeader)
                .FontSize(14);

            _createPasswordInput = new PasswordBox()
                .Placeholder("Optional password (leave blank for instant login)")
                .Background(ColServerRail)
                .Foreground(ColTextHeader)
                .FontSize(14)
                .OnKeyDown(e =>
                {
                    if (e.Key.ToString().Equals("Enter", StringComparison.OrdinalIgnoreCase) ||
                        e.Key.ToString().Equals("Return", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Handled = true;
                        _ = SubmitCreateAccountFromLoginAsync();
                    }
                });

            var createStack = new StackPanel()
                .Vertical()
                .Spacing(10)
                .Children(
                    new Label().Text("CREATE NEW IDENTITY").FontSize(12).Bold().Foreground(ColTextHeader),
                    new Label().Text("USERNAME").FontSize(11).Bold().Foreground(ColTextMuted),
                    _createUsernameInput,
                    new Label().Text("PASSWORD (OPTIONAL — AES-256-GCM VAULT)").FontSize(11).Bold().Foreground(ColTextMuted),
                    _createPasswordInput,
                    new Button()
                        .Content(_loginBusy ? "Creating & Starting Mesh..." : "Create Account & Start Mesh →")
                        .StretchHorizontal()
                        .Background(ColGreen)
                        .Foreground(ColTextHeader)
                        .FontSize(13)
                        .Bold()
                        .CornerRadius(8)
                        .Padding(14, 10)
                        .OnClick(() => _ = SubmitCreateAccountFromLoginAsync())
                );

            if (accounts.Count > 0)
            {
                createStack.Add(
                    new Button()
                        .Content("← Back to existing accounts")
                        .StretchHorizontal()
                        .Background(ColButtonGray)
                        .Foreground(ColTextBody)
                        .FontSize(12)
                        .CornerRadius(8)
                        .Padding(12, 8)
                        .OnClick(() =>
                        {
                            _loginErrorText = string.Empty;
                            _selectedLoginAccount = null;
                            _otherAccountsAnimProgress = 1.0;
                            _loginMode = LoginScreenMode.AccountList;
                            RefreshLoginCardContent();
                        })
                );
            }

            _loginCardHost.Content(createStack);
        }
    }

    private void OnLoginAccountClicked(AccountProfile account)
    {
        if (_loginBusy)
        {
            return;
        }

        _loginErrorText = string.Empty;
        _selectedLoginAccount = account;

        if (account.IsPasswordProtected)
        {
            // Déclenche l'animation de disparition de tous les autres comptes avant d'afficher le champ de mot de passe
            _otherAccountsAnimProgress = 1.0;
            RefreshLoginCardContent();
            _loginMode = LoginScreenMode.AnimatingToPassword;
        }
        else
        {
            _ = UnlockAndEnterWorkspaceAsync(account, password: null);
        }
    }

    private async Task SubmitSelectedAccountPasswordAsync()
    {
        if (_loginBusy || _selectedLoginAccount is null)
        {
            return;
        }

        string pwd = _loginPasswordInput?.Password ?? string.Empty;
        if (string.IsNullOrEmpty(pwd))
        {
            _loginErrorText = "Please enter the password for this account.";
            _uiDirty = true;
            return;
        }

        await UnlockAndEnterWorkspaceAsync(_selectedLoginAccount, pwd).ConfigureAwait(false);
    }

    private async Task SubmitCreateAccountFromLoginAsync()
    {
        if (_loginBusy)
        {
            return;
        }

        string username = _createUsernameInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username))
        {
            _loginErrorText = "Please enter a username.";
            _uiDirty = true;
            return;
        }

        string? pwd = _createPasswordInput?.Password;
        if (string.IsNullOrWhiteSpace(pwd))
        {
            pwd = null;
        }

        _loginBusy = true;
        _loginErrorText = string.Empty;
        _uiDirty = true;

        try
        {
            AccountProfile created = await _accountService.CreateAccountAsync(username, pwd).ConfigureAwait(false);
            await RefreshAccountsListAsync().ConfigureAwait(false);
            _loginBusy = false;
            await UnlockAndEnterWorkspaceAsync(created, pwd).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _loginBusy = false;
            _loginErrorText = ex.Message;
            _uiDirty = true;
        }
    }

    private async Task UnlockAndEnterWorkspaceAsync(AccountProfile profile, string? password)
    {
        _loginBusy = true;
        _loginErrorText = string.Empty;
        _uiDirty = true;

        try
        {
            AccountSession session = await _accountService.UnlockAccountAsync(
                profile.AuthorId,
                profile.IsPasswordProtected ? password : null).ConfigureAwait(false);

            lock (_stateLock)
            {
                _activeSession?.Dispose();
                _activeSession = session;
                _knownAuthorNames[session.Profile.AuthorId] = session.Profile.Username;
            }

            IReadOnlyList<GroupGovernanceState> savedGroups = await _govService.LoadAllGroupsFromVaultAsync(session).ConfigureAwait(false);
            foreach (GroupGovernanceState g in savedGroups)
            {
                RegisterGovernedGroup(g);
            }

            // Démarrer automatiquement le mesh Iroh avec la clé dédiée au compte
            if (_transport is not null)
            {
                StopMeshInternal();
            }
            await HandleMeshCommandAsync(new List<string> { "mesh", "start" }).ConfigureAwait(false);

            SetStatus($"Logged in as '{session.Profile.Username}' (@{ShortId(session.Profile.AuthorId.Value)}) — Mesh Online ({ShortId(_transport?.SelfId.Value ?? "")}).");
            AddLog(GuiLogTheme.Auth, $"Unlocked '{session.Profile.Username}' and auto-started Iroh mesh.");

            _loginBusy = false;
            _isInMainWorkspace = true;
            if (_rootScreenHost is not null && _mainWorkspaceElement is not null)
            {
                _rootScreenHost.Content(_mainWorkspaceElement);
            }
            _uiDirty = true;
        }
        catch (Exception ex)
        {
            _loginBusy = false;
            _loginErrorText = $"Authentication failed: {ex.Message}";
            _uiDirty = true;
        }
    }

    private void ReturnToLoginScreen()
    {
        lock (_stateLock)
        {
            _activeSession?.Dispose();
            _activeSession = null;
        }
        StopMeshInternal();

        _selectedLoginAccount = null;
        _loginErrorText = string.Empty;
        _otherAccountsAnimProgress = 1.0;
        _loginMode = _localAccounts.Count > 0 ? LoginScreenMode.AccountList : LoginScreenMode.CreateAccount;
        _isInMainWorkspace = false;

        if (_rootScreenHost is not null && _loginScreenElement is not null)
        {
            _rootScreenHost.Content(_loginScreenElement);
        }
        _uiDirty = true;
    }

    // =========================================================================
    // ESPACE PRINCIPAL À 4 COLONNES
    // =========================================================================
    private Element BuildMainWorkspaceGrid()
    {
        return new Grid()
            .Columns("72,256,*,272")
            .Rows("*")
            .Children(
                BuildServerRailColumn().Column(0),
                BuildChannelSidebarColumn().Column(1),
                BuildMainChatColumn().Column(2),
                BuildRightMembersColumn().Column(3)
            );
    }

    // 1. COLONNE GAUCHE (72px) — RAIL DES SERVEURS / GROUPES
    private Element BuildServerRailColumn()
    {
        _serverRailIconsStack = new StackPanel()
            .Vertical()
            .Spacing(10);

        var homeButton = new Button()
            .Content("W")
            .Size(48, 48)
            .Background(ColBlurple)
            .Foreground(ColTextHeader)
            .FontSize(18)
            .Bold()
            .CornerRadius(16)
            .ToolTip("Whispernet Home (#general)")
            .OnClick(() => SwitchActiveGroup("general", "General Mesh"));

        var divider = new Border()
            .Height(2)
            .Width(32)
            .Background(ColChannelActive)
            .CenterHorizontal();

        var addGroupButton = new Button()
            .Content("+")
            .Size(48, 48)
            .Background(ColChatBg)
            .Foreground(ColGreen)
            .FontSize(22)
            .Bold()
            .CornerRadius(24)
            .ToolTip("Create or Join a Governed Group")
            .OnClick(() =>
            {
                SetStatus("Enter a group title or whispernet://invite/... link in the sidebar to create or join.");
            });

        var railStack = new StackPanel()
            .Vertical()
            .Spacing(10)
            .Padding(12, 14)
            .Children(
                homeButton.CenterHorizontal(),
                divider,
                _serverRailIconsStack,
                addGroupButton.CenterHorizontal()
            );

        return new Border()
            .Background(ColServerRail)
            .Child(
                new ScrollViewer()
                    .AutoVerticalScroll()
                    .NoHorizontalScroll()
                    .Content(railStack)
            );
    }

    // 2. COLONNE SALONS & GROUPES (256px)
    private Element BuildChannelSidebarColumn()
    {
        _serverHeaderTitleLabel = new Label()
            .Text(_activeGroupTitle)
            .FontSize(15)
            .Bold()
            .Foreground(ColTextHeader);

        _serverHeaderRoleLabel = new Label()
            .Text("[MESH ROOM]")
            .FontSize(11)
            .Bold()
            .Foreground(ColBlurple);

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
        _textChannelsListStack = new StackPanel().Vertical().Spacing(4);
        _tabChatBtn = CreateChannelTabButton("# general", GuiChannelTab.GeneralChat);
        _tabGovLogBtn = CreateChannelTabButton("# governance-log", GuiChannelTab.GovernanceLog);
        _tabTxLogsBtn = CreateChannelTabButton("# transaction-logs", GuiChannelTab.TransactionLogs);

        _newChannelNameBox = new TextBox()
            .Placeholder("#channel-name")
            .Background(ColServerRail)
            .Foreground(ColTextHeader)
            .FontSize(12);

        _channelAdminCard = new Border()
            .Background(ColCardBg)
            .CornerRadius(8)
            .Padding(8)
            .Child(
                new StackPanel()
                    .Vertical()
                    .Spacing(5)
                    .Children(
                        new Label().Text("ADMIN CHANNELS").FontSize(10).Bold().Foreground(ColTextMuted),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(6)
                            .Children(
                                _newChannelNameBox.Width(130),
                                new Button()
                                    .Content("+ Add")
                                    .Background(ColBlurple)
                                    .Foreground(ColTextHeader)
                                    .FontSize(11)
                                    .Bold()
                                    .CornerRadius(5)
                                    .Padding(8, 4)
                                    .OnClick(() => _ = HandleGuiChannelCreateAsync())
                            )
                    )
            );

        // Carte Groupe (Create / Join / Invite)
        _groupTitleOrInviteBox = new TextBox()
            .Placeholder("Group title or whispernet://invite/...")
            .Background(ColServerRail)
            .Foreground(ColTextHeader)
            .FontSize(12);

        _groupPassBox = new PasswordBox()
            .Placeholder("Group password (optional)")
            .Background(ColServerRail)
            .Foreground(ColTextHeader)
            .FontSize(12);

        _preApproveToggleBtn = new Button()
            .Content("Pre-Approve: OFF")
            .Background(ColButtonGray)
            .Foreground(ColTextBody)
            .FontSize(11)
            .CornerRadius(5)
            .Padding(8, 4)
            .OnClick(() =>
            {
                _preApproveInvite = !_preApproveInvite;
                _uiDirty = true;
            });

        var groupCard = new Border()
            .Background(ColCardBg)
            .CornerRadius(8)
            .Padding(10)
            .Child(
                new StackPanel()
                    .Vertical()
                    .Spacing(6)
                    .Children(
                        new Label().Text("GOVERNED GROUPS & INVITES").FontSize(11).Bold().Foreground(ColTextMuted),
                        _groupTitleOrInviteBox,
                        _groupPassBox,
                        new StackPanel()
                            .Horizontal()
                            .Spacing(6)
                            .Children(
                                new Button()
                                    .Content("Create")
                                    .Background(ColBlurple)
                                    .Foreground(ColTextHeader)
                                    .FontSize(11)
                                    .Bold()
                                    .CornerRadius(5)
                                    .Padding(8, 4)
                                    .OnClick(() => _ = HandleGuiGroupCreateAsync()),
                                new Button()
                                    .Content("Join Link")
                                    .Background(ColGreen)
                                    .Foreground(ColTextHeader)
                                    .FontSize(11)
                                    .Bold()
                                    .CornerRadius(5)
                                    .Padding(8, 4)
                                    .OnClick(() => _ = HandleGuiGroupJoinAsync())
                            ),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(6)
                            .Children(
                                new Button()
                                    .Content("Copy Invite")
                                    .Background(ColBlurple)
                                    .Foreground(ColTextHeader)
                                    .FontSize(11)
                                    .Bold()
                                    .CornerRadius(5)
                                    .Padding(8, 4)
                                    .OnClick(() => _ = HandleGuiCopyInviteAsync()),
                                _preApproveToggleBtn
                            )
                    )
            );

        var middleScroll = new ScrollViewer()
            .AutoVerticalScroll()
            .NoHorizontalScroll()
            .Content(
                new StackPanel()
                    .Vertical()
                    .Spacing(10)
                    .Padding(10, 12)
                    .Children(
                        new Label().Text("TEXT CHANNELS").FontSize(11).Bold().Foreground(ColTextMuted),
                        _textChannelsListStack,
                        _channelAdminCard,
                        new Label().Text("DIAGNOSTICS & LOGS").FontSize(11).Bold().Foreground(ColTextMuted),
                        _tabGovLogBtn,
                        _tabTxLogsBtn,
                        groupCard
                    )
            );

        return new Border()
            .Background(ColSidebar)
            .Child(
                new DockPanel()
                    .Children(topServerHeader, bottomUserBar, middleScroll)
            );
    }

    private Button CreateChannelTabButton(string text, GuiChannelTab tab)
    {
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

        var sendBtn = new Button()
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
                            .Children(sendBtn, _chatInputBox)
                    )
            )
            .DockBottom();

        _feedItemsStack = new StackPanel()
            .Vertical()
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
    private Element BuildRightMembersColumn()
    {
        var rightHeader = new Border()
            .Background(ColSidebar)
            .BorderBrush(ColDivider)
            .BorderThickness(1)
            .Padding(14, 12)
            .Child(
                new Label()
                    .Text("MEMBERS & MESH PEERS")
                    .FontSize(13)
                    .Bold()
                    .Foreground(ColTextHeader)
            )
            .DockTop();

        _pendingListStack = new StackPanel().Vertical().Spacing(6);
        _membersListStack = new StackPanel().Vertical().Spacing(6);
        _peersListStack = new StackPanel().Vertical().Spacing(6);

        var rightScroll = new ScrollViewer()
            .AutoVerticalScroll()
            .NoHorizontalScroll()
            .Content(
                new StackPanel()
                    .Vertical()
                    .Spacing(14)
                    .Padding(12, 12)
                    .Children(
                        new Label().Text("PENDING JOIN REQUESTS").FontSize(11).Bold().Foreground(ColAmber),
                        _pendingListStack,
                        new Label().Text("GROUP MEMBERS (CRDT)").FontSize(11).Bold().Foreground(ColTextMuted),
                        _membersListStack,
                        new Label().Text("ONLINE IROH PEERS (< 15M)").FontSize(11).Bold().Foreground(ColGreen),
                        _peersListStack
                    )
            );

        return new Border()
            .Background(ColSidebar)
            .Child(
                new DockPanel()
                    .Children(rightHeader, rightScroll)
            );
    }

    // =========================================================================
    // RAFRAÎCHISSEMENT DYNAMIQUE DE L'ESPACE PRINCIPAL
    // =========================================================================
    private void RefreshDynamicPanels()
    {
        string activeGid;
        string activeTitle;
        string activeChId;
        string activeChName;
        AccountSession? session;
        bool meshOnline;
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
        if (_serverRailIconsStack is not null)
        {
            _serverRailIconsStack.Clear();
            foreach (GroupGovernanceState g in groups)
            {
                string gid = g.GroupId.Value;
                string title = g.CurrentManifest.Title;
                bool isSelected = gid.Equals(activeGid, StringComparison.Ordinal);
                string acronym = BuildAcronym(title);

                var btn = new Button()
                    .Content(acronym)
                    .Size(48, 48)
                    .Background(isSelected ? ColBlurple : ColChatBg)
                    .Foreground(ColTextHeader)
                    .FontSize(14)
                    .Bold()
                    .CornerRadius(isSelected ? 14 : 24)
                    .ToolTip($"{title} (#{ShortId(gid)})")
                    .OnClick(() => SwitchActiveGroup(gid, title));

                _serverRailIconsStack.Add(btn.CenterHorizontal());
            }
        }

        // 2. En-tête du serveur et barre utilisateur
        _serverHeaderTitleLabel?.Text(activeTitle);
        string roleText = "[MESH ROOM]";
        if (activeGov is not null)
        {
            if (activeGov.CurrentManifest.IsTerminated)
            {
                roleText = "[TERMINATED]";
            }
            else if (session is not null)
            {
                AuthorId myId = session.Profile.AuthorId;
                bool isMod = activeGov.ActiveModerators.ContainsKey(myId) ||
                             activeGov.CurrentManifest.ModeratorPublicKeysSpkiBase64.ContainsKey(myId);
                MemberStatus st = activeGov.GetMemberStatus(myId);
                roleText = isMod ? $"[MODERATOR · {st}]" : $"[MEMBER · {st}]";
            }
            else
            {
                roleText = $"[GOVERNED · #{ShortId(activeGid)}]";
            }
        }
        _serverHeaderRoleLabel?.Text(roleText);

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
            _meshStatusLabel?.Text($"● {ShortId(selfNodeId.Value.Value)}").Foreground(ColGreen);
            _meshToggleBtn?.Content("Stop").Background(ColRed);
        }
        else
        {
            _meshStatusLabel?.Text("● Offline").Foreground(ColTextMuted);
            _meshToggleBtn?.Content("Start").Background(ColGreen);
        }

        _preApproveToggleBtn?.Content(_preApproveInvite ? "Pre-Approve: ON" : "Pre-Approve: OFF")
            .Background(_preApproveInvite ? ColGreen : ColButtonGray);

        // Onglets de salons
        bool isGroupAdmin = activeGov is not null && session is not null &&
            !activeGov.CurrentManifest.IsTerminated &&
            activeGov.ActiveModerators.ContainsKey(session.Profile.AuthorId);

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
                        .Background(isChSelected ? ColChannelActive : ColChannelInactive)
                        .Foreground(isChSelected ? ColTextHeader : ColTextMuted)
                        .FontSize(13)
                        .Bold()
                        .CornerRadius(6)
                        .Padding(10, 6)
                        .Width(isGroupAdmin && channels.Count > 1 ? 168 : 200)
                        .ToolTip($"#{chNameVal} (id={chIdVal})")
                        .OnClick(() => SwitchActiveChannel(chIdVal, chNameVal));

                    if (isGroupAdmin && channels.Count > 1)
                    {
                        var rmBtn = new Button()
                            .Content("×")
                            .Background(ColRed)
                            .Foreground(ColTextHeader)
                            .FontSize(11)
                            .Bold()
                            .CornerRadius(5)
                            .Padding(6, 4)
                            .ToolTip($"Remove #{chNameVal}")
                            .OnClick(() => _ = ExecuteSlashCommandAsync($"channel remove {chIdVal}"));

                        _textChannelsListStack.Add(
                            new StackPanel()
                                .Horizontal()
                                .Spacing(4)
                                .Children(chBtn, rmBtn)
                        );
                    }
                    else
                    {
                        _textChannelsListStack.Add(chBtn);
                    }
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

        if (_channelAdminCard is not null)
        {
            _channelAdminCard.IsVisible = isGroupAdmin;
        }

        _tabGovLogBtn?.Background(_activeTab == GuiChannelTab.GovernanceLog ? ColChannelActive : ColChannelInactive)
            .Foreground(_activeTab == GuiChannelTab.GovernanceLog ? ColTextHeader : ColTextMuted);
        _tabTxLogsBtn?.Background(_activeTab == GuiChannelTab.TransactionLogs ? ColChannelActive : ColChannelInactive)
            .Foreground(_activeTab == GuiChannelTab.TransactionLogs ? ColTextHeader : ColTextMuted);

        // 3. Barre supérieure du salon et flux principal
        _statusBannerLabel?.Text(_statusBanner);
        ulong maxClock = activeDag?.MaxLogicalClock ?? 0;
        int headCount = activeDag?.HeadIds.Count ?? 0;

        if (_activeTab == GuiChannelTab.GeneralChat)
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

        // 4. Colonne droite : Demandes d'admission, Membres CRDT, Pairs Iroh
        RebuildRightSidebar(activeGov, session, recentPeers);
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

        foreach (GuiChatMessage msg in messages)
        {
            Color avatarColor = PickAvatarColor(msg.AuthorName);
            string initials = BuildAcronym(msg.AuthorName);
            string timeText = msg.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

            var msgCard = new Border()
                .Background(msg.IsSystem ? ColSidebar : ColChatBg)
                .CornerRadius(6)
                .Padding(8, 6)
                .Child(
                    new StackPanel()
                        .Horizontal()
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
                                .Top(),
                            new StackPanel()
                                .Vertical()
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

    private void RebuildRightSidebar(
        GroupGovernanceState? activeGov,
        AccountSession? session,
        IReadOnlyList<GuiPeerStatus> recentPeers)
    {
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
                _pendingListStack.Add(new Label().Text("Not a governed group.").FontSize(12).Foreground(ColTextMuted));
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

                foreach ((AuthorId memberId, _) in activeGov.MemberStates)
                {
                    MemberStatus st = activeGov.GetMemberStatus(memberId);
                    string displayName;
                    lock (_stateLock)
                    {
                        displayName = _knownAuthorNames.TryGetValue(memberId, out string? n) && !string.IsNullOrWhiteSpace(n)
                            ? n
                            : $"@{ShortId(memberId.Value)}";
                    }

                    Color stColor = st switch
                    {
                        MemberStatus.Admitted => ColGreen,
                        MemberStatus.Silenced => ColAmber,
                        MemberStatus.Banned => ColRed,
                        _ => ColTextMuted
                    };

                    var memberBox = new StackPanel()
                        .Vertical()
                        .Spacing(4)
                        .Children(
                            new StackPanel()
                                .Horizontal()
                                .Spacing(6)
                                .Children(
                                    new Label().Text("●").FontSize(12).Foreground(stColor),
                                    new Label().Text(displayName).FontSize(12).Bold().Foreground(ColTextHeader),
                                    new Label().Text($"[{st}]").FontSize(11).Foreground(stColor)
                                )
                        );

                    if (canModerate && session!.Profile.AuthorId != memberId)
                    {
                        string targetHex = memberId.Value;
                        memberBox.Add(
                            new StackPanel()
                                .Horizontal()
                                .Spacing(4)
                                .Children(
                                    new Button()
                                        .Content("Admit")
                                        .Background(ColGreen)
                                        .Foreground(ColTextHeader)
                                        .FontSize(10)
                                        .CornerRadius(4)
                                        .Padding(6, 2)
                                        .OnClick(() => _ = ExecuteSlashCommandAsync($"mod admit {targetHex}")),
                                    new Button()
                                        .Content("Silence")
                                        .Background(ColAmber)
                                        .Foreground(ColTextHeader)
                                        .FontSize(10)
                                        .CornerRadius(4)
                                        .Padding(6, 2)
                                        .OnClick(() => _ = ExecuteSlashCommandAsync($"mod silence {targetHex}")),
                                    new Button()
                                        .Content("Ban")
                                        .Background(ColRed)
                                        .Foreground(ColTextHeader)
                                        .FontSize(10)
                                        .CornerRadius(4)
                                        .Padding(6, 2)
                                        .OnClick(() => _ = ExecuteSlashCommandAsync($"mod ban {targetHex}"))
                                )
                        );
                    }

                    _membersListStack.Add(
                        new Border()
                            .Background(ColCardBg)
                            .CornerRadius(6)
                            .Padding(8, 6)
                            .Child(memberBox)
                    );
                }
            }
            else
            {
                _membersListStack.Add(new Label().Text("Public mesh room.").FontSize(12).Foreground(ColTextMuted));
            }
        }

        if (_peersListStack is not null)
        {
            _peersListStack.Clear();
            if (recentPeers.Count == 0)
            {
                _peersListStack.Add(new Label().Text("No peer seen < 15m.").FontSize(12).Foreground(ColTextMuted));
            }
            else
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach (GuiPeerStatus peer in recentPeers)
                {
                    int ageSec = Math.Max(0, (int)(now - peer.LastSeenUtc).TotalSeconds);
                    string ageLabel = ageSec < 60 ? $"{ageSec}s" : $"{ageSec / 60}m";
                    string roleBadge = peer.Role == NodeRole.MeetingPoint ? "MP" : "PEER";

                    _peersListStack.Add(
                        new Border()
                            .Background(ColCardBg)
                            .CornerRadius(6)
                            .Padding(8, 5)
                            .Child(
                                new StackPanel()
                                    .Horizontal()
                                    .Spacing(6)
                                    .Children(
                                        new Label().Text("●").FontSize(11).Foreground(ColGreen),
                                        new Label().Text($"[{roleBadge}] {ShortId(peer.NodeId.Value)}").FontSize(12).Bold().Foreground(ColTextBody),
                                        new Label().Text(ageLabel).FontSize(11).Foreground(ColTextMuted)
                                    )
                            )
                    );
                }
            }
        }
    }

    // =========================================================================
    // ACTIONS UTILISATEUR & COMMANDES P2P
    // =========================================================================
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

    private async Task HandleGuiChannelCreateAsync()
    {
        if (_newChannelNameBox is null)
        {
            return;
        }

        string rawName = _newChannelNameBox.Text?.Trim() ?? string.Empty;
        _newChannelNameBox.Text(string.Empty);
        if (string.IsNullOrWhiteSpace(rawName))
        {
            SetStatus("Enter a channel name (e.g. #announcements) first.");
            return;
        }

        await ExecuteSlashCommandAsync($"channel create {rawName}").ConfigureAwait(false);
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
        else
        {
            await SendChatMessageInActiveGroupAsync(text).ConfigureAwait(false);
        }
    }

    private async Task HandleGuiGroupCreateAsync()
    {
        string title = _groupTitleOrInviteBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            SetStatus("Enter a group title in the Group box first.");
            return;
        }

        string pass = _groupPassBox?.Password ?? string.Empty;
        string cmd = string.IsNullOrWhiteSpace(pass)
            ? $"group create \"{title}\""
            : $"group create \"{title}\" {pass}";
        await ExecuteSlashCommandAsync(cmd).ConfigureAwait(false);
    }

    private async Task HandleGuiGroupJoinAsync()
    {
        string link = _groupTitleOrInviteBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(link))
        {
            SetStatus("Paste a whispernet://invite/... link in the Group box first.");
            return;
        }

        string pass = _groupPassBox?.Password ?? string.Empty;
        string cmd = string.IsNullOrWhiteSpace(pass)
            ? $"group join {link}"
            : $"group join {link} {pass}";
        await ExecuteSlashCommandAsync(cmd).ConfigureAwait(false);
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
    public void RegisterGovernedGroup(GroupGovernanceState govState)
    {
        List<NodeId>? fallbackPeers = null;
        lock (_stateLock)
        {
            string key = govState.GroupId.Value;
            _governedGroups[key] = govState;
            _groupTitles[key] = govState.CurrentManifest.Title;
            _activeGroupId = key;
            _activeGroupTitle = govState.CurrentManifest.Title;

            IReadOnlyList<GroupChannel> channels = govState.CurrentManifest.GetEffectiveChannels();
            GroupChannel activeCh = channels.FirstOrDefault(c => c.ChannelId.Value.Equals(_activeChannelId, StringComparison.OrdinalIgnoreCase))
                ?? channels[0];
            _activeChannelId = activeCh.ChannelId.Value;
            _activeChannelName = activeCh.Name;

            foreach (GroupChannel ch in channels)
            {
                if (_roomDags.TryGetValue(ch.ChannelId.Value, out ChatRoomDagState? existingDag))
                {
                    RebuildRoomMessagesLocked(key, existingDag);
                }
            }
            if (govState.KnownAuthorNodes.Count <= 1)
            {
                fallbackPeers = _peers.Keys.ToList();
            }
        }

        if (_directMesh is not null && _transport is not null)
        {
            _directMesh.RegisterGroup(govState, fallbackPeers);
            TriggerBackgroundGroupSync(govState);
        }
        _uiDirty = true;
    }

    private void TriggerBackgroundGroupSync(GroupGovernanceState govState)
    {
        GroupMeshService? mesh = _directMesh;
        IrohEndpointAdapter? transport = _transport;
        if (mesh is null || transport is null || govState.CurrentManifest.IsTerminated)
        {
            return;
        }

        IReadOnlyList<NodeId> targets = govState.GetRoomMemberEndpoints(transport.SelfId);
        if (targets.Count == 0)
        {
            return;
        }

        IReadOnlyList<GroupChannel> channels = govState.CurrentManifest.GetEffectiveChannels();
        var channelDags = new List<(ChannelId ChannelId, ChatRoomDagState Dag)>(channels.Count);
        lock (_stateLock)
        {
            foreach (GroupChannel ch in channels)
            {
                ChatRoomDagState dag = GetOrCreateRoomDagLocked(ch.ChannelId.Value, ch.ChannelId.ToBytes());
                channelDags.Add((ch.ChannelId, dag));
            }
        }

        foreach (NodeId target in targets)
        {
            _ = Task.Run(async () =>
            {
                foreach ((ChannelId chId, ChatRoomDagState localDag) in channelDags)
                {
                    try
                    {
                        await mesh.SendDagSyncRequestAsync(target, govState.GroupId, localDag, channelId: chId).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            });
        }
    }

    public bool IngestDagEvent(string groupId, string groupTitle, ChatEvent evt, string authorName)
    {
        lock (_stateLock)
        {
            _groupTitles[groupId] = groupTitle;
            AuthorId authorId = evt.GetAuthorId();
            _knownAuthorNames[authorId] = authorName;

            string chKey = evt.EffectiveChannelIdHex;
            if (_governedGroups.TryGetValue(groupId, out GroupGovernanceState? govState) &&
                !govState.CurrentManifest.HasChannel(new ChannelId(chKey)))
            {
                AddLog(GuiLogTheme.Chat, $"Ignored ChatEvent {evt.EventIdHex[..8]} for unknown/removed channel {ShortId(chKey)}.");
                return false;
            }

            ChatRoomDagState dag = GetOrCreateRoomDagLocked(chKey, evt.EffectiveChannelId);
            if (!_chatEventService.TryIngestEvent(dag, evt, out string? error))
            {
                AddLog(GuiLogTheme.Chat, $"Rejected ChatEvent {evt.EventIdHex[..8]}: {error}");
                return false;
            }

            RebuildRoomMessagesLocked(groupId, dag);
        }
        _uiDirty = true;
        return true;
    }

    private void RebuildRoomMessagesLocked(string groupId, ChatRoomDagState dag)
    {
        string channelKey = dag.RoomIdHex;
        _messages.RemoveAll(m =>
            m.GroupId.Equals(groupId, StringComparison.Ordinal) &&
            !m.IsSystem &&
            (string.IsNullOrEmpty(m.ChannelId) || m.ChannelId.Equals(channelKey, StringComparison.OrdinalIgnoreCase)));
        foreach (ChatEvent ordered in dag.SortedHistory)
        {
            AuthorId aId = ordered.GetAuthorId();
            string aName = _knownAuthorNames.TryGetValue(aId, out string? n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : aId.Value[..8];
            string parentsShort = ordered.Parents.Count == 0
                ? "root"
                : string.Join(',', ordered.Parents.Select(static p => Convert.ToHexStringLower(p)[..6]));
            string text = Encoding.UTF8.GetString(ordered.Payload);
            DateTimeOffset sentUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)ordered.TimestampMs);

            _messages.Add(new GuiChatMessage(
                sentUtc,
                groupId,
                aName,
                $"@{aId.Value[..6]} · L={ordered.LogicalClock} · id={ordered.EventIdHex[..6]} · p={parentsShort}",
                text,
                IsSystem: false,
                ChannelId: ordered.EffectiveChannelIdHex));
        }
    }

    private ChatRoomDagState GetOrCreateRoomDagLocked(string groupIdHex, byte[] roomIdBytes)
    {
        if (!_roomDags.TryGetValue(groupIdHex, out ChatRoomDagState? dag))
        {
            dag = new ChatRoomDagState(roomIdBytes);
            _roomDags[groupIdHex] = dag;
        }
        return dag;
    }

    private Task SendChatMessageInActiveGroupAsync(string content)
    {
        string targetGroupId = _activeGroupId;
        string targetGroupTitle = _activeGroupTitle;
        string authorName = _activeSession?.Profile.Username ?? "Anonymous";
        AuthorId? senderAuthorId = _activeSession?.Profile.AuthorId;

        var activeGroup = new GroupId(targetGroupId);
        if (_activeSession is not null && activeGroup.IsValid)
        {
            ChatEvent evt;
            string activeChannelName;
            lock (_stateLock)
            {
                if (_governedGroups.TryGetValue(targetGroupId, out GroupGovernanceState? govState))
                {
                    if (!GroupGovernanceEngine.CanAuthorPost(govState, _activeSession.Profile.AuthorId))
                    {
                        SetStatus("You are not allowed to post in this group (not admitted, silenced, or group terminated).");
                        return Task.CompletedTask;
                    }

                    IReadOnlyList<GroupChannel> channels = govState.CurrentManifest.GetEffectiveChannels();
                    GroupChannel currentCh = channels.FirstOrDefault(c => c.ChannelId.Value.Equals(_activeChannelId, StringComparison.OrdinalIgnoreCase))
                        ?? channels[0];
                    _activeChannelId = currentCh.ChannelId.Value;
                    _activeChannelName = currentCh.Name;
                }
                else if (!new ChannelId(_activeChannelId).IsValid)
                {
                    _activeChannelId = ChannelId.DefaultGeneralFor(activeGroup).Value;
                    _activeChannelName = ChannelId.DefaultGeneralName;
                }

                activeChannelName = _activeChannelName;
                byte[] roomIdBytes = Convert.FromHexString(targetGroupId);
                byte[] channelIdBytes = Convert.FromHexString(_activeChannelId);
                ChatRoomDagState dag = GetOrCreateRoomDagLocked(_activeChannelId, channelIdBytes);
                (IReadOnlyList<byte[]> parents, ulong nextClock) = CausalDagEngine.PrepareNextEventCausalContext(dag);
                evt = _chatEventService.CreateSignedEvent(
                    _activeSession,
                    roomIdBytes,
                    parents,
                    nextClock,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    content,
                    channelIdBytes);
            }

            if (!IngestDagEvent(targetGroupId, targetGroupTitle, evt, authorName))
            {
                return Task.CompletedTask;
            }

            if (_chatHistoryRepo is not null)
            {
                _ = _chatHistoryRepo.UpsertEventAsync(evt, authorName);
            }

            AddLog(GuiLogTheme.Chat, $"Signed ChatEvent L={evt.LogicalClock} in '{targetGroupTitle}'/#{activeChannelName}: {content}");

            GroupMeshService? mesh = _directMesh;
            if (mesh is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        IReadOnlyDictionary<NodeId, bool> results = await mesh
                            .BroadcastChatEventAsync(activeGroup, evt)
                            .ConfigureAwait(false);

                        foreach (KeyValuePair<NodeId, bool> kv in results)
                        {
                            if (kv.Value)
                            {
                                RecordPeerSeen(kv.Key);
                            }
                            AddLog(GuiLogTheme.Mesh, $"Sent ChatEvent to {ShortId(kv.Key.Value)} -> {(kv.Value ? "OK" : "FAILED")}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog(GuiLogTheme.Mesh, $"Send failed: {ex.Message}");
                        SetStatus($"Send failed: {ex.Message}");
                    }
                });
            }
            return Task.CompletedTask;
        }

        AddSystemChatMessage(targetGroupId, authorName, content);
        GroupMeshService? fallbackMesh = _directMesh;
        if (fallbackMesh is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    IReadOnlyDictionary<NodeId, bool> results = await fallbackMesh
                        .SendMessageAsync(targetGroupId, authorName, content, senderAuthorId: senderAuthorId)
                        .ConfigureAwait(false);

                    foreach (KeyValuePair<NodeId, bool> kv in results)
                    {
                        if (kv.Value)
                        {
                            RecordPeerSeen(kv.Key);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddLog(GuiLogTheme.Mesh, $"Send failed: {ex.Message}");
                }
            });
        }
        return Task.CompletedTask;
    }

    public async Task ExecuteSlashCommandAsync(string rawCommand)
    {
        try
        {
            List<string> tokens = SplitArgs(rawCommand);
            if (tokens.Count == 0)
            {
                return;
            }

            string cmd = tokens[0].ToLowerInvariant();
            switch (cmd)
            {
                case "account":
                    await HandleAccountCommandAsync(tokens).ConfigureAwait(false);
                    break;
                case "group":
                    await HandleGroupCommandAsync(tokens).ConfigureAwait(false);
                    break;
                case "channel":
                    await HandleChannelCommandAsync(tokens).ConfigureAwait(false);
                    break;
                case "mod":
                    await HandleModCommandAsync(tokens).ConfigureAwait(false);
                    break;
                case "mesh":
                    await HandleMeshCommandAsync(tokens).ConfigureAwait(false);
                    break;
                default:
                    SetStatus($"Unknown command '/{cmd}'. Supported: /account, /group, /channel, /mod, /mesh");
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            AddLog(GuiLogTheme.Group, $"ERROR ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            _uiDirty = true;
        }
    }

    private async Task HandleAccountCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            SetStatus("Usage: /account <create|unlock|lock|list>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "create" && tokens.Count >= 3)
        {
            string username = tokens[2];
            string? password = tokens.Count >= 4 && !tokens[3].Equals("--no-password", StringComparison.OrdinalIgnoreCase)
                ? tokens[3]
                : null;
            AccountProfile profile = await _accountService.CreateAccountAsync(username, password).ConfigureAwait(false);
            await RefreshAccountsListAsync().ConfigureAwait(false);
            SetStatus($"Created account '{profile.Username}' (@{ShortId(profile.AuthorId.Value)}).");
            AddLog(GuiLogTheme.Auth, $"Created account '{profile.Username}' ({profile.AuthorId}).");
        }
        else if (sub == "unlock")
        {
            IReadOnlyList<AccountProfile> accounts = await _accountService.ListAccountsAsync().ConfigureAwait(false);
            if (accounts.Count == 0)
            {
                SetStatus("No local accounts found. Create one first.");
                return;
            }

            string? query = tokens.Count >= 3 ? tokens[2] : null;
            string? password = tokens.Count >= 4 ? tokens[3] : null;

            AccountProfile? target = string.IsNullOrWhiteSpace(query)
                ? accounts.FirstOrDefault(static a => !a.IsPasswordProtected)
                : accounts.FirstOrDefault(a =>
                    a.Username.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                    a.AuthorId.Value.StartsWith(query, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                SetStatus($"No account found matching '{query}'.");
                return;
            }

            await UnlockAndEnterWorkspaceAsync(target, password).ConfigureAwait(false);
        }
        else if (sub == "lock")
        {
            ReturnToLoginScreen();
        }
    }

    private async Task HandleGroupCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            SetStatus("Usage: /group <create <title> [pwd] | invite [--preapprove] | join <link> [pwd] | terminate>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "create" && tokens.Count >= 3)
        {
            if (_activeSession is null)
            {
                SetStatus("Unlock an account first before creating a group.");
                return;
            }

            string title = tokens[2];
            string? pwd = tokens.Count >= 4 ? tokens[3] : null;
            GroupGovernanceState govState = await _govService.CreateGroupAsync(
                _activeSession, title, pwd, creatorNodeId: _transport?.SelfId).ConfigureAwait(false);
            RegisterGovernedGroup(govState);
            SetStatus($"Created governed group '{title}' (#{ShortId(govState.GroupId.Value)}).");
            AddLog(GuiLogTheme.Group, $"Created governed group '{title}' ({govState.GroupId}).");
        }
        else if (sub == "invite")
        {
            if (_activeSession is null || _transport is null)
            {
                SetStatus("Unlock an account and start the mesh first so invitees have your NodeId.");
                return;
            }

            if (!_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? govState))
            {
                SetStatus("Select or create a governed group first.");
                return;
            }

            bool preApprove = tokens.Any(static t => t.Equals("--preapprove", StringComparison.OrdinalIgnoreCase));
            string link = _govService.CreateInvitationLink(_activeSession, govState, _transport.SelfId, preApprove);
            _lastInviteLink = link;
            bool copied = TryCopyToClipboard(link);
            _groupTitleOrInviteBox?.Text(link);
            SetStatus(copied
                ? $"Invite link copied to clipboard (PreApprove={preApprove})!"
                : $"Invite link generated in Group box (PreApprove={preApprove}).");
            AddSystemChatMessage(_activeGroupId, "INVITE", link);
        }
        else if (sub == "join" && tokens.Count >= 3)
        {
            if (_activeSession is null || _transport is null || _directMesh is null)
            {
                SetStatus("Unlock an account and start the mesh before joining a group.");
                return;
            }

            GroupInvitationData parsed = GroupGovernanceService.ParseInvitationLink(tokens[2]);
            string? pwd = tokens.Count >= 4 ? tokens[3] : null;
            RecordPeerSeen(parsed.BootstrapNodeId);

            JoinApprovalRequest req = _govService.CreateJoinApprovalRequest(
                _activeSession, parsed.GroupId, _transport.SelfId, pwd, parsed.PreApprovalTicket);
            await _directMesh.SendJoinRequestAsync(parsed.BootstrapNodeId, req).ConfigureAwait(false);
            SetStatus($"Sent join request for '{parsed.Title}' to {ShortId(parsed.BootstrapNodeId.Value)}...");
            AddLog(GuiLogTheme.Group, $"Sent JoinApprovalRequest to {parsed.BootstrapNodeId}.");
        }
        else if (sub == "terminate")
        {
            if (_activeSession is null || !_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? govState))
            {
                SetStatus("Select a governed group and unlock the owner account first.");
                return;
            }

            await _govService.TerminateGroupAsync(_activeSession, govState).ConfigureAwait(false);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(govState);
                await _directMesh.BroadcastSyncPayloadAsync(govState.GroupId).ConfigureAwait(false);
            }
            SetStatus($"Group '{govState.CurrentManifest.Title}' permanently terminated.");
        }
        else if (sub == "channel")
        {
            await HandleChannelCommandAsync(tokens.Skip(1).ToList()).ConfigureAwait(false);
        }
    }

    private async Task HandleChannelCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            SetStatus("Usage: /channel <create <name> | remove <name|id> | use <name|id> | list>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        string gid;
        GroupGovernanceState? govState;
        lock (_stateLock)
        {
            gid = _activeGroupId;
            _governedGroups.TryGetValue(gid, out govState);
        }

        if (govState is null)
        {
            SetStatus("Select or create a governed group first.");
            return;
        }

        if (sub == "list")
        {
            IReadOnlyList<GroupChannel> channels = govState.CurrentManifest.GetEffectiveChannels();
            SetStatus($"Channels in '{govState.CurrentManifest.Title}': {string.Join(", ", channels.Select(static c => $"#{c.Name}"))}");
            return;
        }

        if (sub is "use" or "switch" && tokens.Count >= 3)
        {
            GroupChannel? target = govState.CurrentManifest.FindChannel(tokens[2]);
            if (target is null)
            {
                SetStatus($"Channel '{tokens[2]}' not found in '{govState.CurrentManifest.Title}'.");
                return;
            }

            SwitchActiveChannel(target.ChannelId.Value, target.Name);
            return;
        }

        if (sub is "create" or "add" && tokens.Count >= 3)
        {
            if (_activeSession is null)
            {
                SetStatus("Unlock the group administrator account first.");
                return;
            }

            string rawName = tokens[2];
            GroupChannel created = await _govService.CreateChannelAsync(_activeSession, govState, rawName).ConfigureAwait(false);
            RegisterGovernedGroup(govState);
            SwitchActiveChannel(created.ChannelId.Value, created.Name);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(govState);
                await _directMesh.BroadcastSyncPayloadAsync(govState.GroupId).ConfigureAwait(false);
            }
            SetStatus($"Created channel #{created.Name} (id={ShortId(created.ChannelId.Value)}) in '{govState.CurrentManifest.Title}'.");
            AddLog(GuiLogTheme.Group, $"Created channel #{created.Name} ({created.ChannelId.Value}) in '{govState.CurrentManifest.Title}'.");
            return;
        }

        if (sub is "remove" or "delete" or "rm" && tokens.Count >= 3)
        {
            if (_activeSession is null)
            {
                SetStatus("Unlock the group administrator account first.");
                return;
            }

            GroupChannel removed = await _govService.RemoveChannelAsync(_activeSession, govState, tokens[2]).ConfigureAwait(false);
            RegisterGovernedGroup(govState);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(govState);
                await _directMesh.BroadcastSyncPayloadAsync(govState.GroupId).ConfigureAwait(false);
            }
            SetStatus($"Removed channel #{removed.Name} from '{govState.CurrentManifest.Title}'.");
            AddLog(GuiLogTheme.Group, $"Removed channel #{removed.Name} ({removed.ChannelId.Value}) from '{govState.CurrentManifest.Title}'.");
            return;
        }
    }

    private async Task HandleModCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2 || !_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? govState))
        {
            SetStatus("Select a governed group first.");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "compact")
        {
            ulong window = tokens.Count >= 3 && ulong.TryParse(tokens[2], out ulong w) ? w : 50;
            int removed = GroupGovernanceEngine.CompactGovernanceLog(govState, window);
            if (removed > 0 && _activeSession is not null)
            {
                await _govService.SaveGroupToVaultAsync(_activeSession, govState).ConfigureAwait(false);
            }
            SetStatus($"CompactGovernanceLog(window={window}): removed {removed} obsolete entry(ies), {govState.GovernanceLog.Count} remaining.");
            return;
        }

        if (sub == "ignore" && tokens.Count >= 3)
        {
            JoinApprovalRequest? req = ResolvePendingRequest(govState.GroupId, tokens[2]);
            if (req is not null)
            {
                _govService.IgnorePendingMember(govState.GroupId, req.RequesterAuthorId);
                SetStatus($"Ignored join request from '{req.RequesterUsername}'.");
            }
            return;
        }

        if (_activeSession is null)
        {
            SetStatus("Unlock a moderator account first.");
            return;
        }

        if (sub == "accept" && tokens.Count >= 3)
        {
            JoinApprovalRequest? req = ResolvePendingRequest(govState.GroupId, tokens[2]);
            if (req is null)
            {
                SetStatus($"No pending request matching '{tokens[2]}'.");
                return;
            }

            _govService.AcceptPendingMember(_activeSession, govState, req.RequesterAuthorId);
            await _govService.SaveGroupToVaultAsync(_activeSession, govState).ConfigureAwait(false);
            RecordPeerSeen(req.RequesterNodeId);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(govState);
                await _directMesh.BroadcastSyncPayloadAsync(govState.GroupId).ConfigureAwait(false);
            }
            SetStatus($"Accepted '{req.RequesterUsername}' into '{govState.CurrentManifest.Title}'.");
        }
        else if (sub is "silence" or "ban" or "admit" && tokens.Count >= 3)
        {
            string query = tokens[2];
            AuthorId? targetId = govState.MemberStates.Keys.FirstOrDefault(
                id => id.Value.StartsWith(query, StringComparison.OrdinalIgnoreCase));
            if (targetId is null || !govState.MemberStates.TryGetValue(targetId.Value, out ModerationAction? lastAction))
            {
                SetStatus($"No member matching '{query}'.");
                return;
            }

            MemberStatus newStatus = sub switch
            {
                "silence" => MemberStatus.Silenced,
                "ban" => MemberStatus.Banned,
                _ => MemberStatus.Admitted
            };
            string reason = tokens.Count >= 4 ? string.Join(' ', tokens.Skip(3)) : $"Updated to {newStatus}";
            _govService.SetMemberStatus(
                _activeSession, govState, targetId.Value, lastAction.TargetPublicKeySpkiBase64, newStatus, reason, lastAction.TargetLastKnownNodeId);
            await _govService.SaveGroupToVaultAsync(_activeSession, govState).ConfigureAwait(false);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(govState);
                _ = _directMesh.BroadcastSyncPayloadAsync(govState.GroupId);
            }
            SetStatus($"Updated @{ShortId(targetId.Value.Value)} → {newStatus}.");
        }
    }

    private async Task HandleMeshCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "start")
        {
            if (_transport is not null)
            {
                SetStatus($"Mesh already online (SelfId={ShortId(_transport.SelfId.Value)}).");
                return;
            }

            await EnsurePeerCacheAsync().ConfigureAwait(false);
            string? arg = tokens.Count >= 3 ? tokens[2] : null;
            string alpn = arg is not null && arg.StartsWith('/') ? arg : "/whispernet/direct/1";
            string? secretKeyB32;
            if (_activeSession is not null)
            {
                string accountKeyPath = Path.Combine(_activeSession.AccountDirectory, "iroh_node.key");
                secretKeyB32 = IrohEndpointAdapter.GenerateOrLoadSecretKeyBase32(accountKeyPath);
            }
            else
            {
                string irohKeyPath = Path.Combine(_effectiveRoot, "iroh_node.key");
                secretKeyB32 = IrohEndpointAdapter.GenerateOrLoadSecretKeyBase32(irohKeyPath);
            }

            _boundSecretKeyB32 = secretKeyB32;
            _boundAlpn = alpn;
            _transport = IrohEndpointAdapter.CreateAndBind(
                alpn: alpn,
                secretKeyBase32: secretKeyB32,
                enableRelay: true,
                onlineTimeoutMs: 3000);
            _directMesh = new GroupMeshService(_transport, _peerCache);

            _directMesh.MessageReceived += msg =>
            {
                RecordPeerSeen(msg.SenderId);
                AddSystemChatMessage(msg.ConversationId, msg.SenderName, msg.Content);
                _uiDirty = true;
            };
            _directMesh.JoinRequestReceived += req =>
            {
                RecordPeerSeen(req.RequesterNodeId);
                lock (_stateLock)
                {
                    _knownAuthorNames[req.RequesterAuthorId] = req.RequesterUsername;
                }
                if (_governedGroups.TryGetValue(req.GroupId.Value, out GroupGovernanceState? govState))
                {
                    if (_govService.TryProcessJoinApprovalRequest(
                            govState, req, out string? err, out ModerationAction? autoAdmitted, _activeSession))
                    {
                        if (autoAdmitted is not null || govState.GetMemberStatus(req.RequesterAuthorId) is MemberStatus.Admitted or MemberStatus.Silenced)
                        {
                            _directMesh.RegisterGroup(govState);
                            if (_activeSession is not null)
                            {
                                _ = _govService.SaveGroupToVaultAsync(_activeSession, govState);
                            }
                            _ = _directMesh.BroadcastSyncPayloadAsync(govState.GroupId);
                            SetStatus($"Auto-admitted & synced '{req.RequesterUsername}'.");
                        }
                        else
                        {
                            SetStatus($"New pending join request from '{req.RequesterUsername}' (see right sidebar).");
                        }
                    }
                    else
                    {
                        AddLog(GuiLogTheme.Group, $"Rejected join request from '{req.RequesterUsername}': {err}");
                    }
                }
                _uiDirty = true;
            };
            _directMesh.RosterSyncReceived += sync =>
            {
                if (_directMesh.ApplySyncPayload(sync, _accountService.VerifySignature, out string? err) &&
                    _directMesh.TryGetGroupGovernanceState(sync.Manifest.GroupId, out GroupGovernanceState? syncedState) &&
                    syncedState is not null)
                {
                    RegisterGovernedGroup(syncedState);
                    if (_activeSession is not null)
                    {
                        _ = _govService.SaveGroupToVaultAsync(_activeSession, syncedState);
                    }
                    if (sync.SenderNodeId is NodeId senderNode)
                    {
                        RecordPeerSeen(senderNode);
                        foreach (GroupChannel ch in syncedState.CurrentManifest.GetEffectiveChannels())
                        {
                            ChatRoomDagState localDag;
                            lock (_stateLock)
                            {
                                localDag = GetOrCreateRoomDagLocked(ch.ChannelId.Value, ch.ChannelId.ToBytes());
                            }
                            _ = _directMesh.SendDagSyncRequestAsync(senderNode, syncedState.GroupId, localDag, channelId: ch.ChannelId);
                        }
                    }
                    foreach (NodeId peerId in syncedState.GetRoomMemberEndpoints(_transport.SelfId))
                    {
                        RecordPeerSeen(peerId);
                    }
                    SetStatus(syncedState.CurrentManifest.IsTerminated
                        ? $"Group '{syncedState.CurrentManifest.Title}' was permanently terminated by owner."
                        : $"Synced group '{syncedState.CurrentManifest.Title}' (#{ShortId(syncedState.GroupId.Value)}).");
                }
                else
                {
                    AddLog(GuiLogTheme.Group, $"Roster sync rejected: {err}");
                }
                _uiDirty = true;
            };
            _directMesh.ModerationActionReceived += action =>
            {
                if (_governedGroups.TryGetValue(action.GroupId.Value, out GroupGovernanceState? govState) &&
                    GroupGovernanceEngine.TryApplyModerationAction(govState, action, _accountService.VerifySignature, out _, out _))
                {
                    _directMesh.RegisterGroup(govState);
                    if (_activeSession is not null)
                    {
                        _ = _govService.SaveGroupToVaultAsync(_activeSession, govState);
                    }
                    SetStatus($"Moderation update: @{ShortId(action.TargetAuthorId.Value)} → {action.NewStatus}.");
                    _uiDirty = true;
                }
            };
            _directMesh.DagSyncRequestReceived += req =>
            {
                RecordPeerSeen(req.RequesterNodeId);
                string chKey = req.ChannelId is { IsValid: true } chId
                    ? chId.Value
                    : ChannelId.DefaultGeneralFor(req.GroupId).Value;
                ChatRoomDagState localDag;
                lock (_stateLock)
                {
                    localDag = GetOrCreateRoomDagLocked(chKey, Convert.FromHexString(chKey));
                }
                _ = _directMesh.StreamTopologicalSliceAsync(req.RequesterNodeId, req.GroupId, localDag, req.KnownHeadIds, channelId: new ChannelId(chKey));
            };
            _directMesh.DagSyncBatchReceived += batch =>
            {
                if (batch.SenderNodeId is NodeId senderNode)
                {
                    RecordPeerSeen(senderNode);
                }
                if (batch.Events.Count == 0)
                {
                    return;
                }

                string gid = batch.GroupId.Value;
                string batchChKey = batch.ChannelId is { IsValid: true } bCh
                    ? bCh.Value
                    : (batch.Events[0].ChannelId is { Length: 32 } eCh
                        ? Convert.ToHexString(eCh).ToLowerInvariant()
                        : ChannelId.DefaultGeneralFor(batch.GroupId).Value);
                bool ok;
                string? err;
                List<ChatEvent> allowedEvents;
                lock (_stateLock)
                {
                    if (_governedGroups.TryGetValue(gid, out GroupGovernanceState? govState))
                    {
                        if (govState.CurrentManifest.IsTerminated ||
                            !govState.CurrentManifest.HasChannel(new ChannelId(batchChKey)))
                        {
                            return;
                        }
                        allowedEvents = batch.Events
                            .Where(e => GroupGovernanceEngine.CanAuthorPost(govState, e.GetAuthorId()))
                            .ToList();
                        if (allowedEvents.Count == 0)
                        {
                            return;
                        }
                    }
                    else
                    {
                        allowedEvents = batch.Events.ToList();
                    }

                    ChatRoomDagState dag = GetOrCreateRoomDagLocked(batchChKey, Convert.FromHexString(batchChKey));
                    ok = _chatEventService.TryIngestBatch(dag, allowedEvents, out err);
                    if (ok)
                    {
                        RebuildRoomMessagesLocked(gid, dag);
                    }
                }

                if (ok)
                {
                    if (_chatHistoryRepo is not null)
                    {
                        _ = _chatHistoryRepo.UpsertBatchAsync(allowedEvents);
                    }
                    AddLog(GuiLogTheme.Chat, $"Synced {allowedEvents.Count} ChatEvent(s) for #{ShortId(gid)}/{ShortId(batchChKey)}.");
                    _uiDirty = true;
                }
                else
                {
                    AddLog(GuiLogTheme.Chat, $"Rejected DagSyncBatch: {err}");
                }
            };

            List<NodeId> knownPeersSnapshot;
            lock (_stateLock)
            {
                knownPeersSnapshot = _peers.Keys.ToList();
            }

            _meshCts = new CancellationTokenSource();
            _transport.StartAcceptLoop(_directMesh.HandleIncomingPayload, _meshCts.Token);

            foreach (GroupGovernanceState existingGov in _governedGroups.Values)
            {
                IEnumerable<NodeId>? fallback = existingGov.KnownAuthorNodes.Count <= 1 ? knownPeersSnapshot : null;
                _directMesh.RegisterGroup(existingGov, fallback);
                TriggerBackgroundGroupSync(existingGov);
            }

            SetStatus($"Iroh mesh started — SelfId={ShortId(_transport.SelfId.Value)}");
            AddLog(GuiLogTheme.Mesh, $"Iroh endpoint bound: SelfId={_transport.SelfId}");
        }
        else if (sub == "stop")
        {
            StopMeshInternal();
            SetStatus("Iroh mesh transport stopped.");
            AddLog(GuiLogTheme.Mesh, "Stopped Iroh mesh transport.");
        }
    }

    // =========================================================================
    // PERSISTANCE SQLITE & HELPERS
    // =========================================================================
    private async Task RefreshAccountsListAsync()
    {
        try
        {
            IReadOnlyList<AccountProfile> accounts = await _accountService.ListAccountsAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                _localAccounts = accounts.ToList();
                foreach (AccountProfile a in accounts)
                {
                    _knownAuthorNames[a.AuthorId] = a.Username;
                }
            }
            _uiDirty = true;
        }
        catch
        {
        }
    }

    private async Task EnsurePeerCacheAsync()
    {
        if (_peerCache is not null)
        {
            return;
        }

        Directory.CreateDirectory(_effectiveRoot);
        string dbPath = WhispernetPaths.GetPeerCacheDbPath(_effectiveRoot);
        _peerCache = new SqlitePeerCacheRepository(dbPath);
        await _peerCache.InitializeAsync().ConfigureAwait(false);
    }

    private async Task TryHydratePeersFromSqliteAsync()
    {
        try
        {
            await EnsurePeerCacheAsync().ConfigureAwait(false);
            if (_peerCache is null)
            {
                return;
            }

            IReadOnlyList<CachedPeerEntry> cached = await _peerCache.LoadCachedPeersAsync(100).ConfigureAwait(false);
            foreach (CachedPeerEntry e in cached)
            {
                RecordPeerSeen(e.NodeId, e.Role, e.LastSeenUtc);
            }
            AddLog(GuiLogTheme.Cache, $"Loaded {cached.Count} peer(s) from SQLite cache.");
        }
        catch (Exception ex)
        {
            AddLog(GuiLogTheme.Cache, $"PeerCache load skipped: {ex.Message}");
        }
    }

    private async Task EnsureChatHistoryRepoAsync()
    {
        if (_chatHistoryRepo is not null)
        {
            return;
        }

        Directory.CreateDirectory(_effectiveRoot);
        string dbPath = WhispernetPaths.GetChatHistoryDbPath(_effectiveRoot);
        _chatHistoryRepo = new SqliteChatHistoryRepository(dbPath);
        await _chatHistoryRepo.InitializeAsync().ConfigureAwait(false);
    }

    private async Task TryHydrateChatHistoryFromSqliteAsync()
    {
        try
        {
            await EnsureChatHistoryRepoAsync().ConfigureAwait(false);
            if (_chatHistoryRepo is null)
            {
                return;
            }

            IReadOnlyList<(ChatEvent Event, string AuthorName)> saved = await _chatHistoryRepo.LoadAllEventsAsync().ConfigureAwait(false);
            if (saved.Count == 0)
            {
                return;
            }

            lock (_stateLock)
            {
                foreach (var group in saved.GroupBy(static x => x.Event.EffectiveChannelIdHex, StringComparer.Ordinal))
                {
                    foreach ((ChatEvent evt, string authorName) in group)
                    {
                        if (!string.IsNullOrWhiteSpace(authorName))
                        {
                            _knownAuthorNames[evt.GetAuthorId()] = authorName;
                        }
                    }

                    ChatEvent first = group.First().Event;
                    string roomIdHex = first.RoomIdHex;
                    ChatRoomDagState dag = GetOrCreateRoomDagLocked(group.Key, first.EffectiveChannelId);
                    if (_chatEventService.TryIngestBatch(dag, group.Select(static x => x.Event), out _))
                    {
                        RebuildRoomMessagesLocked(roomIdHex, dag);
                    }
                }
            }
            AddLog(GuiLogTheme.Cache, $"Loaded {saved.Count} ChatEvent(s) from SQLite history.");
        }
        catch (Exception ex)
        {
            AddLog(GuiLogTheme.Cache, $"ChatHistory load skipped: {ex.Message}");
        }
    }

    private JoinApprovalRequest? ResolvePendingRequest(GroupId groupId, string query)
    {
        IReadOnlyList<JoinApprovalRequest> pending = _govService.GetPendingJoinRequests(groupId);
        return pending.FirstOrDefault(r =>
            r.RequesterUsername.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            r.RequesterAuthorId.Value.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    private void RecordPeerSeen(NodeId nodeId, NodeRole role = NodeRole.Peer, DateTimeOffset? seenAtUtc = null)
    {
        lock (_stateLock)
        {
            DateTimeOffset ts = seenAtUtc ?? DateTimeOffset.UtcNow;
            if (_peers.TryGetValue(nodeId, out GuiPeerStatus? existing))
            {
                NodeRole mergedRole = existing.Role == NodeRole.MeetingPoint || role == NodeRole.MeetingPoint
                    ? NodeRole.MeetingPoint
                    : NodeRole.Peer;
                _peers[nodeId] = new GuiPeerStatus(nodeId, mergedRole, ts);
            }
            else
            {
                _peers[nodeId] = new GuiPeerStatus(nodeId, role, ts);
            }
        }
        _uiDirty = true;
    }

    private void AddSystemChatMessage(string groupId, string authorName, string content)
    {
        lock (_stateLock)
        {
            _messages.Add(new GuiChatMessage(
                DateTimeOffset.UtcNow,
                groupId,
                authorName,
                "SYSTEM",
                content,
                IsSystem: true));
        }
        _uiDirty = true;
    }

    private void AddLog(GuiLogTheme theme, string message)
    {
        lock (_stateLock)
        {
            _logs.Add(new GuiLogEntry(DateTimeOffset.UtcNow, theme, message));
        }
        _uiDirty = true;
    }

    private void SetStatus(string status)
    {
        _statusBanner = status;
        _uiDirty = true;
    }

    private void StopMeshInternal()
    {
        _meshCts?.Cancel();
        _meshCts?.Dispose();
        _meshCts = null;
        _directMesh = null;
        _transport?.Dispose();
        _transport = null;
        _uiDirty = true;
    }

    private static string ShortId(string id)
        => string.IsNullOrEmpty(id) ? "none" : (id.Length <= 8 ? id : id[..8]);

    private static string BuildAcronym(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "W";
        }

        string[] parts = title.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
        }

        string s = parts[0];
        return s.Length >= 2 ? s[..2].ToUpperInvariant() : s.ToUpperInvariant();
    }

    private static string Slugify(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "general";
        }

        var sb = new StringBuilder();
        foreach (char c in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        return sb.ToString().Trim('-');
    }

    private static Color PickAvatarColor(string name)
    {
        int hash = StringComparer.Ordinal.GetHashCode(name ?? string.Empty);
        return AvatarPalette[Math.Abs(hash) % AvatarPalette.Length];
    }

    private static bool TryCopyToClipboard(string text)
    {
        (string File, string Args)[] tools =
        {
            ("wl-copy", ""),
            ("xclip", "-selection clipboard"),
            ("xsel", "--clipboard --input")
        };

        foreach ((string file, string args) in tools)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using Process? p = Process.Start(psi);
                if (p is null)
                {
                    continue;
                }
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                if (p.WaitForExit(1500) && p.ExitCode == 0)
                {
                    return true;
                }
            }
            catch
            {
            }
        }
        return false;
    }

    private static List<string> SplitArgs(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}
