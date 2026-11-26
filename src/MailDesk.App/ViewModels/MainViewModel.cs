using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Agent;
using MailDesk.App.Services;
using MailDesk.Core.Models;
using MailDesk.Core.Rules;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;

namespace MailDesk.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly IMailSync _sync;
    private readonly IAgentService _agent;
    private readonly ProposalApplier _applier;
    private readonly RuleRunner _rules;

    public MainViewModel(IMailStore store, IMailSync sync, IAgentService agent, ProposalApplier applier, RuleRunner rules)
    {
        _store = store;
        _sync = sync;
        _agent = agent;
        _applier = applier;
        _rules = rules;
    }

    public ObservableCollection<Account> Accounts { get; } = [];
    public ObservableCollection<FolderItem> Folders { get; } = [];
    public ObservableCollection<MessageSummary> Messages { get; } = [];
    public ObservableCollection<AgentProposal> Proposals { get; } = [];
    public ObservableCollection<MailRule> Rules { get; } = [];

    [ObservableProperty] private FolderItem? _selectedFolder;
    [ObservableProperty] private MessageSummary? _selectedMessage;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _readerHtml = MailHtml.Empty();
    [ObservableProperty] private string _listTitle = "Inbox";
    [ObservableProperty] private string _listSubtitle = "";
    [ObservableProperty] private bool _isAgentPaneOpen;
    [ObservableProperty] private string _workspaceTitle = "MailDesk";
    [ObservableProperty] private string _workspaceSubtitle = "No account yet";

    /// <summary>Raised by the view to open the account dialog; returns the account plus its plaintext password, or null.</summary>
    public Func<Task<(Account account, string password)?>>? RequestAccountDialog { get; set; }

    /// <summary>Raised by the view to open the rule editor. Returns the saved rule, or null if cancelled; the bool is "apply now".</summary>
    public Func<MailRule?, long, IReadOnlyList<FolderChoice>, Task<(MailRule rule, bool applyNow)?>>? RequestRuleEditor { get; set; }

    /// <summary>Raised by the view to show the rules window.</summary>
    public Func<Task>? RequestRulesWindow { get; set; }

    /// <summary>Raised by the view to open a compose window. Returns a status line describing what happened, or null.</summary>
    public Func<Account, OutgoingMessage, Task<string?>>? RequestCompose { get; set; }

    /// <summary>Raised by the view to open the AI settings dialog.</summary>
    public Func<Task>? RequestAiSettings { get; set; }

    /// <summary>Raised by the view to open the calendar window for an account.</summary>
    public Func<Account, Task>? RequestCalendar { get; set; }

    /// <summary>Raised by the view to show the tasks window.</summary>
    public Func<Account, Task>? RequestTasks { get; set; }

    [RelayCommand]
    private async Task OpenTasksAsync()
    {
        var account = CurrentAccount;
        if (RequestTasks is null || account is null) { StatusText = "Add an account first."; return; }
        await RequestTasks(account);
    }

    /// <summary>Right-click: one task per message, titled from the subject, linked back to the message.</summary>
    [RelayCommand]
    private async Task CreateTaskFromMessageAsync(MessageSummary? message)
    {
        if (message is null || SelectedFolder is null) return;
        var subject = message.Subject.Trim();
        foreach (var prefix in new[] { "re:", "fw:", "fwd:" })
            while (subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) subject = subject[prefix.Length..].Trim();
        var task = new TaskItem
        {
            AccountId = SelectedFolder.Account.Id,
            Title = subject.Length == 0 ? $"Follow up with {message.FromDisplay}" : subject,
            Notes = $"From {message.FromDisplay}, {message.Date:d MMM yyyy}",
            SourceMessageId = message.Id,
            SourceSubject = message.Subject,
        };
        await _store.UpsertTaskAsync(task);
        StatusText = $"Task added: {task.Title}";
    }

    /// <summary>Opens a message by local id, switching folders if needed. Used by the tasks window.</summary>
    public async Task OpenMessageAsync(long messageId)
    {
        var message = await _store.GetMessageAsync(messageId);
        if (message is null) { StatusText = "That message is no longer in the local cache."; return; }
        var folder = Folders.FirstOrDefault(f => f.Folder.Id == message.FolderId);
        if (folder is not null && !ReferenceEquals(folder, SelectedFolder))
        {
            SelectedFolder = folder;
            await LoadMessagesAsync();
        }
        SelectedMessage = Messages.FirstOrDefault(m => m.Id == messageId) ?? message;
    }

    // ---------------- Calendar and invites ----------------

    [ObservableProperty] private CalendarItem? _invite;
    [ObservableProperty] private string _inviteStatusText = "";

    [RelayCommand]
    private async Task OpenCalendarAsync()
    {
        var account = CurrentAccount;
        if (RequestCalendar is null || account is null) { StatusText = "Add an account first."; return; }
        await RequestCalendar(account);
    }

    private async Task LoadInviteAsync(MessageSummary message, MessageBody body)
    {
        Invite = null;
        InviteStatusText = "";
        if (string.IsNullOrWhiteSpace(body.CalendarIcs) || SelectedFolder is null) return;
        try
        {
            var parsed = Calendar.IcsCodec.Parse(body.CalendarIcs, SelectedFolder.Account.Id, message.Id).FirstOrDefault();
            if (parsed is null) return;
            // Prefer what we already know (our response) but show the newest details from this message.
            var known = await _store.GetCalendarItemAsync(parsed.AccountId, parsed.Uid);
            if (known is not null) { parsed.MyResponse = known.MyResponse; parsed.Id = known.Id; }
            Invite = parsed;
            InviteStatusText = parsed.Status == "CANCELLED" ? "This meeting was cancelled."
                : parsed.MyResponse == RsvpStatus.NeedsAction ? "Not responded yet."
                : $"You responded: {parsed.MyResponse}.";
        }
        catch (Exception ex)
        {
            InviteStatusText = $"Invite could not be read: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task AcceptInviteAsync() => RespondAsync(RsvpStatus.Accepted);

    [RelayCommand]
    private Task TentativeInviteAsync() => RespondAsync(RsvpStatus.Tentative);

    [RelayCommand]
    private Task DeclineInviteAsync() => RespondAsync(RsvpStatus.Declined);

    [RelayCommand]
    private async Task AddInviteToCalendarAsync()
    {
        if (Invite is null) return;
        var saved = await _store.UpsertCalendarItemAsync(Invite);
        Invite = saved;
        InviteStatusText = "Added to your calendar.";
        StatusText = $"Added to calendar: {saved.Summary}";
    }

    [RelayCommand]
    private void JoinInvite() => CalendarViewModel.OpenLink(Invite?.JoinUrl);

    /// <summary>Sends an iTIP reply to the organizer, then records the response locally. Sending is a real email, so it goes through IMailSender.</summary>
    private async Task RespondAsync(RsvpStatus response)
    {
        if (Invite is null || SelectedFolder is null || RequestSender is null) return;
        var account = SelectedFolder.Account;
        var invite = Invite;
        await RunBusyAsync($"Sending response to {invite.OrganizerName}...", async () =>
        {
            if (string.IsNullOrWhiteSpace(invite.OrganizerEmail))
                throw new InvalidOperationException("The invite has no organizer address to reply to.");
            var ics = Calendar.IcsCodec.BuildReply(invite, account.Email, account.Name, response);
            var sender = RequestSender();
            await sender.SendAsync(account, new OutgoingMessage
            {
                To = Core.Text.ComposeBuilder.Format(invite.OrganizerName, invite.OrganizerEmail),
                Subject = Calendar.IcsCodec.ResponseSubject(invite, response),
                TextBody = $"{account.Name} has {response.ToString().ToLowerInvariant()} this meeting: {invite.Summary}\n{invite.WhenText()}",
                CalendarIcs = ics, CalendarMethod = "REPLY",
            });
            invite.MyResponse = response;
            var saved = await _store.UpsertCalendarItemAsync(invite);
            await _store.SetCalendarResponseAsync(saved.Id, response);
            saved.MyResponse = response;
            Invite = saved;
            InviteStatusText = $"You responded: {response}. Reply sent to {invite.OrganizerEmail}.";
            StatusText = $"{response}: {invite.Summary}";
        });
    }

    /// <summary>Supplied by the view so the view model can send invite replies without holding the sender itself.</summary>
    public Func<IMailSender>? RequestSender { get; set; }

    [ObservableProperty] private string _agentSummary = "";

    [RelayCommand]
    private async Task OpenAiSettingsAsync()
    {
        if (RequestAiSettings is not null) await RequestAiSettings();
    }

    // ---------------- Compose ----------------

    [RelayCommand]
    private Task ComposeNewAsync() => OpenComposeAsync(new OutgoingMessage());

    [RelayCommand]
    private Task ReplyAsync() => ReplyCoreAsync(replyAll: false);

    [RelayCommand]
    private Task ReplyAllAsync() => ReplyCoreAsync(replyAll: true);

    [RelayCommand]
    private async Task ForwardAsync()
    {
        if (SelectedMessage is null || SelectedFolder is null) return;
        var body = await _store.GetBodyAsync(SelectedMessage.Id);
        await OpenComposeAsync(Core.Text.ComposeBuilder.Forward(SelectedMessage, body));
    }

    private async Task ReplyCoreAsync(bool replyAll)
    {
        if (SelectedMessage is null || SelectedFolder is null) return;
        var body = await _store.GetBodyAsync(SelectedMessage.Id);
        await OpenComposeAsync(Core.Text.ComposeBuilder.Reply(SelectedMessage, body, SelectedFolder.Account.Email, replyAll));
    }

    private async Task OpenComposeAsync(OutgoingMessage seed)
    {
        var account = CurrentAccount;
        if (RequestCompose is null || account is null) { StatusText = "Add an account first."; return; }
        var outcome = await RequestCompose(account, seed);
        if (outcome is null) return;
        StatusText = outcome;
        // A sent or drafted message shows up in Sent or Drafts once that folder syncs.
        if (SelectedFolder is { Kind: FolderKind.Sent or FolderKind.Drafts })
            await RunBusyAsync($"Refreshing {SelectedFolder.DisplayName}...", () => SyncFolderAsync(SelectedFolder));
    }

    private Account? CurrentAccount => SelectedFolder?.Account ?? Accounts.FirstOrDefault();

    public async Task LoadAsync()
    {
        Accounts.Clear();
        Folders.Clear();
        foreach (var a in await _store.GetAccountsAsync())
        {
            Accounts.Add(a);
            foreach (var f in await _store.GetFoldersAsync(a.Id))
                Folders.Add(new FolderItem(a, f));
        }
        SelectedFolder ??= Folders.FirstOrDefault(f => f.Kind == FolderKind.Inbox) ?? Folders.FirstOrDefault();
        var first = Accounts.FirstOrDefault();
        WorkspaceTitle = first?.Name ?? "MailDesk";
        WorkspaceSubtitle = first?.Email ?? "No account yet";
        StatusText = Accounts.Count == 0 ? "No accounts yet. Click Add account to get started." : "Ready";
    }

    partial void OnSelectedFolderChanged(FolderItem? value)
    {
        _ = LoadMessagesAsync();
    }

    private async Task LoadMessagesAsync()
    {
        Messages.Clear();
        SelectedMessage = null;
        if (SelectedFolder is null) return;
        ListTitle = SelectedFolder.DisplayName;
        foreach (var m in await _store.GetMessagesAsync(SelectedFolder.Folder.Id))
            Messages.Add(m);
        ListSubtitle = $"{Messages.Count} messages, {SelectedFolder.UnreadCount} unread";
    }

    partial void OnSelectedMessageChanged(MessageSummary? value)
    {
        _ = ShowMessageAsync(value);
    }

    private async Task ShowMessageAsync(MessageSummary? message)
    {
        if (message is null || SelectedFolder is null)
        {
            ReaderHtml = MailHtml.Empty();
            return;
        }
        try
        {
            ReaderHtml = MailHtml.Empty("Loading...");
            Invite = null;
            HasBlockedImages = false;
            var body = await _sync.FetchBodyAsync(SelectedFolder.Account, message);
            if (!ReferenceEquals(message, SelectedMessage)) return; // user moved on while the body was downloading
            _currentBody = body;
            var allowImages = await IsSenderAllowedImagesAsync(message.FromAddress);
            RenderBody(allowImages);
            await LoadInviteAsync(message, body);

            if (!message.IsRead)
            {
                message.IsRead = true;
                await _sync.SetFlagsAsync(SelectedFolder.Account, message, isRead: true);
                SelectedFolder.UnreadCount = Math.Max(0, SelectedFolder.UnreadCount - 1);
            }
        }
        catch (Exception ex)
        {
            ReaderHtml = MailHtml.Empty($"Could not load message: {ex.Message}");
        }
    }

    // ---------------- Remote images ----------------

    private const string AllowedImageSendersKey = "images.allowed_senders";
    private MessageBody? _currentBody;

    /// <summary>True while the open message has remote images that the reader is blocking.</summary>
    [ObservableProperty] private bool _hasBlockedImages;

    private void RenderBody(bool allowRemoteImages)
    {
        var body = _currentBody;
        if (body is null) return;
        if (body.HtmlBody is { Length: > 0 } html)
        {
            ReaderHtml = MailHtml.FromHtml(html, allowRemoteImages);
            HasBlockedImages = !allowRemoteImages && MailHtml.HasRemoteImages(html);
        }
        else
        {
            ReaderHtml = MailHtml.FromText(body.TextBody ?? "(empty message)");
            HasBlockedImages = false;
        }
    }

    private async Task<HashSet<string>> AllowedImageSendersAsync()
    {
        var raw = await _store.GetSettingAsync(AllowedImageSendersKey) ?? "";
        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> IsSenderAllowedImagesAsync(string fromAddress) =>
        !string.IsNullOrWhiteSpace(fromAddress) && (await AllowedImageSendersAsync()).Contains(fromAddress);

    /// <summary>Shows the remote images in the open message only. The next message from the sender is blocked again.</summary>
    [RelayCommand]
    private void ShowImages() => RenderBody(allowRemoteImages: true);

    /// <summary>Shows the images now and remembers the sender address, so their mail always loads images.</summary>
    [RelayCommand]
    private async Task AlwaysShowImagesFromSenderAsync()
    {
        var from = SelectedMessage?.FromAddress;
        if (string.IsNullOrWhiteSpace(from)) return;
        var allowed = await AllowedImageSendersAsync();
        allowed.Add(from);
        await _store.SetSettingAsync(AllowedImageSendersKey, string.Join('\n', allowed.OrderBy(a => a)));
        RenderBody(allowRemoteImages: true);
        StatusText = $"Images from {from} will always show.";
    }

    // ---------------- Accounts and sync ----------------

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (RequestAccountDialog is null) return;
        var result = await RequestAccountDialog();
        if (result is null) return;
        var (account, _) = result.Value;
        await _store.UpsertAccountAsync(account);
        await RunBusyAsync($"Loading folders for {account.Email}...", async () =>
        {
            await _sync.SyncFoldersAsync(account);
            await LoadAsync();
            var inbox = Folders.FirstOrDefault(f => f.Account.Id == account.Id && f.Kind == FolderKind.Inbox);
            if (inbox is not null)
            {
                SelectedFolder = inbox;
                await SyncFolderAsync(inbox);
            }
        });
    }

    [RelayCommand]
    private async Task SyncAsync()
    {
        if (SelectedFolder is null) return;
        await RunBusyAsync($"Syncing {SelectedFolder.DisplayName}...", () => SyncFolderAsync(SelectedFolder));
    }

    private async Task SyncFolderAsync(FolderItem item)
    {
        var progress = new Progress<string>(s => StatusText = s);
        var fresh = await _sync.SyncFolderAsync(item.Account, item.Folder, progress: progress);
        item.UnreadCount = item.Folder.UnreadCount;

        // Rules run on new Inbox mail only. Other folders are where rules put things.
        if (item.Kind == FolderKind.Inbox && fresh.Count > 0)
        {
            var result = await _rules.RunAsync(item.Account, fresh);
            if (result.Matched > 0)
            {
                StatusText = $"{fresh.Count} new. {result.Summary()}";
                await ReloadFoldersKeepingSelectionAsync();
            }
        }
        if (ReferenceEquals(item, SelectedFolder)) await LoadMessagesAsync();
    }

    private async Task ReloadFoldersKeepingSelectionAsync()
    {
        var selectedName = SelectedFolder?.Folder.FullName;
        var selectedAccount = SelectedFolder?.Account.Id;
        await LoadAsync();
        if (selectedName is not null)
            SelectedFolder = Folders.FirstOrDefault(f => f.Account.Id == selectedAccount && f.Folder.FullName == selectedName) ?? SelectedFolder;
    }

    // ---------------- Search ----------------

    /// <summary>True searches every folder of the account (syncing any never-opened folder first); false searches the selected folder only.</summary>
    [ObservableProperty] private bool _searchAllFolders = true;

    partial void OnSearchAllFoldersChanged(bool value)
    {
        if (!string.IsNullOrWhiteSpace(SearchText)) _ = SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await LoadMessagesAsync();
            return;
        }
        var account = CurrentAccount;
        if (SearchAllFolders && account is not null && !IsBusy)
        {
            // Folders that were never opened have nothing cached, so a whole-account search would silently skip them.
            var unsynced = Folders.Where(f => f.Account.Id == account.Id && f.Folder.HighestUid == 0 && f.Kind != FolderKind.Trash && f.Kind != FolderKind.Junk).ToList();
            if (unsynced.Count > 0)
            {
                await RunBusyAsync("Syncing folders for search...", async () =>
                {
                    foreach (var f in unsynced)
                    {
                        StatusText = $"Syncing {f.DisplayName} for search...";
                        try { await _sync.SyncFolderAsync(f.Account, f.Folder); f.UnreadCount = f.Folder.UnreadCount; }
                        catch (Exception ex) { StatusText = $"Could not sync {f.DisplayName}: {ex.Message}"; }
                    }
                });
            }
        }

        Messages.Clear();
        SelectedMessage = null;
        ListTitle = $"Search: {SearchText}";
        var folderId = SearchAllFolders ? null : SelectedFolder?.Folder.Id;
        foreach (var m in await _store.SearchAsync(SearchText, account?.Id, folderId))
            Messages.Add(m);
        var scope = SearchAllFolders ? "all folders" : (SelectedFolder?.DisplayName ?? "this folder");
        ListSubtitle = $"{Messages.Count} result{(Messages.Count == 1 ? "" : "s")} in {scope}";
        StatusText = ListSubtitle;
    }

    // ---------------- Message actions ----------------

    [RelayCommand]
    private async Task ToggleFlagAsync(MessageSummary? message)
    {
        if (message is null || SelectedFolder is null) return;
        message.IsFlagged = !message.IsFlagged;
        await _sync.SetFlagsAsync(SelectedFolder.Account, message, isFlagged: message.IsFlagged);
        var idx = Messages.IndexOf(message);
        if (idx >= 0) { Messages[idx] = message; }
    }

    [RelayCommand]
    private async Task MoveToTrashAsync(MessageSummary? message)
    {
        if (message is null || SelectedFolder is null) return;
        var trash = Folders.FirstOrDefault(f => f.Account.Id == SelectedFolder.Account.Id && f.Kind == FolderKind.Trash);
        if (trash is null) { StatusText = "No Trash folder found for this account."; return; }
        await RunBusyAsync("Moving to Trash...", async () =>
        {
            await _sync.MoveAsync(SelectedFolder.Account, [message], trash.Folder);
            Messages.Remove(message);
        });
    }

    /// <summary>Whether a drag of messages may land on this folder: same account, and a folder that can hold moved mail.</summary>
    public bool CanDropOn(FolderItem target) =>
        SelectedFolder is not null && target.Account.Id == SelectedFolder.Account.Id && target.Kind != FolderKind.Drafts;

    /// <summary>Drag-and-drop move. Messages already in the target are skipped; counts update without a full reload so search results stay put.</summary>
    public async Task MoveMessagesAsync(IReadOnlyList<MessageSummary> messages, FolderItem target)
    {
        var toMove = messages.Where(m => m.FolderId != target.Folder.Id).ToList();
        if (toMove.Count == 0 || !CanDropOn(target)) return;
        var label = toMove.Count == 1 ? $"\"{toMove[0].Subject}\"" : $"{toMove.Count} messages";
        await RunBusyAsync($"Moving {label} to {target.DisplayName}...", async () =>
        {
            await _sync.MoveAsync(target.Account, toMove, target.Folder);
            foreach (var m in toMove)
            {
                Messages.Remove(m);
                if (m.IsRead) continue;
                var source = Folders.FirstOrDefault(f => f.Folder.Id == m.FolderId);
                if (source is not null) source.UnreadCount = Math.Max(0, source.UnreadCount - 1);
                target.UnreadCount++;
            }
            StatusText = $"Moved {label} to {target.DisplayName}.";
        });
    }

    // ---------------- Rules ----------------

    private IReadOnlyList<FolderChoice> FolderChoicesFor(Account account) =>
        Folders.Where(f => f.Account.Id == account.Id && f.Kind is FolderKind.Other or FolderKind.Archive or FolderKind.Junk)
               .Select(f => new FolderChoice(f.Folder.FullName, f.DisplayName)).ToList();

    public async Task LoadRulesAsync()
    {
        Rules.Clear();
        var account = CurrentAccount;
        if (account is null) return;
        foreach (var r in await _store.GetRulesAsync(account.Id)) Rules.Add(r);
    }

    [RelayCommand]
    private async Task OpenRulesAsync()
    {
        if (RequestRulesWindow is null || CurrentAccount is null) return;
        await LoadRulesAsync();
        await RequestRulesWindow();
    }

    [RelayCommand]
    private Task NewRuleAsync() => EditRuleAsync(null);

    [RelayCommand]
    private async Task EditRuleAsync(MailRule? rule)
    {
        var account = CurrentAccount;
        if (RequestRuleEditor is null || account is null) return;
        var result = await RequestRuleEditor(rule, account.Id, FolderChoicesFor(account));
        if (result is null) return;
        var (saved, applyNow) = result.Value;
        await _store.UpsertRuleAsync(saved);
        await LoadRulesAsync();
        StatusText = $"Rule saved: {saved.Name}";
        if (applyNow) await ApplyRulesToCurrentFolderAsync([saved]);
    }

    [RelayCommand]
    private async Task CreateRuleFromSenderAsync(MessageSummary? message)
    {
        if (message is null) return;
        var seed = new MailRule
        {
            Name = "",
            Conditions = [new RuleCondition(RuleField.FromAddress, RuleOperator.Equals, message.FromAddress)],
            Action = RuleAction.MoveToFolder,
        };
        await EditRuleAsync(seed);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(MailRule? rule)
    {
        if (rule is null) return;
        await _store.DeleteRuleAsync(rule.Id);
        Rules.Remove(rule);
        StatusText = $"Rule deleted: {rule.Name}";
    }

    [RelayCommand]
    private async Task ToggleRuleEnabledAsync(MailRule? rule)
    {
        if (rule is null) return;
        rule.Enabled = !rule.Enabled;
        await _store.UpsertRuleAsync(rule);
        await LoadRulesAsync();
    }

    [RelayCommand]
    private Task RunRulesNowAsync() => ApplyRulesToCurrentFolderAsync(null);

    private async Task ApplyRulesToCurrentFolderAsync(IReadOnlyList<MailRule>? only)
    {
        if (SelectedFolder is null) return;
        var folder = SelectedFolder;
        await RunBusyAsync($"Running rules on {folder.DisplayName}...", async () =>
        {
            var candidates = await _store.GetMessagesAsync(folder.Folder.Id, limit: 10_000);
            var result = await _rules.RunAsync(folder.Account, candidates, only);
            StatusText = result.Summary();
            await LoadRulesAsync();
            if (result.Moved > 0) await ReloadFoldersKeepingSelectionAsync();
            await LoadMessagesAsync();
        });
    }

    // ---------------- Agent ----------------

    [RelayCommand]
    private async Task RunTriageAsync()
    {
        if (SelectedFolder is null) return;
        IsAgentPaneOpen = true;
        AgentSummary = "";
        await RunBusyAsync($"{_agent.Name}: reviewing {SelectedFolder.DisplayName}...", async () =>
        {
            Proposals.Clear();
            var proposals = await _agent.ProposeTriageAsync(SelectedFolder.Account.Id, SelectedFolder.Folder.Id);
            foreach (var p in proposals) Proposals.Add(p);
            AgentSummary = _agent.LastRunNote ?? "";
            StatusText = proposals.Count == 0 ? $"{_agent.Name}: nothing to tidy." : $"{_agent.Name}: {proposals.Count} proposals ready for review.";
        });
    }

    [RelayCommand]
    private async Task ApproveProposalAsync(AgentProposal? proposal)
    {
        if (proposal is null) return;
        proposal.Status = ProposalStatus.Approved;
        await RunBusyAsync($"Applying: {proposal.Title}", async () =>
        {
            try
            {
                await _applier.ApplyAsync(proposal);
                StatusText = $"Done: {proposal.Title}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed: {ex.Message}";
            }
            Proposals.Remove(proposal);
            if (proposal.Kind == ProposalKind.CreateRule) await LoadRulesAsync();
            else if (proposal.Kind == ProposalKind.CreateTask) StatusText = $"Added {proposal.Count} task{(proposal.Count == 1 ? "" : "s")}. Open Tasks to see them.";
            else { await ReloadFoldersKeepingSelectionAsync(); await LoadMessagesAsync(); }
        });
    }

    [RelayCommand]
    private void RejectProposal(AgentProposal? proposal)
    {
        if (proposal is null) return;
        proposal.Status = ProposalStatus.Rejected;
        Proposals.Remove(proposal);
    }

    [RelayCommand]
    private void ToggleAgentPane() => IsAgentPaneOpen = !IsAgentPaneOpen;

    private async Task RunBusyAsync(string status, Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = status;
        try { await work(); }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsBusy = false; if (StatusText == status) StatusText = "Ready"; }
    }
}
