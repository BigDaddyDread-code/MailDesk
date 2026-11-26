using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Agent;
using MailDesk.App.Services;
using MailDesk.Core.Models;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;

namespace MailDesk.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly IMailSync _sync;
    private readonly IAgentService _agent;
    private readonly ProposalApplier _applier;

    public MainViewModel(IMailStore store, IMailSync sync, IAgentService agent, ProposalApplier applier)
    {
        _store = store;
        _sync = sync;
        _agent = agent;
        _applier = applier;
    }

    public ObservableCollection<Account> Accounts { get; } = [];
    public ObservableCollection<FolderItem> Folders { get; } = [];
    public ObservableCollection<MessageSummary> Messages { get; } = [];
    public ObservableCollection<AgentProposal> Proposals { get; } = [];

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
        await _sync.SyncFolderAsync(item.Account, item.Folder, progress: progress);
        item.UnreadCount = item.Folder.UnreadCount;
        if (ReferenceEquals(item, SelectedFolder)) await LoadMessagesAsync();
    }

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
            await LoadMessagesAsync();
            await LoadAsync();
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
