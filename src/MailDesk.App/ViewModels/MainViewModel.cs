using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Agent;
using MailDesk.App.Services;
using MailDesk.Core.Models;
using MailDesk.Core.Rules;
using MailDesk.Core.Security;
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
    /// <summary>Smart views (Priority, Waiting, Newsletters, Notifications), one set per account, shown above the folders.</summary>
    public ObservableCollection<FolderItem> Views { get; } = [];
    public ObservableCollection<MessageSummary> Messages { get; } = [];
    public ObservableCollection<AgentProposal> Proposals { get; } = [];
    public ObservableCollection<MailRule> Rules { get; } = [];

    [ObservableProperty] private FolderItem? _selectedFolder;
    /// <summary>The list selection. Normally also the message in the reader; see <see cref="ReaderMessage"/>.</summary>
    [ObservableProperty] private MessageSummary? _selectedMessage;
    /// <summary>The message open in the reader. Follows the selection, or a conversation entry clicked in the timeline (which may live in another folder).</summary>
    [ObservableProperty] private MessageSummary? _readerMessage;
    /// <summary>One row per conversation in folder lists (newest message of each thread). Persisted as a setting.</summary>
    [ObservableProperty] private bool _showConversations;
    private bool _loadingSettings;

    /// <summary>The conversation around <see cref="ReaderMessage"/>, oldest first, across folders.</summary>
    public ObservableCollection<ThreadEntry> Thread { get; } = [];
    [ObservableProperty] private bool _hasThread;
    [ObservableProperty] private string _threadTitle = "";
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

    /// <summary>Raised by the view to show the commitments window.</summary>
    public Func<Account, Task>? RequestCommitments { get; set; }

    // ---------------- Reminders ----------------

    /// <summary>Raised by the view to show the reminders window.</summary>
    public Func<Account, Task>? RequestReminders { get; set; }

    /// <summary>Rule-based: unanswered sent mail, overdue commitments and tasks. Shared with the reminders window.</summary>
    public ReminderService Reminders => _reminders ??= new ReminderService(_store);
    private ReminderService? _reminders;

    [ObservableProperty] private int _reminderCount;
    public bool HasReminders => ReminderCount > 0;
    partial void OnReminderCountChanged(int value) => OnPropertyChanged(nameof(HasReminders));

    [RelayCommand]
    private async Task OpenRemindersAsync()
    {
        var account = CurrentAccount;
        if (RequestReminders is null || account is null) { StatusText = "Add an account first."; return; }
        await RequestReminders(account);
    }

    /// <summary>Recounts the badge. Called on load, after syncs that can change Waiting, and when the reminders window changes something.</summary>
    public async Task RefreshRemindersAsync(bool announce = false)
    {
        var account = CurrentAccount;
        if (account is null) { ReminderCount = 0; return; }
        try
        {
            var items = await Reminders.ComputeAsync(account, DateTimeOffset.UtcNow);
            ReminderCount = items.Count;
            if (announce && items.Count > 0)
            {
                var waiting = items.Count(r => r.Kind == ReminderKind.NoReply);
                var overdue = items.Count - waiting;
                StatusText = $"{items.Count} reminder{(items.Count == 1 ? "" : "s")}: "
                             + string.Join(", ", new[] { waiting > 0 ? $"{waiting} waiting on a reply" : null, overdue > 0 ? $"{overdue} overdue" : null }.Where(s => s is not null))
                             + ". Click the bell.";
            }
        }
        catch { /* a badge; never let it break loading */ }
    }

    /// <summary>Opens a follow-up draft for a reminder. Returns true when the compose window reported it was sent or saved.</summary>
    public async Task<bool> NudgeAsync(Reminder reminder)
    {
        var account = CurrentAccount;
        if (reminder.Message is null || account is null || RequestCompose is null) return false;
        MessageBody? body = null;
        try { body = await _sync.FetchBodyAsync(account, reminder.Message); } catch { /* quote the snippet instead */ }
        body ??= new MessageBody { MessageId = reminder.Message.Id, TextBody = reminder.Message.Snippet };
        var about = reminder.Fact is { } f ? $"what you mentioned about \"{f.Text}\"" : null;
        var seed = Core.Text.ComposeBuilder.Nudge(reminder.Message, body, account.Email, account.Name, about);
        var outcome = await RequestCompose(account, seed);
        if (outcome is null) return false;
        StatusText = outcome;
        return true;
    }

    [RelayCommand]
    private async Task OpenCommitmentsAsync()
    {
        var account = CurrentAccount;
        if (RequestCommitments is null || account is null) { StatusText = "Add an account first."; return; }
        await RequestCommitments(account);
    }

    /// <summary>Conversations analysed before the facts table existed are loaded into it from their cached JSON, once.</summary>
    private async Task BackfillFactsAsync()
    {
        const string flag = "facts.backfilled";
        if (await _store.GetSettingAsync(flag) == "1") return;
        try
        {
            foreach (var (key, json) in await _store.GetSettingsByPrefixAsync(ThreadInsightKeyPrefix))
            {
                var insight = ConversationAnalyst.Deserialize(json);
                if (insight is null) continue;
                var threadKey = key[ThreadInsightKeyPrefix.Length..];
                foreach (var account in Accounts)
                {
                    var rows = await _store.GetThreadAsync(account.Id, threadKey);
                    if (rows.Count == 0) continue;
                    insight.ThreadKey = threadKey;
                    await _store.ReplaceThreadFactsAsync(account.Id, threadKey, insight.ToFacts(account.Id, rows[0].Subject));
                    break;
                }
            }
            await _store.SetSettingAsync(flag, "1");
        }
        catch { /* best effort; the next analysis writes facts directly */ }
    }

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
        var inList = Messages.FirstOrDefault(m => m.Id == messageId);
        if (inList is not null) SelectedMessage = inList;
        else ReaderMessage = message; // e.g. collapsed inside a conversation row: show it without disturbing the list
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
        if (ReaderMessage is null || SelectedFolder is null) return;
        var body = await _store.GetBodyAsync(ReaderMessage.Id);
        await OpenComposeAsync(Core.Text.ComposeBuilder.Forward(ReaderMessage, body));
    }

    private async Task ReplyCoreAsync(bool replyAll)
    {
        if (ReaderMessage is null || SelectedFolder is null) return;
        var body = await _store.GetBodyAsync(ReaderMessage.Id);
        await OpenComposeAsync(Core.Text.ComposeBuilder.Reply(ReaderMessage, body, SelectedFolder.Account.Email, replyAll));
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
        Views.Clear();
        foreach (var a in await _store.GetAccountsAsync())
        {
            Accounts.Add(a);
            foreach (var f in await _store.GetFoldersAsync(a.Id))
                Folders.Add(new FolderItem(a, f));
            foreach (var v in Enum.GetValues<SmartView>())
                Views.Add(new FolderItem(a, v));
        }
        _ = RefreshViewCountsAsync();
        _loadingSettings = true;
        ShowConversations = await _store.GetSettingAsync(ConversationsKey) == "1";
        _loadingSettings = false;
        _ = BackfillFactsAsync();
        _ = RefreshRemindersAsync(announce: true);
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

    private const string ConversationsKey = "list.conversations";

    partial void OnShowConversationsChanged(bool value)
    {
        if (_loadingSettings) return;
        _ = _store.SetSettingAsync(ConversationsKey, value ? "1" : "0");
        if (SelectedFolder is { IsView: false }) _ = LoadMessagesAsync();
    }

    private async Task LoadMessagesAsync()
    {
        Messages.Clear();
        SelectedMessage = null;
        if (SelectedFolder is null) return;
        ListTitle = SelectedFolder.DisplayName;

        if (SelectedFolder.View is { } view)
        {
            // Waiting is built from Sent, which has nothing cached until it has been opened once. Fetch it the first time.
            if (view == SmartView.Waiting && !IsBusy)
            {
                var current = SelectedFolder;
                var unsynced = SourceFoldersFor(current).Where(f => f.Folder.HighestUid == 0).ToList();
                if (unsynced.Count > 0)
                {
                    await RunBusyAsync("Loading Sent mail for the first time...", async () => { foreach (var f in unsynced) await SyncFolderAsync(f); });
                    if (!ReferenceEquals(current, SelectedFolder)) return;
                }
            }

            var rows = await _store.GetSmartViewAsync(SelectedFolder.Account.Id, view);
            foreach (var m in rows) Messages.Add(m);
            var unread = rows.Count(m => !m.IsRead);
            ListSubtitle = view switch
            {
                SmartView.Waiting => rows.Count == 0 ? "Nobody owes you a reply." : $"{rows.Count} sent message{(rows.Count == 1 ? "" : "s")} waiting for a reply",
                _ => $"{rows.Count} messages, {unread} unread. {SelectedFolder.Description}",
            };
            SelectedFolder.UnreadCount = view == SmartView.Waiting ? rows.Count : unread;
            return;
        }

        var list = ShowConversations
            ? await _store.GetThreadsAsync(SelectedFolder.Folder.Id)
            : await _store.GetMessagesAsync(SelectedFolder.Folder.Id);
        foreach (var m in list)
        {
            if (SelectedFolder.Kind == FolderKind.Sent) m.ShowRecipients = true;
            Messages.Add(m);
        }
        ListSubtitle = ShowConversations
            ? $"{Messages.Count} conversation{(Messages.Count == 1 ? "" : "s")}, {SelectedFolder.UnreadCount} unread"
            : $"{Messages.Count} messages, {SelectedFolder.UnreadCount} unread";
    }

    /// <summary>Badge counts for the views: unread for Priority, items for Waiting. Newsletters and Notifications stay quiet on purpose.</summary>
    private async Task RefreshViewCountsAsync()
    {
        foreach (var v in Views.ToList())
        {
            try
            {
                if (v.View == SmartView.Priority) v.UnreadCount = (await _store.GetSmartViewAsync(v.Account.Id, SmartView.Priority)).Count(m => !m.IsRead);
                else if (v.View == SmartView.Waiting) v.UnreadCount = (await _store.GetSmartViewAsync(v.Account.Id, SmartView.Waiting)).Count;
            }
            catch { /* a count is decoration; never let it break loading */ }
        }
        await RefreshRemindersAsync();
    }

    /// <summary>The server folders a smart view is built from, so Sync can refresh them.</summary>
    private IEnumerable<FolderItem> SourceFoldersFor(FolderItem view)
    {
        var kinds = view.View == SmartView.Waiting ? new[] { FolderKind.Sent, FolderKind.Inbox } : [FolderKind.Inbox];
        return kinds.Select(k => Folders.FirstOrDefault(f => f.Account.Id == view.Account.Id && f.Kind == k)).Where(f => f is not null)!;
    }

    partial void OnSelectedMessageChanged(MessageSummary? value)
    {
        // Clearing the list selection (folder change, reload) clears the reader; a null from a peeked message does not.
        if (value is not null || ReaderMessage is null || Messages.Contains(ReaderMessage) || Messages.Count == 0)
            ReaderMessage = value;
    }

    partial void OnReaderMessageChanged(MessageSummary? value)
    {
        _ = ShowMessageAsync(value);
    }

    /// <summary>Show a message from the conversation timeline. Stays in the list when it is there; otherwise the reader shows it alone.</summary>
    [RelayCommand]
    private void OpenThreadEntry(ThreadEntry? entry)
    {
        if (entry is null || entry.IsCurrent) return;
        ShowInPlace(entry.Message);
    }

    /// <summary>Selects the message when the list holds it, otherwise shows it in the reader without touching the list.</summary>
    private void ShowInPlace(MessageSummary message)
    {
        var inList = Messages.FirstOrDefault(m => m.Id == message.Id);
        if (inList is not null) SelectedMessage = inList;
        else ReaderMessage = message;
    }

    // ---- Decisions, commitments and open questions per conversation ----

    [ObservableProperty] private ConversationInsight? _threadInsight;
    [ObservableProperty] private bool _isAnalysingThread;
    private const string ThreadInsightKeyPrefix = "thread.insight:";
    private IReadOnlyList<MessageSummary> _threadRows = [];

    /// <summary>Sends the whole conversation to the model and shows what was decided, promised and asked, linked to the source messages.</summary>
    [RelayCommand]
    private async Task AnalyseThreadAsync()
    {
        if (ReaderMessage is null || SelectedFolder is null || RequestProtector is null || _threadRows.Count == 0) return;
        var message = ReaderMessage;
        var account = SelectedFolder.Account;
        var rows = _threadRows;
        IsAnalysingThread = true;
        try
        {
            var client = await ModelClients.CreateAsync(_store, RequestProtector());
            var bodies = new Dictionary<long, string>();
            foreach (var m in rows)
            {
                StatusText = $"Reading {bodies.Count + 1} of {rows.Count} messages...";
                try { bodies[m.Id] = Core.Text.ComposeBuilder.BodyText(await _sync.FetchBodyAsync(account, m)); }
                catch { bodies[m.Id] = m.Snippet; }
            }
            StatusText = "Analysing the conversation...";
            var insight = await ConversationAnalyst.AnalyzeAsync(client, account.Name, account.Email, rows, bodies);
            await _store.SetSettingAsync(ThreadInsightKeyPrefix + insight.ThreadKey, ConversationAnalyst.Serialize(insight));
            await _store.ReplaceThreadFactsAsync(account.Id, insight.ThreadKey, insight.ToFacts(account.Id, rows[0].Subject));
            if (ReferenceEquals(message, ReaderMessage) || ReaderMessage?.ThreadKey == insight.ThreadKey) ThreadInsight = insight;
            StatusText = $"Conversation analysed with {insight.ModelNote}: {insight.Counts}.";
        }
        catch (Exception ex) { StatusText = $"Analyse failed: {ex.Message}"; }
        finally { IsAnalysingThread = false; }
    }

    [RelayCommand]
    private void CloseThreadInsight() => ThreadInsight = null;

    /// <summary>Opens the message a decision, commitment or question came from.</summary>
    [RelayCommand]
    private async Task OpenInsightSourceAsync(ThreadItem? item)
    {
        if (item?.MessageId is not { } id) return;
        var inThread = _threadRows.FirstOrDefault(m => m.Id == id) ?? await _store.GetMessageAsync(id);
        if (inThread is null) { StatusText = "That message is no longer in the local cache."; return; }
        ShowInPlace(inThread);
    }

    /// <summary>A commitment of yours becomes a task with its due date and a link to the message it was made in.</summary>
    [RelayCommand]
    private async Task AddCommitmentTaskAsync(ThreadItem? item)
    {
        if (item is null || SelectedFolder is null) return;
        var source = item.MessageId is { } id ? _threadRows.FirstOrDefault(m => m.Id == id) : null;
        var task = new TaskItem
        {
            AccountId = SelectedFolder.Account.Id, Title = item.Text, DueUtcMs = item.DueDate?.ToUnixTimeMilliseconds(),
            Notes = source is null ? "Promised in a conversation" : $"Promised to {source.ToAddresses}",
            SourceMessageId = source?.Id, SourceSubject = source?.Subject ?? ReaderMessage?.Subject ?? "",
            CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await _store.UpsertTaskAsync(task);
        StatusText = $"Task added: {task.Title}";
    }

    private async Task LoadThreadAsync(MessageSummary message)
    {
        Thread.Clear();
        HasThread = false;
        ThreadInsight = null;
        _threadRows = [];
        if (SelectedFolder is null || string.IsNullOrEmpty(message.ThreadKey)) return;
        var account = SelectedFolder.Account;
        var rows = await _store.GetThreadAsync(account.Id, message.ThreadKey);
        if (!ReferenceEquals(message, ReaderMessage) || rows.Count < 2) return;
        _threadRows = rows;

        // A cached analysis is shown again until the thread grows.
        var cached = ConversationAnalyst.Deserialize(await _store.GetSettingAsync(ThreadInsightKeyPrefix + message.ThreadKey));
        if (cached is not null && cached.MessageCount == rows.Count && ReferenceEquals(message, ReaderMessage)) ThreadInsight = cached;
        foreach (var r in rows)
            Thread.Add(new ThreadEntry(r, r.Id == message.Id, r.FromAddress.Equals(account.Email, StringComparison.OrdinalIgnoreCase)));
        var mine = rows.Count(r => r.FromAddress.Equals(account.Email, StringComparison.OrdinalIgnoreCase));
        var others = rows.Select(r => r.FromAddress.ToLowerInvariant()).Where(a => a != account.Email.ToLowerInvariant()).Distinct().Count();
        var span = rows[^1].Date - rows[0].Date;
        var spanText = span.TotalDays >= 1 ? $"over {Math.Max(1, (int)Math.Round(span.TotalDays))} day{((int)Math.Round(span.TotalDays) == 1 ? "" : "s")}" : "today";
        ThreadTitle = $"Conversation: {rows.Count} messages {spanText}, {mine} from you, {others} other {(others == 1 ? "person" : "people")}";
        HasThread = true;
    }

    private async Task ShowMessageAsync(MessageSummary? message)
    {
        if (message is null || SelectedFolder is null)
        {
            ReaderHtml = MailHtml.Empty();
            Insight = null;
            Thread.Clear();
            HasThread = false;
            return;
        }
        try
        {
            ReaderHtml = MailHtml.Empty("Loading...");
            Invite = null;
            Insight = null;
            HasBlockedImages = false;
            _ = LoadThreadAsync(message);
            var body = await _sync.FetchBodyAsync(SelectedFolder.Account, message);
            if (!ReferenceEquals(message, ReaderMessage)) return; // user moved on while the body was downloading
            _currentBody = body;
            var allowImages = await IsSenderAllowedImagesAsync(message.FromAddress);
            RenderBody(allowImages);
            await LoadInviteAsync(message, body);

            if (!message.IsRead)
            {
                message.IsRead = true;
                await _sync.SetFlagsAsync(SelectedFolder.Account, message, isRead: true);
                var home = Folders.FirstOrDefault(f => f.Folder.Id == message.FolderId) ?? SelectedFolder;
                home.UnreadCount = Math.Max(0, home.UnreadCount - 1);
                if (SelectedFolder.IsView && !ReferenceEquals(home, SelectedFolder) && SelectedFolder.View != SmartView.Waiting)
                    SelectedFolder.UnreadCount = Math.Max(0, SelectedFolder.UnreadCount - 1);
                foreach (var t in Thread.Where(t => t.Message.Id == message.Id).ToList())
                {
                    var i = Thread.IndexOf(t);
                    Thread[i] = new ThreadEntry(t.Message, t.IsCurrent, t.IsMine); // refresh the unread dot
                }
            }
        }
        catch (Exception ex)
        {
            ReaderHtml = MailHtml.Empty($"Could not load message: {ex.Message}");
        }
    }

    // ---------------- AI: summarise and ask ----------------

    /// <summary>Supplied by the view: the credential protector, needed to build a model client on demand.</summary>
    public Func<ICredentialProtector>? RequestProtector { get; set; }

    [ObservableProperty] private MessageInsight? _insight;
    [ObservableProperty] private bool _isSummarising;

    /// <summary>One click: the open message's text goes to the model and comes back as a summary, to-dos and reply options.</summary>
    [RelayCommand]
    private async Task SummariseAsync()
    {
        if (ReaderMessage is null || SelectedFolder is null || RequestProtector is null) return;
        var message = ReaderMessage;
        var account = SelectedFolder.Account;
        IsSummarising = true;
        try
        {
            var client = await ModelClients.CreateAsync(_store, RequestProtector());
            var body = _currentBody ?? await _sync.FetchBodyAsync(account, message);
            var text = Core.Text.ComposeBuilder.BodyText(body);
            if (string.IsNullOrWhiteSpace(text)) text = message.Snippet;
            var insight = await InsightsService.AnalyzeAsync(client, account.Name, message, text);
            if (ReferenceEquals(message, ReaderMessage)) Insight = insight;
            StatusText = $"Summarised with {insight.ModelNote}.";
        }
        catch (Exception ex) { StatusText = $"Summarise failed: {ex.Message}"; }
        finally { IsSummarising = false; }
    }

    [RelayCommand]
    private void CloseInsight() => Insight = null;

    /// <summary>An action item from the summary becomes a task linked to the message.</summary>
    [RelayCommand]
    private async Task AddActionTaskAsync(string? item)
    {
        if (string.IsNullOrWhiteSpace(item) || ReaderMessage is null || SelectedFolder is null) return;
        await _store.UpsertTaskAsync(new TaskItem
        {
            AccountId = SelectedFolder.Account.Id, Title = item.Trim(), Notes = $"From {ReaderMessage.FromDisplay}",
            SourceMessageId = ReaderMessage.Id, SourceSubject = ReaderMessage.Subject,
        });
        StatusText = $"Task added: {item.Trim()}";
    }

    /// <summary>A suggested reply opens the compose window as a reply with that text above the quote. The user still sends.</summary>
    [RelayCommand]
    private async Task UseSuggestedReplyAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || ReaderMessage is null || SelectedFolder is null) return;
        var body = await _store.GetBodyAsync(ReaderMessage.Id);
        var seed = Core.Text.ComposeBuilder.Reply(ReaderMessage, body, SelectedFolder.Account.Email, replyAll: false);
        seed.TextBody = text.Trim() + seed.TextBody;
        await OpenComposeAsync(seed);
    }

    public ObservableCollection<ChatEntry> Chat { get; } = [];
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private bool _isAsking;

    /// <summary>Ask your mailbox: the question, prior turns and the model's tool reads stay in this session only.</summary>
    [RelayCommand]
    private async Task AskAsync()
    {
        var question = Question.Trim();
        var account = CurrentAccount;
        if (question.Length == 0 || account is null || RequestProtector is null || IsAsking) return;
        IsAgentPaneOpen = true;
        var history = Chat.Select(c => new ChatTurn(c.FromUser, c.Text)).ToList();
        Chat.Add(new ChatEntry { FromUser = true, Text = question });
        Question = "";
        IsAsking = true;
        try
        {
            var client = await ModelClients.CreateAsync(_store, RequestProtector());
            var answer = await new MailboxAssistant(_store, _sync).AskAsync(client, account, history, question);
            Chat.Add(new ChatEntry
            {
                FromUser = false, Text = answer.Text, Sources = answer.Sources.ToList(), Note = answer.ModelNote,
                Events = answer.SuggestedEvents.Select(e => new OfferedEvent(e.Event, e.MessageId)).ToList(),
                Tasks = answer.SuggestedTasks.Select(t => new OfferedTask(t)).ToList(),
            });
        }
        catch (Exception ex)
        {
            Chat.Add(new ChatEntry { FromUser = false, Text = $"Sorry, that failed: {ex.Message}", IsError = true });
        }
        finally { IsAsking = false; }
    }

    [RelayCommand]
    private void ClearChat() => Chat.Clear();

    /// <summary>Saves a suggested event as a private calendar entry organised by the user. Nothing is sent.</summary>
    private async Task<CalendarItem?> SaveSuggestedEventAsync(EventSuggestion suggestion, long? sourceMessageId)
    {
        var account = CurrentAccount;
        if (account is null || !suggestion.IsValid) return null;
        var item = suggestion.ToCalendarItem(account.Id, Calendar.IcsCodec.NewUid(account.Email), account.Email, account.Name, sourceMessageId);
        item.RawIcs = Calendar.IcsCodec.BuildRequest(item);
        var saved = await _store.UpsertCalendarItemAsync(item);
        StatusText = $"Added to calendar: {saved.Summary}, {saved.WhenText()}";
        return saved;
    }

    /// <summary>Summary card: a date the model found in the open message.</summary>
    [RelayCommand]
    private async Task AddInsightEventAsync(EventSuggestion? suggestion)
    {
        if (suggestion is null) return;
        var saved = await SaveSuggestedEventAsync(suggestion, ReaderMessage?.Id);
        if (saved is not null && Insight is { } insight) { insight.Events.Remove(suggestion); OnPropertyChanged(nameof(Insight)); Insight = insight; }
    }

    /// <summary>Chat: an event the assistant offered.</summary>
    [RelayCommand]
    private async Task AddOfferedEventAsync(OfferedEvent? offered)
    {
        if (offered is null || offered.Added) return;
        if (await SaveSuggestedEventAsync(offered.Suggestion, offered.MessageId) is not null) offered.Added = true;
    }

    /// <summary>Chat: a task the assistant offered.</summary>
    [RelayCommand]
    private async Task AddOfferedTaskAsync(OfferedTask? offered)
    {
        if (offered is null || offered.Added || CurrentAccount is null) return;
        offered.Task.AccountId = CurrentAccount.Id;
        await _store.UpsertTaskAsync(offered.Task);
        offered.Added = true;
        StatusText = $"Task added: {offered.Title}";
    }

    [RelayCommand]
    private Task OpenSourceMessageAsync(MessageSummary? message) => message is null ? Task.CompletedTask : OpenMessageAsync(message.Id);

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
        var from = ReaderMessage?.FromAddress;
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
        if (SelectedFolder.IsView)
        {
            var view = SelectedFolder;
            await RunBusyAsync($"Refreshing {view.DisplayName}...", async () =>
            {
                foreach (var source in SourceFoldersFor(view)) await SyncFolderAsync(source);
                if (ReferenceEquals(view, SelectedFolder)) await LoadMessagesAsync();
                await RefreshViewCountsAsync();
            });
            return;
        }
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
        else if (item.Kind is FolderKind.Inbox or FolderKind.Sent) _ = RefreshViewCountsAsync();
    }

    private async Task ReloadFoldersKeepingSelectionAsync()
    {
        var selectedName = SelectedFolder?.Folder.FullName;
        var selectedAccount = SelectedFolder?.Account.Id;
        await LoadAsync();
        if (selectedName is not null)
            SelectedFolder = Folders.Concat(Views).FirstOrDefault(f => f.Account.Id == selectedAccount && f.Folder.FullName == selectedName) ?? SelectedFolder;
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
        long? folderId = SearchAllFolders || SelectedFolder is null or { IsView: true } ? null : SelectedFolder.Folder.Id;
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
    private Task MoveToTrashAsync(MessageSummary? message) => TrashMessagesAsync(message is null ? [] : [message]);

    /// <summary>
    /// Move one or more messages to the account's Trash folder (never a permanent delete). Afterwards the message that
    /// followed the selection is selected so Delete can be pressed repeatedly to work down the list.
    /// </summary>
    public async Task TrashMessagesAsync(IReadOnlyList<MessageSummary> messages)
    {
        if (messages.Count == 0 || SelectedFolder is null) return;
        var trash = Folders.FirstOrDefault(f => f.Account.Id == SelectedFolder.Account.Id && f.Kind == FolderKind.Trash);
        if (trash is null) { StatusText = "No Trash folder found for this account."; return; }

        var toMove = messages.Where(m => m.FolderId != trash.Folder.Id).ToList();
        if (toMove.Count == 0)
        {
            StatusText = messages.Count == 1 ? "That message is already in Trash." : "Those messages are already in Trash.";
            return;
        }

        var positions = toMove.Select(Messages.IndexOf).Where(i => i >= 0).ToList();
        var nextIndex = positions.Count == 0 ? -1 : positions.Min();

        await MoveMessagesAsync(toMove, trash);

        if (nextIndex >= 0 && Messages.Count > 0 && (SelectedMessage is null || !Messages.Contains(SelectedMessage)))
            SelectedMessage = Messages[Math.Min(nextIndex, Messages.Count - 1)];
    }

    /// <summary>Whether a drag of messages may land on this folder: same account, and a folder that can hold moved mail.</summary>
    public bool CanDropOn(FolderItem target) =>
        SelectedFolder is not null && !target.IsView && target.Account.Id == SelectedFolder.Account.Id && target.Kind != FolderKind.Drafts;

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
