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
        _dmService?.RegisterContactsFromGroup(govState);
        _uiDirty = true;
    }

    private void TriggerBackgroundGroupSync(GroupGovernanceState govState)
    {
        GroupMeshService? mesh = _directMesh;
        IMeshTransport? transport = _transport;
        if (mesh is null || transport is null || govState.CurrentManifest.IsTerminated)
        {
            return;
        }

        IReadOnlyList<NodeId> friendNodes = _friendService.GetFriendNodeEndpoints(transport.SelfId);
        List<NodeId> onlineFriends;
        lock (_stateLock)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
            onlineFriends = friendNodes
                .Where(n => transport.HasActiveConnection(n) ||
                            (_peers.TryGetValue(n, out GuiPeerStatus? st) && st.LastSeenUtc >= cutoff))
                .ToList();
        }
        IReadOnlyList<NodeId> targets = mesh.GetReconnectTargets(govState.GroupId, onlineFriends);
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
                case "friend":
                    await HandleFriendCommandAsync(tokens).ConfigureAwait(false);
                    break;
                default:
                    SetStatus($"Unknown command '/{cmd}'. Supported: /account, /group, /channel, /mod, /mesh, /friend");
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
            SetStatus(copied
                ? $"Invite link copied to clipboard (PreApprove={preApprove})!"
                : $"Invite link generated (PreApprove={preApprove}).");
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
            bool forceStandalone = tokens.Any(static t => t.Equals("--standalone", StringComparison.OrdinalIgnoreCase));

            if (!forceStandalone)
            {
                var daemonClient = new DaemonMeshTransport();
                try
                {
                    await daemonClient.ConnectAsync().ConfigureAwait(false);
                    daemonClient.PeerSeen += seen => RecordPeerSeen(seen.NodeId, seen.Role, seen.SeenAtUtc);
                    _transport = daemonClient;
                    AddLog(GuiLogTheme.Mesh, $"Connected to background daemon whispernetd (SelfId={daemonClient.SelfId}).");
                }
                catch (Exception ex)
                {
                    AddLog(GuiLogTheme.Mesh, $"whispernetd not reachable ({ex.GetType().Name}: {ex.Message}); falling back to in-process Iroh.");
                    daemonClient.Dispose();
                }
            }

            if (_transport is null)
            {
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
            }
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
            _directMesh.FriendRequestReceived += pkt =>
            {
                if (pkt.SenderNodeId is NodeId senderNode)
                {
                    RecordPeerSeen(senderNode);
                }
                _ = _friendService.HandleIncomingFriendRequestAsync(pkt);
            };
            _directMesh.FriendResponseReceived += pkt =>
            {
                if (pkt.SenderNodeId is NodeId senderNode)
                {
                    RecordPeerSeen(senderNode);
                }
                _ = _friendService.HandleIncomingFriendResponseAsync(pkt);
            };
            _directMesh.FriendRemoveReceived += pkt => _ = _friendService.HandleIncomingFriendRemoveAsync(pkt);
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

            if (_transport is DaemonMeshTransport)
            {
                SetStatus($"Connected to whispernetd — SelfId={ShortId(_transport.SelfId.Value)}");
            }
            else
            {
                SetStatus($"Iroh mesh started (standalone) — SelfId={ShortId(_transport.SelfId.Value)}");
                AddLog(GuiLogTheme.Mesh, $"Iroh endpoint bound: SelfId={_transport.SelfId}");
            }
            await EnsureDirectMessagingInitializedAsync().ConfigureAwait(false);
            _friendService.AttachMesh(_transport, _directMesh, _dmService);
            _ = _friendService.FlushPendingOutboundAsync();
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
        _dmService?.RecordPeerActivity(null, nodeId, seenAtUtc);
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

    private async Task EnsureDirectMessagingInitializedAsync()
    {
        if (_activeSession is null)
        {
            return;
        }

        if (_dmRepo is null)
        {
            string dbPath = WhispernetPaths.GetDirectMessagesDbPath(_effectiveRoot, _activeSession.Profile.AuthorId);
            _dmRepo = new SqliteDirectMessageRepository(dbPath);
            await _dmRepo.InitializeAsync().ConfigureAwait(false);
            IReadOnlyList<DirectMessageRecord> saved = await _dmRepo.LoadAllMessagesAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                _dmMessages.Clear();
                _dmMessages.AddRange(saved);
            }
        }

        if (_dmService is null && _transport is not null && _directMesh is not null)
        {
            _dmService = new DirectMessagingService(
                _accountService,
                _activeSession,
                _transport,
                _directMesh,
                _dmRepo,
                () =>
                {
                    lock (_stateLock)
                    {
                        return _governedGroups.Values.ToList();
                    }
                });

            _dmService.MessageQueued += rec =>
            {
                lock (_stateLock)
                {
                    if (!_dmMessages.Any(m => m.MessageUuid == rec.MessageUuid))
                    {
                        _dmMessages.Add(rec);
                    }
                }
                _uiDirty = true;
            };
            _dmService.DirectMessageReceived += rec =>
            {
                lock (_stateLock)
                {
                    _dmTypingPeers.Remove(rec.PeerAuthorId);
                    if (!_dmMessages.Any(m => m.MessageUuid == rec.MessageUuid))
                    {
                        _dmMessages.Add(rec);
                    }
                }
                _uiDirty = true;
            };
            _dmService.MessageStatusChanged += (uuid, status, deliveredAt) =>
            {
                lock (_stateLock)
                {
                    int idx = _dmMessages.FindIndex(m => m.MessageUuid == uuid);
                    if (idx >= 0)
                    {
                        _dmMessages[idx] = _dmMessages[idx] with { Status = status, DeliveredAtUtc = deliveredAt };
                    }
                }
                _uiDirty = true;
            };
            _dmService.TypingIndicatorReceived += typing =>
            {
                lock (_stateLock)
                {
                    if (typing.IsTyping)
                    {
                        _dmTypingPeers[typing.SenderAuthorId] = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        _dmTypingPeers.Remove(typing.SenderAuthorId);
                    }
                }
                _uiDirty = true;
            };
            _dmService.LogEmitted += msg => AddLog(GuiLogTheme.Chat, msg);

            await _dmService.HydrateFromStorageAsync().ConfigureAwait(false);
            await _dmService.ConsumeTestLoopbackInboxAsync().ConfigureAwait(false);
            if (_transport is DaemonMeshTransport daemonClient)
            {
                IReadOnlyList<string> tags = _dmService.ComputeExpectedBlindTags(DateTimeOffset.UtcNow);
                if (tags.Count > 0)
                {
                    _ = daemonClient.RegisterWatchTagsAsync(_activeSession.Profile.AuthorId, tags);
                }
            }
        }
    }

    private void StopMeshInternal()
    {
        _dmService?.Dispose();
        _dmService = null;
        _dmRepo = null;
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

    private async Task HandleFriendCommandAsync(List<string> tokens)
    {
        if (tokens.Count < 2)
        {
            SetStatus("Usage: /friend <link | add <whispernet://friend/...> | accept <id> | reject <id> | remove <id> | restrict <on|off>>");
            return;
        }

        string sub = tokens[1].ToLowerInvariant();
        if (sub == "link")
        {
            if (_activeSession is null || _transport is null)
            {
                SetStatus("Unlock an account and start the mesh first.");
                return;
            }
            string link = _friendService.CreateFriendLink(_activeSession, _transport.SelfId);
            bool copied = TryCopyToClipboard(link);
            SetStatus(copied ? "Copied your whispernet://friend/... link to clipboard." : link);
            return;
        }

        if (sub == "add" && tokens.Count >= 3)
        {
            FriendInvitationData data = FriendService.ParseFriendLink(tokens[2]);
            await _friendService.SendFriendRequestAsync(
                data.AuthorId,
                data.PublicKeySpkiBase64,
                data.DisplayName,
                data.BootstrapNodeId).ConfigureAwait(false);
            SetStatus($"Sent friend request to {data.DisplayName}.");
            return;
        }

        if (sub == "restrict" && tokens.Count >= 3)
        {
            bool restrict = tokens[2].Equals("on", StringComparison.OrdinalIgnoreCase) ||
                            tokens[2].Equals("true", StringComparison.OrdinalIgnoreCase);
            await _friendService.SetRestrictDmsToFriendsAsync(restrict).ConfigureAwait(false);
            SetStatus($"Friends-only DMs: {(restrict ? "ON" : "OFF")}.");
            return;
        }

        if (tokens.Count >= 3 && sub is "accept" or "reject" or "remove")
        {
            string q = tokens[2];
            IReadOnlyList<FriendEntry> pool = sub == "remove"
                ? _friendService.GetAcceptedFriends()
                : _friendService.GetIncomingRequests();
            FriendEntry? target = pool.FirstOrDefault(e =>
                e.PeerAuthorId.Value.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                e.DisplayName.Equals(q, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                SetStatus($"No matching friend/request for '{q}'.");
                return;
            }

            if (sub == "accept")
            {
                await _friendService.AcceptFriendRequestAsync(target.PeerAuthorId).ConfigureAwait(false);
                SetStatus($"Accepted friend request from {target.DisplayName}.");
            }
            else if (sub == "reject")
            {
                await _friendService.RejectFriendRequestAsync(target.PeerAuthorId).ConfigureAwait(false);
                SetStatus($"Rejected friend request from {target.DisplayName}.");
            }
            else
            {
                await _friendService.RemoveFriendAsync(target.PeerAuthorId).ConfigureAwait(false);
                SetStatus($"Removed {target.DisplayName} from friends.");
            }
        }
    }
}
