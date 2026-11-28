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
                    string badge = captured.IsPasswordProtected ? " · 🔒 Password required" : string.Empty;

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
        if (profile.IsPasswordProtected)
        {
            _uiDirty = true;
        }

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

            await _friendService.LoadFromVaultAsync(session).ConfigureAwait(false);
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

}
