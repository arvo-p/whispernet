using System.Collections.Concurrent;
using System.Text;
using Whispernet.App.Messaging;
using Whispernet.Core;
using Whispernet.Transport.Iroh;

namespace Whispernet.Client.Linux;

/// <summary>
/// Catégories (thèmes) de journaux de transactions filtrables dans l'interface ncurses.
/// </summary>
internal enum LogTheme
{
    All = 0,
    Chat = 1,
    Mesh = 2,
    Auth = 3,
    Vault = 4,
    Cache = 5,
    Group = 6
}

/// <summary>
/// Entrée horodatée du journal des transactions.
/// </summary>
internal sealed record TransactionLogEntry(
    DateTimeOffset TimestampUtc,
    LogTheme Theme,
    string Message);

/// <summary>
/// Message affiché dans la fenêtre de discussion de groupe.
/// </summary>
internal sealed record UiChatMessage(
    DateTimeOffset TimestampUtc,
    string GroupId,
    string GroupTitle,
    string AuthorName,
    string AuthorBadge,
    string Content);

/// <summary>
/// Pair suivi dans le panneau des pairs connectés (dernière activité &lt; 15 min).
/// </summary>
internal sealed record ConnectedPeerStatus(
    NodeId NodeId,
    NodeRole Role,
    DateTimeOffset LastSeenUtc);

/// <summary>
/// Interface TUI style ncurses pour Linux affichant le groupe de chat (messages, date, auteur,
/// titre du groupe), les pairs connectés (&lt; 15 min), et un écran de logs de transactions filtrable par thème.
/// </summary>
internal sealed class NcursesTui
{
    public static readonly TimeSpan ActivePeerWindow = TimeSpan.FromMinutes(15);

    private static readonly LogTheme[] FilterThemes =
    {
        LogTheme.All,
        LogTheme.Chat,
        LogTheme.Mesh,
        LogTheme.Auth,
        LogTheme.Vault,
        LogTheme.Cache,
        LogTheme.Group
    };

    private readonly string _effectiveRoot;
    private readonly IAccountService _accountService;
    private readonly ChatEventService _chatEventService;
    private readonly GroupGovernanceService _govService;
    private readonly object _stateLock = new();
    private readonly List<UiChatMessage> _messages = new();
    private readonly List<TransactionLogEntry> _logs = new();
    private readonly Dictionary<NodeId, ConnectedPeerStatus> _peers = new();
    private readonly Dictionary<string, string> _groupTitles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatRoomDagState> _roomDags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupGovernanceState> _governedGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<AuthorId, string> _knownAuthorNames = new();
    private readonly StringBuilder _inputBuffer = new();
    private readonly List<string> _inputHistory = new();

    private int _historyIndex = -1;
    private string _savedDraft = string.Empty;

    private AccountSession? _activeSession;
    private SqlitePeerCacheRepository? _peerCache;
    private IrohEndpointAdapter? _transport;
    private GroupMeshService? _directMesh;
    private CancellationTokenSource? _meshCts;

    private string _activeGroupId = "general";
    private string _activeGroupTitle = "General Group";
    private string? _lastInviteLink;
    private bool _showLogsPanel;
    private bool _showPendingPanel;
    private int _selectedPendingIndex;
    private int _themeFilterIndex;
    private int _scrollOffset;
    private bool _needsRedraw = true;
    private bool _running = true;

    public NcursesTui(string effectiveRoot, IAccountService accountService)
    {
        _effectiveRoot = effectiveRoot;
        _accountService = accountService;
        _chatEventService = new ChatEventService(accountService);
        _govService = new GroupGovernanceService(accountService);
        _groupTitles[_activeGroupId] = _activeGroupTitle;

        AddLog(LogTheme.Group, $"Initialized group '{_activeGroupTitle}' (id={_activeGroupId}).");
        AddLog(LogTheme.Auth, $"Data directory set to '{_effectiveRoot}'.");
    }

    public GroupGovernanceService GovernanceService => _govService;

    public void RegisterGovernedGroup(GroupGovernanceState govState)
    {
        lock (_stateLock)
        {
            string key = govState.GroupId.Value;
            _governedGroups[key] = govState;
            _groupTitles[key] = govState.CurrentManifest.Title;
            _activeGroupId = key;
            _activeGroupTitle = govState.CurrentManifest.Title;
            _needsRedraw = true;
        }

        _directMesh?.RegisterGroup(govState);
    }

    public bool IngestDagEvent(string groupId, string groupTitle, ChatEvent evt, string authorName)
    {
        lock (_stateLock)
        {
            _groupTitles[groupId] = groupTitle;
            AuthorId authorId = evt.GetAuthorId();
            _knownAuthorNames[authorId] = authorName;

            if (!_roomDags.TryGetValue(groupId, out ChatRoomDagState? dag))
            {
                dag = new ChatRoomDagState(evt.RoomId);
                _roomDags[groupId] = dag;
            }

            if (!_chatEventService.TryIngestEvent(dag, evt, out string? error))
            {
                AddLog(LogTheme.Chat, $"Rejected ChatEvent {evt.EventIdHex[..8]}: {error}");
                return false;
            }

            // Reconstruire les messages du groupe dans l'ordre exact de SortedHistory (parents -> logical_clock -> timestamp_ms)
            _messages.RemoveAll(m => m.GroupId.Equals(groupId, StringComparison.Ordinal));
            foreach (ChatEvent ordered in dag.SortedHistory)
            {
                AuthorId aId = ordered.GetAuthorId();
                string aName = _knownAuthorNames.TryGetValue(aId, out string? n) ? n : "Peer";
                string parentsShort = ordered.Parents.Count == 0
                    ? "root"
                    : string.Join(',', ordered.Parents.Select(p => Convert.ToHexStringLower(p)[..6]));
                string text = Encoding.UTF8.GetString(ordered.Payload);
                DateTimeOffset sentUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)ordered.TimestampMs);

                _messages.Add(new UiChatMessage(
                    sentUtc,
                    groupId,
                    groupTitle,
                    aName,
                    $"@{aId.Value[..6]} [L={ordered.LogicalClock} id={ordered.EventIdHex[..6]} p={parentsShort}]",
                    text));
            }

            _needsRedraw = true;
        }

        AddLog(LogTheme.Chat,
            $"DAG Ingested event={evt.EventIdHex[..8]} clock={evt.LogicalClock} parents={evt.Parents.Count} by {authorName}");
        return true;
    }

    public void AddLog(LogTheme theme, string message)
    {
        lock (_stateLock)
        {
            _logs.Add(new TransactionLogEntry(DateTimeOffset.UtcNow, theme, message));
            if (_logs.Count > 1000)
            {
                _logs.RemoveAt(0);
            }
            _needsRedraw = true;
        }
    }

    public void RecordPeerSeen(NodeId nodeId, NodeRole role = NodeRole.Peer, DateTimeOffset? seenAtUtc = null)
    {
        lock (_stateLock)
        {
            DateTimeOffset ts = seenAtUtc ?? DateTimeOffset.UtcNow;
            if (_peers.TryGetValue(nodeId, out ConnectedPeerStatus? existing) && existing.Role == NodeRole.MeetingPoint)
            {
                role = NodeRole.MeetingPoint;
            }

            _peers[nodeId] = new ConnectedPeerStatus(nodeId, role, ts);
            _needsRedraw = true;
        }
    }

    public void AddChatMessage(string groupId, string authorName, string authorBadge, string content, DateTimeOffset? sentAtUtc = null)
    {
        lock (_stateLock)
        {
            if (!_groupTitles.TryGetValue(groupId, out string? title))
            {
                title = groupId;
                _groupTitles[groupId] = title;
            }

            _messages.Add(new UiChatMessage(
                sentAtUtc ?? DateTimeOffset.UtcNow,
                groupId,
                title,
                authorName,
                authorBadge,
                content));
            _needsRedraw = true;
        }
    }

    public async Task RunAsync()
    {
        await TryHydratePeersFromSqliteAsync().ConfigureAwait(false);

        // Activer le buffer d'écran alternatif ncurses/xterm et masquer le curseur matériel
        Console.OutputEncoding = Encoding.UTF8;
        Console.Write("\x1b[?1049h\x1b[?25l\x1b[2J");

        int lastWidth = SafeWindowWidth();
        int lastHeight = SafeWindowHeight();
        DateTimeOffset lastPeriodicRefresh = DateTimeOffset.UtcNow;

        try
        {
            while (_running)
            {
                int width = SafeWindowWidth();
                int height = SafeWindowHeight();
                if (width != lastWidth || height != lastHeight)
                {
                    lastWidth = width;
                    lastHeight = height;
                    _needsRedraw = true;
                }

                // Rafraîchissement périodique pour mettre à jour l'âge des pairs (< 15 min)
                if ((DateTimeOffset.UtcNow - lastPeriodicRefresh).TotalSeconds >= 5)
                {
                    lastPeriodicRefresh = DateTimeOffset.UtcNow;
                    _needsRedraw = true;
                }

                if (_needsRedraw)
                {
                    string frame = RenderFrame(width, height);
                    Console.Write(frame);
                    _needsRedraw = false;
                }

                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                    await HandleKeyPressAsync(key).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(35).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Restaurer l'écran principal et le curseur
            Console.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
            _activeSession?.Dispose();
            StopMeshInternal();
        }
    }

    /// <summary>
    /// Génère une trame complète de l'interface ncurses (utilisable aussi pour les tests de rendu).
    /// </summary>
    public string RenderFrame(int width, int height)
    {
        width = Math.Max(width, 72);
        height = Math.Max(height, 18);

        var sb = new StringBuilder(width * height * 2);
        sb.Append("\x1b[H"); // Curseur en (1,1)

        lock (_stateLock)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            LogTheme activeTheme = FilterThemes[_themeFilterIndex];

            // 1. Barre de titre supérieure (couleur bleu foncé / blanc gras style ncurses)
            string accountLabel = _activeSession is null
                ? "Account: [Locked]"
                : $"Account: {_activeSession.Profile.Username} ({_activeSession.Profile.AuthorId.Value[..8]})";
            string modBadge = string.Empty;
            int pendingCount = 0;
            if (_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? activeGov))
            {
                pendingCount = _govService.GetPendingJoinRequests(activeGov.GroupId).Count;
                if (activeGov.CurrentManifest.IsTerminated)
                {
                    modBadge = " | [TERMINATED]";
                }
                else if (_activeSession is not null && activeGov.ActiveModerators.ContainsKey(_activeSession.Profile.AuthorId))
                {
                    modBadge = pendingCount > 0 ? $" | [MOD Pending:{pendingCount}]" : " | [MOD]";
                }
            }
            string nodeLabel = _transport is null
                ? "Node: [Offline]"
                : $"Node: {_transport.SelfId.Value[..Math.Min(10, _transport.SelfId.Value.Length)]}";
            string headerText = $" WHISPERNET TUI | Group: {_activeGroupTitle} [{ShortId(_activeGroupId)}] | {accountLabel}{modBadge} | {nodeLabel} ";
            sb.Append("\x1b[44;97;1m");
            sb.Append(FitString(headerText, width));
            sb.Append("\x1b[0m\n");

            int bodyHeight = height - 4; // 1 header + bodyHeight + 1 status bar + 1 input bar + 1 border
            int rightPaneWidth = Math.Clamp(width / 3, 26, 38);
            int leftPaneWidth = width - rightPaneWidth - 1;

            // Bordure supérieure des panneaux
            string leftTitle = _showPendingPanel
                ? $" Moderator Queue [Pending: {pendingCount}] (A:Accept, I:Ignore, F2:Chat) "
                : _showLogsPanel
                    ? $" Transaction Logs [Filter: {activeTheme.ToString().ToUpperInvariant()}] (F2:Chat, F3:Theme) "
                    : $" Group Chat: {_activeGroupTitle} (#{ShortId(_activeGroupId)}) ";
            string rightTitle = " Peers (<15m) ";

            sb.Append("\x1b[36m┌");
            sb.Append(BuildBorderTitle(leftTitle, leftPaneWidth - 2));
            sb.Append('┬');
            sb.Append(BuildBorderTitle(rightTitle, rightPaneWidth - 1));
            sb.Append("┐\x1b[0m\n");

            // Préparation des lignes du panneau gauche (Chat, Logs ou Modération)
            List<string> leftLines = _showPendingPanel
                ? FormatPendingApprovalsLines(leftPaneWidth - 2, bodyHeight - 1)
                : _showLogsPanel
                    ? FormatLogLines(activeTheme, leftPaneWidth - 2, bodyHeight - 1)
                    : FormatChatLines(leftPaneWidth - 2, bodyHeight - 1);

            // Préparation des lignes du panneau droit (Pairs actifs < 15 min)
            List<string> rightLines = FormatActivePeersLines(nowUtc, rightPaneWidth - 1, bodyHeight - 1);

            for (int row = 0; row < bodyHeight - 1; row++)
            {
                string lText = row < leftLines.Count ? leftLines[row] : string.Empty;
                string rText = row < rightLines.Count ? rightLines[row] : string.Empty;

                sb.Append("\x1b[36m│\x1b[0m");
                sb.Append(FitString(lText, leftPaneWidth - 2));
                sb.Append("\x1b[36m│\x1b[0m");
                sb.Append(FitString(rText, rightPaneWidth - 1));
                sb.Append("\x1b[36m│\x1b[0m\n");
            }

            // Bordure inférieure des panneaux
            sb.Append("\x1b[36m└");
            sb.Append(new string('─', leftPaneWidth - 2));
            sb.Append('┴');
            sb.Append(new string('─', rightPaneWidth - 1));
            sb.Append("┘\x1b[0m\n");

            // Barre de raccourcis style ncurses
            string modeIndicator = _showPendingPanel ? "VIEW:MOD" : (_showLogsPanel ? "VIEW:LOGS" : "VIEW:CHAT");
            string statusLine = $" F2:Logs/Chat | F3:Filter[{activeTheme.ToString().ToUpperInvariant()}] | F5:Mod/Pending | PgUp/PgDn:Scroll | F10:Quit | {modeIndicator} ";
            sb.Append("\x1b[46;30;1m");
            sb.Append(FitString(statusLine, width));
            sb.Append("\x1b[0m\n");

            // Ligne de saisie (commandes /... ou message direct)
            string prompt = $"[{_activeGroupTitle}]> {_inputBuffer}█";
            sb.Append("\x1b[97m");
            sb.Append(FitString(prompt, width));
            sb.Append("\x1b[0m");
        }

        return sb.ToString();
    }

    private List<string> FormatChatLines(int innerWidth, int maxRows)
    {
        var result = new List<string>();
        List<UiChatMessage> groupMsgs = _messages
            .Where(m => m.GroupId.Equals(_activeGroupId, StringComparison.Ordinal))
            .ToList();

        if (groupMsgs.Count == 0)
        {
            result.Add(" No messages in this group yet.");
            result.Add(" Type a message and press Enter, or type /help for commands.");
            return result;
        }

        var wrapped = new List<string>();
        foreach (UiChatMessage m in groupMsgs)
        {
            string line = $"[{m.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {m.AuthorName}{m.AuthorBadge}: {m.Content}";
            WrapLine(line, innerWidth, wrapped);
        }

        int end = Math.Max(0, wrapped.Count - _scrollOffset);
        int start = Math.Max(0, end - maxRows);
        for (int i = start; i < end; i++)
        {
            result.Add(wrapped[i]);
        }

        return result;
    }

    private List<string> FormatLogLines(LogTheme filter, int innerWidth, int maxRows)
    {
        var result = new List<string>();
        IEnumerable<TransactionLogEntry> query = _logs;
        if (filter != LogTheme.All)
        {
            query = query.Where(e => e.Theme == filter);
        }

        List<TransactionLogEntry> filtered = query.ToList();
        if (filtered.Count == 0)
        {
            result.Add($" No transaction logs for theme [{filter.ToString().ToUpperInvariant()}].");
            result.Add(" Press F3 (or Tab) to cycle theme filters.");
            return result;
        }

        var wrapped = new List<string>();
        foreach (TransactionLogEntry entry in filtered)
        {
            string tag = entry.Theme.ToString().ToUpperInvariant().PadRight(5);
            string line = $"[{entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff}] [{tag}] {entry.Message}";
            WrapLine(line, innerWidth, wrapped);
        }

        int end = Math.Max(0, wrapped.Count - _scrollOffset);
        int start = Math.Max(0, end - maxRows);
        for (int i = start; i < end; i++)
        {
            result.Add(wrapped[i]);
        }

        return result;
    }

    private List<string> FormatActivePeersLines(DateTimeOffset nowUtc, int innerWidth, int maxRows)
    {
        var lines = new List<string>();
        List<ConnectedPeerStatus> activePeers = _peers.Values
            .Where(p => (nowUtc - p.LastSeenUtc) <= ActivePeerWindow)
            .OrderByDescending(p => p.Role == NodeRole.MeetingPoint ? 1 : 0)
            .ThenByDescending(p => p.LastSeenUtc)
            .ToList();

        lines.Add($" Active (<15m): {activePeers.Count}");
        lines.Add(new string('─', Math.Max(1, innerWidth)));

        if (activePeers.Count == 0)
        {
            lines.Add(" No peer seen <15m.");
            lines.Add(" Use /mesh start or");
            lines.Add(" /cache upsert <id>");
            return lines;
        }

        foreach (ConnectedPeerStatus peer in activePeers)
        {
            if (lines.Count >= maxRows)
            {
                break;
            }

            TimeSpan age = nowUtc - peer.LastSeenUtc;
            string ageLabel = age.TotalMinutes < 1
                ? $"{Math.Max(0, (int)age.TotalSeconds)}s"
                : $"{(int)age.TotalMinutes}m";
            string roleBadge = peer.Role == NodeRole.MeetingPoint ? "MP" : "PR";
            string shortId = peer.NodeId.Value.Length > 10
                ? peer.NodeId.Value[..10] + ".."
                : peer.NodeId.Value;

            lines.Add($" [{roleBadge}] {shortId} ({ageLabel})");
        }

        return lines;
    }

    private async Task HandleKeyPressAsync(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.F2:
                lock (_stateLock)
                {
                    _showPendingPanel = false;
                    _showLogsPanel = !_showLogsPanel;
                    _scrollOffset = 0;
                    _needsRedraw = true;
                }
                return;

            case ConsoleKey.F5:
                lock (_stateLock)
                {
                    _showLogsPanel = false;
                    _showPendingPanel = !_showPendingPanel;
                    _selectedPendingIndex = 0;
                    _scrollOffset = 0;
                    _needsRedraw = true;
                }
                return;

            case ConsoleKey.UpArrow when _showPendingPanel:
                lock (_stateLock)
                {
                    _selectedPendingIndex = Math.Max(0, _selectedPendingIndex - 1);
                    _needsRedraw = true;
                }
                return;

            case ConsoleKey.DownArrow when _showPendingPanel:
                lock (_stateLock)
                {
                    _selectedPendingIndex++;
                    _needsRedraw = true;
                }
                return;

            case ConsoleKey.A when _showPendingPanel && _inputBuffer.Length == 0:
                await HandleQuickModActionAsync(accept: true).ConfigureAwait(false);
                return;

            case ConsoleKey.I when _showPendingPanel && _inputBuffer.Length == 0:
                await HandleQuickModActionAsync(accept: false).ConfigureAwait(false);
                return;

            case ConsoleKey.F3:
            case ConsoleKey.F4:
            case ConsoleKey.Tab when _showLogsPanel:
                lock (_stateLock)
                {
                    _themeFilterIndex = (_themeFilterIndex + 1) % FilterThemes.Length;
                    _scrollOffset = 0;
                    _needsRedraw = true;
                }
                return;

            case ConsoleKey.PageUp:
                lock (_stateLock)
                {
                    _scrollOffset += 5;
                    _needsRedraw = true;
                }
                return;

            case ConsoleKey.PageDown:
                lock (_stateLock)
                {
                    _scrollOffset = Math.Max(0, _scrollOffset - 5);
                    _needsRedraw = true;
                }
                return;

            case ConsoleKey.F10:
                _running = false;
                return;

            case ConsoleKey.Escape:
                lock (_stateLock)
                {
                    if (_showLogsPanel || _showPendingPanel)
                    {
                        _showLogsPanel = false;
                        _showPendingPanel = false;
                        _scrollOffset = 0;
                    }
                    else if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Clear();
                        _historyIndex = -1;
                    }
                    _needsRedraw = true;
                }
                return;

            case ConsoleKey.UpArrow when !_showPendingPanel:
                lock (_stateLock)
                {
                    if (_inputHistory.Count > 0)
                    {
                        if (_historyIndex == -1)
                        {
                            _savedDraft = _inputBuffer.ToString();
                            _historyIndex = _inputHistory.Count - 1;
                        }
                        else if (_historyIndex > 0)
                        {
                            _historyIndex--;
                        }

                        _inputBuffer.Clear();
                        _inputBuffer.Append(_inputHistory[_historyIndex]);
                        _needsRedraw = true;
                    }
                }
                return;

            case ConsoleKey.DownArrow when !_showPendingPanel:
                lock (_stateLock)
                {
                    if (_historyIndex != -1)
                    {
                        _historyIndex++;
                        _inputBuffer.Clear();
                        if (_historyIndex >= _inputHistory.Count)
                        {
                            _historyIndex = -1;
                            _inputBuffer.Append(_savedDraft);
                        }
                        else
                        {
                            _inputBuffer.Append(_inputHistory[_historyIndex]);
                        }
                        _needsRedraw = true;
                    }
                }
                return;

            case ConsoleKey.Backspace:
                lock (_stateLock)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Length--;
                        _needsRedraw = true;
                    }
                }
                return;

            case ConsoleKey.Enter:
            {
                string line;
                lock (_stateLock)
                {
                    line = _inputBuffer.ToString().Trim();
                    _inputBuffer.Clear();
                    _historyIndex = -1;
                    _savedDraft = string.Empty;
                    _scrollOffset = 0;
                    if (!string.IsNullOrEmpty(line) &&
                        (_inputHistory.Count == 0 || !_inputHistory[^1].Equals(line, StringComparison.Ordinal)))
                    {
                        _inputHistory.Add(line);
                        if (_inputHistory.Count > 200)
                        {
                            _inputHistory.RemoveAt(0);
                        }
                    }
                    _needsRedraw = true;
                }

                if (!string.IsNullOrEmpty(line))
                {
                    await ProcessInputLineAsync(line).ConfigureAwait(false);
                }
                return;
            }

            default:
                if (key.KeyChar is '\x7f' or '\b')
                {
                    lock (_stateLock)
                    {
                        if (_inputBuffer.Length > 0)
                        {
                            _inputBuffer.Length--;
                            _needsRedraw = true;
                        }
                    }
                    return;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    lock (_stateLock)
                    {
                        _inputBuffer.Append(key.KeyChar);
                        _needsRedraw = true;
                    }
                }
                return;
        }
    }

    public async Task ProcessInputLineAsync(string line)
    {
        try
        {
            if (line.StartsWith('/'))
            {
                await ExecuteCommandAsync(line[1..]).ConfigureAwait(false);
            }
            else
            {
                await SendChatMessageInActiveGroupAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AddLog(LogTheme.Group, $"ERROR ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private async Task SendChatMessageInActiveGroupAsync(string content)
    {
        string authorName = _activeSession?.Profile.Username ?? "Anonymous";
        string authorBadge = _activeSession is null
            ? string.Empty
            : $"@{_activeSession.Profile.AuthorId.Value[..8]}";

        AddChatMessage(_activeGroupId, authorName, authorBadge, content, DateTimeOffset.UtcNow);
        AddLog(LogTheme.Chat, $"Local message in '{_activeGroupTitle}' (#{_activeGroupId}) by {authorName}: {content}");

        if (_directMesh is not null)
        {
            try
            {
                IReadOnlyDictionary<NodeId, bool> results = await _directMesh
                    .SendMessageAsync(_activeGroupId, authorName, content, senderAuthorId: _activeSession?.Profile.AuthorId)
                    .ConfigureAwait(false);

                foreach (KeyValuePair<NodeId, bool> kv in results)
                {
                    if (kv.Value)
                    {
                        RecordPeerSeen(kv.Key);
                    }
                    AddLog(LogTheme.Mesh, $"Sent chat packet to {kv.Key} -> {(kv.Value ? "OK" : "FAILED")}");
                }
            }
            catch (Exception ex)
            {
                AddLog(LogTheme.Mesh, $"Send skipped/failed: {ex.Message}");
                AddChatMessage(_activeGroupId, "MESH", "", $"Send failed: {ex.Message}");
            }
        }
    }

    private async Task ExecuteCommandAsync(string rawCommand)
    {
        List<string> tokens = Program.SplitArgs(rawCommand);
        if (tokens.Count == 0)
        {
            return;
        }

        string cmd = tokens[0].ToLowerInvariant();
        switch (cmd)
        {
            case "quit" or "exit":
                _running = false;
                break;

            case "help":
                AddChatMessage(_activeGroupId, "SYSTEM", "",
                    "Commands: /account create|unlock|lock | /group create|invite|join|use|terminate | /mod pending|accept|ignore|silence|ban|admit | /mesh start|conv | /logs | /filter <THEME> | /quit");
                break;

            case "logs":
                lock (_stateLock)
                {
                    _showPendingPanel = false;
                    _showLogsPanel = !_showLogsPanel;
                    _needsRedraw = true;
                }
                break;

            case "filter":
                if (tokens.Count >= 2 && Enum.TryParse(tokens[1], ignoreCase: true, out LogTheme parsedTheme))
                {
                    int idx = Array.IndexOf(FilterThemes, parsedTheme);
                    if (idx >= 0)
                    {
                        lock (_stateLock)
                        {
                            _themeFilterIndex = idx;
                            _showPendingPanel = false;
                            _showLogsPanel = true;
                            _needsRedraw = true;
                        }
                        break;
                    }
                }
                ShowCommandError(LogTheme.Group, "Invalid or missing log theme.", "/filter <ALL|CHAT|MESH|AUTH|VAULT|CACHE|GROUP>");
                break;

            case "group":
                await HandleGroupCommandAsync(tokens).ConfigureAwait(false);
                break;

            case "mod":
                await HandleModCommandAsync(tokens).ConfigureAwait(false);
                break;

            case "account" or "acccout" or "acccount":
                await HandleAccountCommandAsync(tokens).ConfigureAwait(false);
                break;

            case "authorid" or "whoami":
                ShowCurrentAuthorId();
                break;

            case "copy":
                HandleCopyCommand(tokens);
                break;

            case "vault":
                await HandleVaultCommandAsync(tokens).ConfigureAwait(false);
                break;

            case "cache":
                await HandleCacheCommandAsync(tokens).ConfigureAwait(false);
                break;

            case "mesh":
                await HandleMeshCommandAsync(tokens).ConfigureAwait(false);
                break;

            case "sim-dag":
                _ = Task.Run(RunThreeUserLateMessageSimulationAsync);
                break;

            default:
                ShowCommandError(LogTheme.Group, $"Unknown command '/{cmd}'.", "/help");
                break;
        }
    }

    public async Task<(ChatEvent E1, ChatEvent E2Bob, ChatEvent E3CharlieLate, ChatEvent E4AliceMerge, string GroupId, string GroupTitle)> BuildThreeUserDagEventsAsync()
    {
        AccountProfile aliceProf = await _accountService.CreateAccountAsync("Alice", null).ConfigureAwait(false);
        AccountProfile bobProf = await _accountService.CreateAccountAsync("Bob", null).ConfigureAwait(false);
        AccountProfile charlieProf = await _accountService.CreateAccountAsync("Charlie", null).ConfigureAwait(false);

        using AccountSession alice = await _accountService.UnlockAccountAsync(aliceProf.AuthorId).ConfigureAwait(false);
        using AccountSession bob = await _accountService.UnlockAccountAsync(bobProf.AuthorId).ConfigureAwait(false);
        using AccountSession charlie = await _accountService.UnlockAccountAsync(charlieProf.AuthorId).ConfigureAwait(false);

        // RoomId = 32-byte SHA-256 of a dedicated group authority ECDSA P-256 public key (independent of Alice's personal AuthorId)
        using var groupKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        byte[] roomIdBytes = System.Security.Cryptography.SHA256.HashData(groupKey.ExportSubjectPublicKeyInfo());
        string groupId = Convert.ToHexStringLower(roomIdBytes)[..12];
        const string groupTitle = "Release v1.0 Room";

        // Base timestamp: 2026-11-26 12:00:00 UTC
        ulong t0 = (ulong)new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        ChatEvent e1 = _chatEventService.CreateSignedEvent(
            alice, roomIdBytes, Array.Empty<byte[]>(), logicalClock: 1, timestampMs: t0 + 1000,
            "1. [Alice] Starting release v1.0 deployment.");

        ChatEvent e3CharlieLate = _chatEventService.CreateSignedEvent(
            charlie, roomIdBytes, new[] { e1.EventId }, logicalClock: 2, timestampMs: t0 + 3000,
            "2a. [Charlie - LATE ARRIVAL] Staging DB check passed (sent at :03s, before Bob!).");

        ChatEvent e2Bob = _chatEventService.CreateSignedEvent(
            bob, roomIdBytes, new[] { e1.EventId }, logicalClock: 2, timestampMs: t0 + 5000,
            "2b. [Bob] Production build is green (sent at :05s).");

        ChatEvent e4AliceMerge = _chatEventService.CreateSignedEvent(
            alice, roomIdBytes, new[] { e2Bob.EventId, e3CharlieLate.EventId }, logicalClock: 3, timestampMs: t0 + 8000,
            "3. [Alice] Great, both Charlie and Bob are green -> tagging v1.0!");

        return (e1, e2Bob, e3CharlieLate, e4AliceMerge, groupId, groupTitle);
    }

    public async Task<(ChatEvent LateEventE3, string GroupId, string GroupTitle)> PrepareThreeUserOutOfOrderScenarioAsync()
    {
        (ChatEvent e1, ChatEvent e2Bob, ChatEvent e3CharlieLate, ChatEvent e4AliceMerge, string groupId, string groupTitle) =
            await BuildThreeUserDagEventsAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _activeGroupId = groupId;
            _activeGroupTitle = groupTitle;
            _groupTitles[groupId] = groupTitle;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecordPeerSeen(new NodeId("node_alice_01"), NodeRole.MeetingPoint, now.AddMinutes(-1));
        RecordPeerSeen(new NodeId("node_bob_02"), NodeRole.Peer, now.AddMinutes(-2));
        RecordPeerSeen(new NodeId("node_charlie_03"), NodeRole.Peer, now.AddMinutes(-3));

        IngestDagEvent(groupId, groupTitle, e1, "Alice");
        IngestDagEvent(groupId, groupTitle, e2Bob, "Bob");
        IngestDagEvent(groupId, groupTitle, e4AliceMerge, "Alice");

        return (e3CharlieLate, groupId, groupTitle);
    }

    public async Task RunThreeUserLateMessageSimulationAsync()
    {
        try
        {
            (ChatEvent e1, ChatEvent e2Bob, ChatEvent e3CharlieLate, ChatEvent e4AliceMerge, string groupId, string groupTitle) =
                await BuildThreeUserDagEventsAsync().ConfigureAwait(false);

            lock (_stateLock)
            {
                _activeGroupId = groupId;
                _activeGroupTitle = groupTitle;
                _groupTitles[groupId] = groupTitle;
                _needsRedraw = true;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            RecordPeerSeen(new NodeId("node_alice_01"), NodeRole.MeetingPoint, now.AddMinutes(-1));
            await Task.Delay(1200).ConfigureAwait(false);

            IngestDagEvent(groupId, groupTitle, e1, "Alice");
            await Task.Delay(2500).ConfigureAwait(false);

            RecordPeerSeen(new NodeId("node_bob_02"), NodeRole.Peer, now);
            IngestDagEvent(groupId, groupTitle, e2Bob, "Bob");
            await Task.Delay(2500).ConfigureAwait(false);

            IngestDagEvent(groupId, groupTitle, e4AliceMerge, "Alice");
            AddLog(LogTheme.Chat, "Waiting 4s for Charlie's delayed packet E3 (L=2, ts=:03s) to arrive...");
            await Task.Delay(4000).ConfigureAwait(false);

            RecordPeerSeen(new NodeId("node_charlie_03"), NodeRole.Peer, DateTimeOffset.UtcNow);
            IngestDagEvent(groupId, groupTitle, e3CharlieLate, "Charlie");
        }
        catch (Exception ex)
        {
            AddLog(LogTheme.Chat, $"Simulation error: {ex.Message}");
        }
    }

    private void HandleCopyCommand(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            ShowCommandError(LogTheme.Group, "Missing /copy target.", "/copy <invite|authorid|endpoint>");
            return;
        }

        string target = tokens[1].ToLowerInvariant();
        string? value = null;
        string label = target;

        switch (target)
        {
            case "invite" or "link":
                value = _lastInviteLink;
                if (value is null)
                {
                    ShowCommandError(LogTheme.Group, "No invitation link generated yet.", "/group invite [--preapprove]");
                    return;
                }
                label = "Invitation link";
                break;

            case "authorid" or "author":
                value = _activeSession?.Profile.AuthorId.Value;
                if (value is null)
                {
                    ShowCommandError(LogTheme.Auth, "No account is currently unlocked.", "/account unlock [username|authorId]");
                    return;
                }
                label = "AuthorId";
                break;

            case "endpoint" or "nodeid" or "selfid":
                value = _transport?.SelfId.Value;
                if (value is null)
                {
                    ShowCommandError(LogTheme.Mesh, "Mesh transport is not running.", "/mesh start");
                    return;
                }
                label = "EndpointId";
                break;

            default:
                ShowCommandError(LogTheme.Group, $"Unknown /copy target '{tokens[1]}'.", "/copy <invite|authorid|endpoint>");
                return;
        }

        bool ok = TryCopyToClipboard(value, out string method);
        AddChatMessage(_activeGroupId, "CLIPBOARD", "", ok
            ? $"Copied {label} to clipboard ({method}): {value}"
            : $"Could not copy {label} to OS clipboard; value: {value}");
        AddLog(LogTheme.Group, $"Clipboard copy {label} -> ok={ok} ({method})");
    }

    private static bool TryCopyToClipboard(string text, out string method)
    {
        // 1. Try native Linux clipboard utilities (Wayland wl-copy, then X11 xclip/xsel)
        (string FileName, string Args)[] tools =
        {
            ("wl-copy", string.Empty),
            ("xclip", "-selection clipboard"),
            ("xsel", "--clipboard --input")
        };

        foreach ((string file, string args) in tools)
        {
            try
            {
                using var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                if (proc.Start())
                {
                    proc.StandardInput.Write(text);
                    proc.StandardInput.Close();
                    if (proc.WaitForExit(1000) && proc.ExitCode == 0)
                    {
                        method = file;
                        return true;
                    }
                }
            }
            catch
            {
                // Tool not installed; try next
            }
        }

        // 2. Fallback: OSC 52 terminal clipboard escape sequence (works in modern terminals & over SSH)
        try
        {
            if (!Console.IsOutputRedirected)
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                Console.Write($"\x1b]52;c;{b64}\x07");
                method = "OSC52";
                return true;
            }
        }
        catch
        {
            // Ignore terminal errors
        }

        method = "none";
        return false;
    }

    private void ShowCurrentAuthorId()
    {
        if (_activeSession is null)
        {
            ShowCommandError(LogTheme.Auth, "No account is currently unlocked.", "/account unlock [username|authorId] [password]");
            return;
        }

        AccountProfile p = _activeSession.Profile;
        AddChatMessage(_activeGroupId, "AUTH", "", $"Username: {p.Username} | AuthorId: {p.AuthorId}");
        AddChatMessage(_activeGroupId, "AUTH", "", $"PublicKey: {p.PublicKeySpkiBase64}");
    }

    private void ShowCommandError(LogTheme theme, string error, string usage)
    {
        string text = $"{error} Usage: {usage}";
        AddLog(theme, text);
        AddChatMessage(_activeGroupId, "USAGE", "", text);
    }

    private async Task HandleGroupCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            ShowCommandError(LogTheme.Group, "Missing /group subcommand.", "/group <create <title> [pwd] | use <id> [title] | title <title> | terminate>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "create" && tokens.Count >= 3)
        {
            if (_activeSession is null)
            {
                AddChatMessage(_activeGroupId, "GROUP", "", "Unlock an account first before creating a group.");
                return;
            }

            string title = tokens[2];
            string? pwd = tokens.Count >= 4 ? tokens[3] : null;
            GroupGovernanceState govState = await _govService.CreateGroupAsync(
                _activeSession, title, joinPassword: pwd, creatorNodeId: _transport?.SelfId).ConfigureAwait(false);

            RegisterGovernedGroup(govState);
            AddLog(LogTheme.Group, $"Created governed group '{title}' (GroupId={govState.GroupId}).");
            AddChatMessage(_activeGroupId, "GROUP", "", $"Created group '{title}' (#{ShortId(govState.GroupId.Value)}).");
        }
        else if (sub == "terminate")
        {
            if (_activeSession is null || !_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? govState))
            {
                ShowCommandError(LogTheme.Group, "No active governed group or unlocked account.", "/group create <title> [password]");
                return;
            }

            await _govService.TerminateGroupAsync(_activeSession, govState).ConfigureAwait(false);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(govState);
                await _directMesh.BroadcastSyncPayloadAsync(govState.GroupId).ConfigureAwait(false);
            }
            AddLog(LogTheme.Group, $"Group '{_activeGroupTitle}' permanently terminated.");
            AddChatMessage(_activeGroupId, "GROUP", "", "Group permanently terminated by administrator (broadcasted to peers).");
            lock (_stateLock) { _needsRedraw = true; }
        }
        else if (sub == "invite")
        {
            if (_activeSession is null)
            {
                ShowCommandError(LogTheme.Auth, "No account is currently unlocked.", "/account unlock [username|authorId]");
                return;
            }

            if (!_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? govState))
            {
                ShowCommandError(LogTheme.Group, $"Active room '{_activeGroupTitle}' (#{ShortId(_activeGroupId)}) is not a governed group.",
                    "/group create <title> [password] (or /group list)");
                return;
            }

            if (_transport is null)
            {
                ShowCommandError(LogTheme.Group, "Start mesh transport first so invitees have your NodeId.", "/mesh start");
                return;
            }

            bool preApprove = tokens.Any(t => t.Equals("--preapprove", StringComparison.OrdinalIgnoreCase));
            string? targetArg = tokens.Skip(2).FirstOrDefault(t => !t.Equals("--preapprove", StringComparison.OrdinalIgnoreCase));
            AuthorId? targetAuthorId = string.IsNullOrWhiteSpace(targetArg) ? null : new AuthorId(targetArg);

            string link = _govService.CreateInvitationLink(_activeSession, govState, _transport.SelfId, preApprove, targetAuthorId);
            _lastInviteLink = link;
            bool copied = TryCopyToClipboard(link, out string method);
            AddLog(LogTheme.Group, $"Created invite link (preApprove={preApprove}, copied={copied} via {method}): {link}");
            AddChatMessage(_activeGroupId, "INVITE", preApprove ? "[PRE-APPROVED]" : "", link);
            if (copied)
            {
                AddChatMessage(_activeGroupId, "CLIPBOARD", "", $"Invite link copied to clipboard ({method}).");
            }
        }
        else if (sub == "join" && tokens.Count >= 3)
        {
            if (_activeSession is null)
            {
                ShowCommandError(LogTheme.Group, "Unlock an account first before joining a group.", "/account unlock [username|authorId]");
                return;
            }

            if (_transport is null || _directMesh is null)
            {
                ShowCommandError(LogTheme.Group, "Start mesh transport first before joining a group.", "/mesh start");
                return;
            }

            GroupId targetGroupId;
            NodeId bootstrapPeer;
            string? password = null;
            InvitePreApprovalTicket? preApproval = null;

            if (tokens[2].StartsWith("whispernet://invite/", StringComparison.OrdinalIgnoreCase))
            {
                GroupInvitationData invite = GroupGovernanceService.ParseInvitationLink(tokens[2]);
                targetGroupId = invite.GroupId;
                bootstrapPeer = invite.BootstrapNodeId;
                preApproval = invite.PreApprovalTicket;
                password = tokens.Count >= 4 ? tokens[3] : null;
            }
            else if (tokens.Count >= 4)
            {
                targetGroupId = new GroupId(tokens[2]);
                bootstrapPeer = new NodeId(tokens[3]);
                password = tokens.Count >= 5 ? tokens[4] : null;
            }
            else
            {
                ShowCommandError(LogTheme.Group, "Invalid /group join syntax.", "/group join <whispernet://invite/... | groupId peerNodeId> [password]");
                return;
            }

            JoinApprovalRequest req = _govService.CreateJoinApprovalRequest(
                _activeSession, targetGroupId, _transport.SelfId, password, preApproval);
            await _directMesh.SendJoinRequestAsync(bootstrapPeer, req).ConfigureAwait(false);
            AddLog(LogTheme.Group, $"Sent join request for group {ShortId(targetGroupId.Value)} to {bootstrapPeer}.");
            AddChatMessage(_activeGroupId, "GROUP", "", $"Sent join request to {bootstrapPeer}. Waiting for sync...");
        }
        else if (sub == "list")
        {
            if (_governedGroups.Count == 0)
            {
                AddChatMessage(_activeGroupId, "GROUP", "", "No governed groups loaded. Create one with: /group create <title> [password]");
                return;
            }

            foreach (GroupGovernanceState g in _governedGroups.Values)
            {
                string marker = g.GroupId.Value == _activeGroupId ? "*" : " ";
                AddChatMessage(_activeGroupId, "GROUP", "", $"{marker} {g.CurrentManifest.Title} | GroupId={g.GroupId}");
            }
        }
        else if (sub == "use" && tokens.Count >= 3)
        {
            string query = tokens[2];
            List<GroupGovernanceState> govMatches = _governedGroups.Values
                .Where(g =>
                    g.GroupId.Value.Equals(query, StringComparison.Ordinal) ||
                    g.CurrentManifest.Title.Equals(query, StringComparison.Ordinal))
                .ToList();

            if (govMatches.Count > 1)
            {
                ShowCommandError(LogTheme.Group, $"Multiple groups match '{query}'. Use exact GroupId.", "/group list");
                return;
            }

            lock (_stateLock)
            {
                if (govMatches.Count == 1)
                {
                    GroupGovernanceState matched = govMatches[0];
                    _activeGroupId = matched.GroupId.Value;
                    _activeGroupTitle = matched.CurrentManifest.Title;
                    _groupTitles[_activeGroupId] = _activeGroupTitle;
                }
                else
                {
                    _activeGroupId = query;
                    if (tokens.Count >= 4)
                    {
                        _activeGroupTitle = string.Join(' ', tokens.Skip(3));
                        _groupTitles[_activeGroupId] = _activeGroupTitle;
                    }
                    else if (_groupTitles.TryGetValue(_activeGroupId, out string? existingTitle))
                    {
                        _activeGroupTitle = existingTitle;
                    }
                    else
                    {
                        _activeGroupTitle = _activeGroupId;
                        _groupTitles[_activeGroupId] = _activeGroupTitle;
                    }
                }
                _needsRedraw = true;
            }
            AddLog(LogTheme.Group, $"Switched active group to '{_activeGroupTitle}' (#{ShortId(_activeGroupId)}).");
        }
        else if (sub == "title" && tokens.Count >= 3)
        {
            lock (_stateLock)
            {
                _activeGroupTitle = string.Join(' ', tokens.Skip(2));
                _groupTitles[_activeGroupId] = _activeGroupTitle;
                _needsRedraw = true;
            }
            AddLog(LogTheme.Group, $"Updated group '{_activeGroupId}' title to '{_activeGroupTitle}'.");
        }
        else
        {
            ShowCommandError(LogTheme.Group, $"Invalid /group syntax '{string.Join(' ', tokens)}'.",
                "/group <create <title> [pwd] | use <id> [title] | title <title> | terminate>");
        }
    }

    private async Task HandleModCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            ShowCommandError(LogTheme.Group, "Missing /mod subcommand.",
                "/mod <pending | accept <user|id> | ignore <user|id> | silence <id> [reason] | ban <id> [reason] | admit <id>>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "pending")
        {
            lock (_stateLock)
            {
                _showLogsPanel = false;
                _showPendingPanel = true;
                _selectedPendingIndex = 0;
                _needsRedraw = true;
            }
            return;
        }

        if (!_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? govState))
        {
            AddChatMessage(_activeGroupId, "MOD", "", "Active group is not a governed group. Create one with /group create <title>.");
            return;
        }

        if (sub == "ignore" && tokens.Count >= 3)
        {
            JoinApprovalRequest? targetReq = ResolvePendingRequest(govState.GroupId, tokens[2]);
            if (targetReq is null)
            {
                AddLog(LogTheme.Group, $"No pending request matching '{tokens[2]}'.");
                return;
            }

            _govService.IgnorePendingMember(govState.GroupId, targetReq.RequesterAuthorId);
            AddLog(LogTheme.Group, $"Ignored pending join request from '{targetReq.RequesterUsername}'.");
            AddChatMessage(_activeGroupId, "MOD", "", $"Ignored join request from {targetReq.RequesterUsername}.");
            lock (_stateLock) { _needsRedraw = true; }
            return;
        }

        if (_activeSession is null)
        {
            AddChatMessage(_activeGroupId, "MOD", "", "Unlock a moderator account first.");
            return;
        }

        if (sub == "accept" && tokens.Count >= 3)
        {
            JoinApprovalRequest? targetReq = ResolvePendingRequest(govState.GroupId, tokens[2]);
            if (targetReq is null)
            {
                AddLog(LogTheme.Group, $"No pending request matching '{tokens[2]}'.");
                return;
            }

            _govService.AcceptPendingMember(_activeSession, govState, targetReq.RequesterAuthorId);
            RecordPeerSeen(targetReq.RequesterNodeId);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(govState);
                await _directMesh.SendSyncPayloadAsync(
                    targetReq.RequesterNodeId,
                    _directMesh.BuildSyncPayload(govState.GroupId)).ConfigureAwait(false);
            }
            AddLog(LogTheme.Group, $"Accepted '{targetReq.RequesterUsername}' ({targetReq.RequesterAuthorId.Value[..8]}..) as Admitted.");
            AddChatMessage(_activeGroupId, "MOD", "", $"Admitted {targetReq.RequesterUsername} into the group.");
            lock (_stateLock) { _needsRedraw = true; }
        }
        else if (sub is "silence" or "ban" or "admit" && tokens.Count >= 3)
        {
            AuthorId? targetId = ResolveMemberAuthorId(govState, tokens[2]);
            if (targetId is null || !govState.MemberStates.TryGetValue(targetId.Value, out ModerationAction? lastAction))
            {
                AddLog(LogTheme.Group, $"No known member matching '{tokens[2]}'.");
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
            AddLog(LogTheme.Group, $"Set {targetId.Value.Value[..8]}.. status -> {newStatus}.");
            AddChatMessage(_activeGroupId, "MOD", "", $"Updated {targetId.Value.Value[..8]}.. -> {newStatus}.");
            lock (_stateLock) { _needsRedraw = true; }
        }
        else
        {
            ShowCommandError(LogTheme.Group, $"Invalid /mod syntax '{string.Join(' ', tokens)}'.",
                "/mod <pending | accept <user|id> | ignore <user|id> | silence <id> [reason] | ban <id> [reason] | admit <id>>");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task HandleQuickModActionAsync(bool accept)
    {
        if (!_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? govState))
        {
            return;
        }

        IReadOnlyList<JoinApprovalRequest> pending = _govService.GetPendingJoinRequests(govState.GroupId);
        if (pending.Count == 0)
        {
            return;
        }

        int idx = Math.Clamp(_selectedPendingIndex, 0, pending.Count - 1);
        JoinApprovalRequest req = pending[idx];

        if (accept)
        {
            if (_activeSession is null)
            {
                AddLog(LogTheme.Group, "Unlock a moderator account to accept pending members.");
                return;
            }

            _govService.AcceptPendingMember(_activeSession, govState, req.RequesterAuthorId);
            RecordPeerSeen(req.RequesterNodeId);
            if (_directMesh is not null)
            {
                _directMesh.RegisterGroup(govState);
                await _directMesh.SendSyncPayloadAsync(
                    req.RequesterNodeId,
                    _directMesh.BuildSyncPayload(govState.GroupId)).ConfigureAwait(false);
            }
            AddLog(LogTheme.Group, $"Accepted '{req.RequesterUsername}' ({req.RequesterAuthorId.Value[..8]}..) via quick key.");
        }
        else
        {
            _govService.IgnorePendingMember(govState.GroupId, req.RequesterAuthorId);
            AddLog(LogTheme.Group, $"Ignored '{req.RequesterUsername}' ({req.RequesterAuthorId.Value[..8]}..) via quick key.");
        }

        lock (_stateLock)
        {
            _selectedPendingIndex = Math.Max(0, Math.Min(_selectedPendingIndex, pending.Count - 2));
            _needsRedraw = true;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private List<string> FormatPendingApprovalsLines(int innerWidth, int maxRows)
    {
        var lines = new List<string>();
        if (!_governedGroups.TryGetValue(_activeGroupId, out GroupGovernanceState? govState))
        {
            lines.Add(" Active group is not a governed group.");
            lines.Add(" Create one with: /group create \"My Room\" [password]");
            return lines;
        }

        IReadOnlyList<JoinApprovalRequest> pending = _govService.GetPendingJoinRequests(govState.GroupId);
        lines.Add($" Group: {govState.CurrentManifest.Title} | Pending requests: {pending.Count}");
        lines.Add(" Keys: Up/Down=Select | A=Accept | I=Ignore | F2=Back to Chat");
        lines.Add(new string('─', Math.Max(1, innerWidth)));

        if (pending.Count == 0)
        {
            lines.Add(" No pending join requests.");
        }
        else
        {
            _selectedPendingIndex = Math.Clamp(_selectedPendingIndex, 0, pending.Count - 1);
            for (int i = 0; i < pending.Count && lines.Count < maxRows - 3; i++)
            {
                JoinApprovalRequest r = pending[i];
                string cursor = i == _selectedPendingIndex ? ">" : " ";
                string nodeShort = r.RequesterNodeId.Value.Length > 10 ? r.RequesterNodeId.Value[..10] + ".." : r.RequesterNodeId.Value;
                lines.Add($" {cursor} {r.RequesterUsername} ({r.RequesterAuthorId.Value[..8]}..) | node={nodeShort} | {r.RequestedAtUtc:HH:mm:ss}");
            }
        }

        lines.Add(new string('─', Math.Max(1, innerWidth)));
        int admittedCount = govState.MemberStates.Values.Count(a => a.NewStatus == MemberStatus.Admitted);
        int silencedCount = govState.MemberStates.Values.Count(a => a.NewStatus == MemberStatus.Silenced);
        int bannedCount = govState.MemberStates.Values.Count(a => a.NewStatus == MemberStatus.Banned);
        lines.Add($" Roster: Admitted={admittedCount} | Silenced={silencedCount} | Banned={bannedCount}");
        return lines;
    }

    private JoinApprovalRequest? ResolvePendingRequest(GroupId groupId, string query)
    {
        return _govService.GetPendingJoinRequests(groupId).FirstOrDefault(r =>
            r.RequesterAuthorId.Value.Equals(query, StringComparison.Ordinal) ||
            r.RequesterUsername.Equals(query, StringComparison.Ordinal));
    }

    private static AuthorId? ResolveMemberAuthorId(GroupGovernanceState govState, string query)
    {
        foreach (AuthorId id in govState.MemberStates.Keys)
        {
            if (id.Value.Equals(query, StringComparison.Ordinal))
            {
                return id;
            }
        }
        return null;
    }

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;

    public async Task PrepareModeratorQueueDemoAsync()
    {
        AccountProfile aliceProf = await _accountService.CreateAccountAsync("Alice", null).ConfigureAwait(false);
        AccountProfile bobProf = await _accountService.CreateAccountAsync("Bob", null).ConfigureAwait(false);
        AccountProfile charlieProf = await _accountService.CreateAccountAsync("Charlie", null).ConfigureAwait(false);

        AccountSession alice = await _accountService.UnlockAccountAsync(aliceProf.AuthorId).ConfigureAwait(false);
        using AccountSession bob = await _accountService.UnlockAccountAsync(bobProf.AuthorId).ConfigureAwait(false);
        using AccountSession charlie = await _accountService.UnlockAccountAsync(charlieProf.AuthorId).ConfigureAwait(false);

        lock (_stateLock)
        {
            _activeSession?.Dispose();
            _activeSession = alice;
        }

        GroupGovernanceState govState = await _govService.CreateGroupAsync(
            alice, "Moderated Room", joinPassword: null, creatorNodeId: new NodeId("node_alice_01")).ConfigureAwait(false);
        RegisterGovernedGroup(govState);
        RecordPeerSeen(new NodeId("node_alice_01"), NodeRole.MeetingPoint);

        JoinApprovalRequest bobReq = _govService.CreateJoinApprovalRequest(bob, govState.GroupId, new NodeId("node_bob_02"));
        JoinApprovalRequest charlieReq = _govService.CreateJoinApprovalRequest(charlie, govState.GroupId, new NodeId("node_charlie_03"));
        _govService.TryProcessJoinApprovalRequest(govState, bobReq, out _);
        _govService.TryProcessJoinApprovalRequest(govState, charlieReq, out _);

        lock (_stateLock)
        {
            _showLogsPanel = false;
            _showPendingPanel = true;
            _selectedPendingIndex = 0;
            _needsRedraw = true;
        }
    }

    private async Task HandleAccountCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            AddChatMessage(_activeGroupId, "AUTH", "", "Usage: /account <create|unlock|lock|list> ...");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        switch (sub)
        {
            case "create" when tokens.Count >= 3:
            {
                string username = tokens[2];
                string? password = tokens.Count >= 4 && !tokens[3].Equals("--no-password", StringComparison.OrdinalIgnoreCase)
                    ? tokens[3]
                    : null;
                AccountProfile profile = await _accountService.CreateAccountAsync(username, password).ConfigureAwait(false);
                string msg = $"Created account '{profile.Username}' (AuthorId={profile.AuthorId}, protected={profile.IsPasswordProtected}).";
                AddLog(LogTheme.Auth, msg);
                AddChatMessage(_activeGroupId, "AUTH", "", msg);
                break;
            }

            case "unlock":
            {
                IReadOnlyList<AccountProfile> accounts = await _accountService.ListAccountsAsync().ConfigureAwait(false);
                if (accounts.Count == 0)
                {
                    const string noAccMsg = "No local accounts found. Create one with: /account create <username>";
                    AddLog(LogTheme.Auth, noAccMsg);
                    AddChatMessage(_activeGroupId, "AUTH", "", noAccMsg);
                    return;
                }

                string? query = tokens.Count >= 3 && !tokens[2].Equals("--no-password", StringComparison.OrdinalIgnoreCase)
                    ? tokens[2]
                    : null;
                string? password = tokens.Count >= 4 && !tokens[3].Equals("--no-password", StringComparison.OrdinalIgnoreCase)
                    ? tokens[3]
                    : null;

                List<AccountProfile> matches;
                if (string.IsNullOrEmpty(query))
                {
                    // Sans argument : sélectionne les comptes sans mot de passe
                    matches = accounts.Where(a => !a.IsPasswordProtected).ToList();
                    if (matches.Count == 0)
                    {
                        const string needUserMsg = "Usage: /account unlock <username|authorId> [password]";
                        AddLog(LogTheme.Auth, needUserMsg);
                        AddChatMessage(_activeGroupId, "AUTH", "", needUserMsg);
                        return;
                    }
                }
                else
                {
                    matches = accounts
                        .Where(a =>
                            a.AuthorId.Value.Equals(query, StringComparison.Ordinal) ||
                            a.Username.Equals(query, StringComparison.Ordinal))
                        .ToList();

                    // Si aucun mot de passe n'est fourni et que plusieurs homonymes existent, privilégier celui sans mot de passe
                    if (matches.Count > 1 && string.IsNullOrEmpty(password))
                    {
                        List<AccountProfile> passwordlessMatches = matches.Where(a => !a.IsPasswordProtected).ToList();
                        if (passwordlessMatches.Count == 1)
                        {
                            matches = passwordlessMatches;
                        }
                    }
                }

                if (matches.Count == 0)
                {
                    string notFoundMsg = $"No account found matching '{query}'.";
                    AddLog(LogTheme.Auth, notFoundMsg);
                    AddChatMessage(_activeGroupId, "AUTH", "", notFoundMsg);
                    return;
                }

                if (matches.Count > 1)
                {
                    // S'il y a plusieurs comptes sans mot de passe du même nom, prendre le plus récent et afficher l'info
                    AccountProfile? latestPasswordless = string.IsNullOrEmpty(password)
                        ? matches.Where(a => !a.IsPasswordProtected).OrderByDescending(a => a.CreatedAtUtc).FirstOrDefault()
                        : null;

                    if (latestPasswordless is not null)
                    {
                        matches = new List<AccountProfile> { latestPasswordless };
                    }
                    else
                    {
                        string multiMsg = $"Multiple accounts match '{query}'. Specify exact AuthorId:";
                        AddLog(LogTheme.Auth, multiMsg);
                        AddChatMessage(_activeGroupId, "AUTH", "", multiMsg);
                        foreach (AccountProfile m in matches)
                        {
                            AddChatMessage(_activeGroupId, "AUTH", "", $"  {m.AuthorId} | {m.Username} | protected={m.IsPasswordProtected}");
                        }
                        return;
                    }
                }

                AccountProfile target = matches[0];
                if (!target.IsPasswordProtected)
                {
                    password = null;
                }
                else if (string.IsNullOrEmpty(password))
                {
                    string pwdReqMsg = $"Account '{target.Username}' is password-protected. Use: /account unlock {query} <password>";
                    AddLog(LogTheme.Auth, pwdReqMsg);
                    AddChatMessage(_activeGroupId, "AUTH", "", pwdReqMsg);
                    return;
                }

                AccountSession session = await _accountService.UnlockAccountAsync(target.AuthorId, password).ConfigureAwait(false);
                lock (_stateLock)
                {
                    _activeSession?.Dispose();
                    _activeSession = session;
                    _needsRedraw = true;
                }
                string unlockedMsg = $"Unlocked account '{session.Profile.Username}' ({session.Profile.AuthorId.Value[..12]}.., passwordProtected={session.Profile.IsPasswordProtected}).";
                AddLog(LogTheme.Auth, unlockedMsg);
                AddChatMessage(_activeGroupId, "AUTH", "", unlockedMsg);

                IReadOnlyList<GroupGovernanceState> savedGroups = await _govService.LoadAllGroupsFromVaultAsync(session).ConfigureAwait(false);
                foreach (GroupGovernanceState g in savedGroups)
                {
                    RegisterGovernedGroup(g);
                }
                if (savedGroups.Count > 0)
                {
                    AddLog(LogTheme.Group, $"Loaded {savedGroups.Count} governed group(s) from vault.");
                    AddChatMessage(_activeGroupId, "GROUP", "", $"Loaded {savedGroups.Count} saved group(s) from vault.");
                }
                break;
            }

            case "lock":
                lock (_stateLock)
                {
                    _activeSession?.Dispose();
                    _activeSession = null;
                    _needsRedraw = true;
                }
                AddLog(LogTheme.Auth, "Account session locked and zeroed.");
                AddChatMessage(_activeGroupId, "AUTH", "", "Account session locked.");
                break;

            case "list":
            {
                IReadOnlyList<AccountProfile> accounts = await _accountService.ListAccountsAsync().ConfigureAwait(false);
                AddLog(LogTheme.Auth, $"Loaded {accounts.Count} local account(s).");
                if (accounts.Count == 0)
                {
                    AddChatMessage(_activeGroupId, "AUTH", "", "No local accounts found.");
                }
                foreach (AccountProfile a in accounts)
                {
                    AddChatMessage(_activeGroupId, "AUTH", "", $"{a.Username} | {a.AuthorId} | protected={a.IsPasswordProtected}");
                }
                break;
            }

            case "authorid" or "whoami" or "status":
                ShowCurrentAuthorId();
                break;

            default:
                ShowCommandError(LogTheme.Auth, $"Invalid /account syntax '{string.Join(' ', tokens)}'.",
                    "/account <create <username> [password] | unlock [username|authorId] [password] | authorid | lock | list>");
                break;
        }
    }

    private async Task HandleVaultCommandAsync(List<string> tokens)
    {
        if (_activeSession is null)
        {
            ShowCommandError(LogTheme.Vault, "Account is locked.", "/account unlock [username|authorId] [password]");
            return;
        }

        if (tokens.Count < 3)
        {
            ShowCommandError(LogTheme.Vault, "Invalid /vault syntax.", "/vault <write <relativePath> <text> | read <relativePath>>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "write" && tokens.Count >= 4)
        {
            string relPath = tokens[2];
            string text = string.Join(' ', tokens.Skip(3));
            await _accountService.WriteVaultFileAsync(_activeSession, relPath, Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
            AddLog(LogTheme.Vault, $"Wrote '{relPath}' to vault (encrypted={_activeSession.IsFolderEncrypted}).");
        }
        else if (sub == "read")
        {
            string relPath = tokens[2];
            byte[] data = await _accountService.ReadVaultFileAsync(_activeSession, relPath).ConfigureAwait(false);
            string text = Encoding.UTF8.GetString(data);
            AddLog(LogTheme.Vault, $"Read '{relPath}' from vault ({data.Length} bytes).");
            AddChatMessage(_activeGroupId, "VAULT", $"[{relPath}]", text);
        }
        else
        {
            ShowCommandError(LogTheme.Vault, $"Invalid /vault syntax '{string.Join(' ', tokens)}'.",
                "/vault <write <relativePath> <text> | read <relativePath>>");
        }
    }

    private async Task HandleCacheCommandAsync(List<string> tokens)
    {
        await EnsurePeerCacheAsync().ConfigureAwait(false);
        if (_peerCache is null || tokens.Count < 2)
        {
            ShowCommandError(LogTheme.Cache, "Missing /cache subcommand.", "/cache <upsert <nodeId> [Peer|MeetingPoint] [ticket] | list>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "upsert" && tokens.Count >= 3)
        {
            var nodeId = new NodeId(tokens[2]);
            NodeRole role = tokens.Count >= 4 && Enum.TryParse(tokens[3], ignoreCase: true, out NodeRole r)
                ? r
                : NodeRole.Peer;
            string? ticket = tokens.Count >= 5 ? tokens[4] : null;
            var entry = new CachedPeerEntry(nodeId, role, ticket, DateTimeOffset.UtcNow, SuccessCount: 1);
            await _peerCache.ApplyCacheUpdateAsync(new[] { entry }, Array.Empty<NodeId>()).ConfigureAwait(false);
            RecordPeerSeen(nodeId, role, entry.LastSeenUtc);
            AddLog(LogTheme.Cache, $"Upserted peer {nodeId} (role={role}) in SQLite cache.");
        }
        else if (sub == "list")
        {
            await TryHydratePeersFromSqliteAsync().ConfigureAwait(false);
        }
        else
        {
            ShowCommandError(LogTheme.Cache, $"Invalid /cache syntax '{string.Join(' ', tokens)}'.",
                "/cache <upsert <nodeId> [Peer|MeetingPoint] [ticket] | list>");
        }
    }

    private async Task HandleMeshCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            ShowCommandError(LogTheme.Mesh, "Missing /mesh subcommand.", "/mesh <start [alpn] | conv <groupId> <nodeId1..> | stop>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        switch (sub)
        {
            case "start":
            {
                if (_transport is not null)
                {
                    AddLog(LogTheme.Mesh, $"Mesh already running (SelfId={_transport.SelfId}).");
                    return;
                }

                await EnsurePeerCacheAsync().ConfigureAwait(false);
                string alpn = tokens.Count >= 3 ? tokens[2] : "/whispernet/direct/1";
                _transport = IrohEndpointAdapter.CreateAndBind(alpn: alpn, enableRelay: true, onlineTimeoutMs: 3000);
                _directMesh = new GroupMeshService(_transport, _peerCache);
                _directMesh.MessageReceived += msg =>
                {
                    RecordPeerSeen(msg.SenderId);
                    AddChatMessage(msg.ConversationId, msg.SenderName, $"({msg.SenderId.Value[..8]})", msg.Content, msg.SentAtUtc);
                    AddLog(LogTheme.Chat, $"Received message in '{msg.ConversationId}' from {msg.SenderName} ({msg.SenderId.Value[..8]}..).");
                };
                _directMesh.JoinRequestReceived += req =>
                {
                    RecordPeerSeen(req.RequesterNodeId);
                    if (_governedGroups.TryGetValue(req.GroupId.Value, out GroupGovernanceState? govState))
                    {
                        if (_govService.TryProcessJoinApprovalRequest(
                                govState, req, out string? err, out ModerationAction? autoAdmitted, _activeSession))
                        {
                            if (autoAdmitted is not null || govState.GetMemberStatus(req.RequesterAuthorId) is MemberStatus.Admitted or MemberStatus.Silenced)
                            {
                                _directMesh.RegisterGroup(govState);
                                _ = _directMesh.SendSyncPayloadAsync(req.RequesterNodeId, _directMesh.BuildSyncPayload(govState.GroupId));
                                AddLog(LogTheme.Group, $"Auto-admitted & synced '{req.RequesterUsername}' ({req.RequesterAuthorId.Value[..8]}..).");
                            }
                            else
                            {
                                AddLog(LogTheme.Group, $"Queued join request from '{req.RequesterUsername}' (press F5 to review).");
                                lock (_stateLock) { _needsRedraw = true; }
                            }
                        }
                        else
                        {
                            AddLog(LogTheme.Group, $"Rejected join request from '{req.RequesterUsername}': {err}");
                        }
                    }
                };
                _directMesh.RosterSyncReceived += sync =>
                {
                    if (_directMesh.ApplySyncPayload(sync, _accountService.VerifySignature, out string? err) &&
                        _directMesh.TryGetGroupGovernanceState(sync.Manifest.GroupId, out GroupGovernanceState? syncedState) &&
                        syncedState is not null)
                    {
                        RegisterGovernedGroup(syncedState);
                        if (sync.SenderNodeId is NodeId senderNode)
                        {
                            RecordPeerSeen(senderNode);
                        }
                        foreach (NodeId peerId in syncedState.GetRoomMemberEndpoints(_transport.SelfId))
                        {
                            RecordPeerSeen(peerId);
                        }
                        AddLog(LogTheme.Group, $"Synced group '{syncedState.CurrentManifest.Title}' (#{ShortId(syncedState.GroupId.Value)}).");
                        if (syncedState.CurrentManifest.IsTerminated)
                        {
                            AddChatMessage(syncedState.GroupId.Value, "GROUP", "",
                                $"Group '{syncedState.CurrentManifest.Title}' was permanently terminated by administrator.");
                        }
                        else
                        {
                            AddChatMessage(syncedState.GroupId.Value, "GROUP", "", $"Joined & synced '{syncedState.CurrentManifest.Title}'.");
                        }
                    }
                    else
                    {
                        AddLog(LogTheme.Group, $"Roster sync rejected: {err}");
                    }
                };
                _directMesh.ModerationActionReceived += action =>
                {
                    if (_governedGroups.TryGetValue(action.GroupId.Value, out GroupGovernanceState? govState) &&
                        GroupGovernanceEngine.TryApplyModerationAction(govState, action, _accountService.VerifySignature, out _, out _))
                    {
                        _directMesh.RegisterGroup(govState);
                        AddLog(LogTheme.Group, $"Applied moderation update: {action.TargetAuthorId.Value[..8]}.. -> {action.NewStatus}");
                    }
                };
                foreach (GroupGovernanceState existingGov in _governedGroups.Values)
                {
                    _directMesh.RegisterGroup(existingGov);
                }

                _meshCts = new CancellationTokenSource();
                _transport.StartAcceptLoop(_directMesh.HandleIncomingPayload, _meshCts.Token);
                AddLog(LogTheme.Mesh, $"Iroh endpoint bound: SelfId={_transport.SelfId}");
                AddChatMessage(_activeGroupId, "MESH", "", $"Started Iroh node. SelfId={_transport.SelfId}");
                break;
            }

            case "conv" when tokens.Count >= 4 && _directMesh is not null:
            {
                string convId = tokens[2];
                List<NodeId> members = tokens.Skip(3).Select(id => new NodeId(id)).ToList();
                _directMesh.RegisterConversation(convId, members);
                foreach (NodeId member in members)
                {
                    RecordPeerSeen(member);
                }
                lock (_stateLock)
                {
                    _activeGroupId = convId;
                    if (!_groupTitles.ContainsKey(convId))
                    {
                        _groupTitles[convId] = convId;
                    }
                    _activeGroupTitle = _groupTitles[convId];
                    _needsRedraw = true;
                }
                AddLog(LogTheme.Group, $"Registered group '{convId}' with {members.Count} peer(s).");
                break;
            }

            case "stop":
                StopMeshInternal();
                AddLog(LogTheme.Mesh, "Stopped Iroh mesh transport.");
                break;

            default:
                ShowCommandError(LogTheme.Mesh, $"Invalid /mesh syntax '{string.Join(' ', tokens)}'.",
                    "/mesh <start [alpn] | conv <groupId> <nodeId1..> | stop>");
                break;
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
        AddLog(LogTheme.Cache, $"Initialized SQLite PeerCache at '{dbPath}'.");
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
            AddLog(LogTheme.Cache, $"Loaded {cached.Count} peer(s) from SQLite cache.");
        }
        catch (Exception ex)
        {
            AddLog(LogTheme.Cache, $"PeerCache load skipped: {ex.Message}");
        }
    }

    private void StopMeshInternal()
    {
        _meshCts?.Cancel();
        _meshCts?.Dispose();
        _meshCts = null;
        _directMesh = null;
        _transport?.Dispose();
        _transport = null;
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Console.WindowWidth > 0 ? Console.WindowWidth : 100;
        }
        catch
        {
            return 100;
        }
    }

    private static int SafeWindowHeight()
    {
        try
        {
            return Console.WindowHeight > 0 ? Console.WindowHeight : 28;
        }
        catch
        {
            return 28;
        }
    }

    private static string BuildBorderTitle(string title, int totalWidth)
    {
        if (totalWidth <= 0)
        {
            return string.Empty;
        }

        if (title.Length >= totalWidth)
        {
            return title[..totalWidth];
        }

        int remaining = totalWidth - title.Length;
        return "─" + title + new string('─', Math.Max(0, remaining - 1));
    }

    private static string FitString(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (text.Length > width)
        {
            return text[..width];
        }

        if (text.Length < width)
        {
            return text.PadRight(width);
        }

        return text;
    }

    private static void WrapLine(string text, int maxWidth, List<string> target)
    {
        if (maxWidth <= 4)
        {
            target.Add(text);
            return;
        }

        if (text.Length <= maxWidth)
        {
            target.Add(" " + text);
            return;
        }

        int offset = 0;
        int chunkWidth = maxWidth - 1;
        while (offset < text.Length)
        {
            int len = Math.Min(chunkWidth, text.Length - offset);
            target.Add(" " + text.Substring(offset, len));
            offset += len;
        }
    }
}
