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
            var body = await _sync.FetchBodyAsync(SelectedFolder.Account, message);
            ReaderHtml = body.HtmlBody is { Length: > 0 } html ? MailHtml.FromHtml(html)
                : MailHtml.FromText(body.TextBody ?? "(empty message)");

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

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await LoadMessagesAsync();
            return;
        }
        Messages.Clear();
        ListTitle = $"Search: {SearchText}";
        foreach (var m in await _store.SearchAsync(SearchText, SelectedFolder?.Account.Id))
            Messages.Add(m);
        ListSubtitle = $"{Messages.Count} results";
        StatusText = $"{Messages.Count} results";
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
        await RunBusyAsync($"{_agent.Name}: reviewing {SelectedFolder.DisplayName}...", async () =>
        {
            Proposals.Clear();
            var proposals = await _agent.ProposeTriageAsync(SelectedFolder.Account.Id, SelectedFolder.Folder.Id);
            foreach (var p in proposals) Proposals.Add(p);
            StatusText = proposals.Count == 0 ? "Nothing to tidy." : $"{proposals.Count} proposals ready for review.";
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
