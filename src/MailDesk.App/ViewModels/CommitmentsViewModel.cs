using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.App.ViewModels;

/// <summary>A fact row with a done checkbox that writes through to the store.</summary>
public sealed partial class FactRow : ObservableObject
{
    private readonly Func<FactRow, bool, Task> _onDone;

    public FactRow(ThreadFact fact, Func<FactRow, bool, Task> onDone)
    {
        Fact = fact;
        _onDone = onDone;
        _isDone = fact.IsDone;
    }

    public ThreadFact Fact { get; }
    public string Text => Fact.Text;
    public string Subject => Fact.Subject;
    public string Who => Fact.Who;
    public bool IsInferred => Fact.IsInferred;
    public bool HasDue => !string.IsNullOrWhiteSpace(Fact.Due) || Fact.DueUtcMs is not null;
    public string DueText => Fact.DueText;
    public bool IsOverdue => Fact.IsOverdue;
    public bool HasSource => Fact.MessageId is not null;
    public bool CanAddTask => Fact.Kind == "commitment" && Fact.IsYours;
    /// <summary>Decisions have no done state.</summary>
    public bool Checkable => Fact.Kind != "decision";
    public string Meta => Fact.Kind switch
    {
        "commitment" => Fact.IsYours ? $"You promised · {Fact.Subject}" : $"{Fact.Who} promised · {Fact.Subject}",
        "question" => $"{Fact.Who} is waiting · {Fact.Subject}",
        _ => string.IsNullOrWhiteSpace(Fact.Who) ? Fact.Subject : $"{Fact.Who} · {Fact.Subject}",
    };

    [ObservableProperty] private bool _isDone;

    partial void OnIsDoneChanged(bool value) => _ = _onDone(this, value);
}

/// <summary>Everything analysed conversations established, across the account: what you owe, what is owed to you, what is open, what was decided.</summary>
public sealed partial class CommitmentsViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly Account _account;

    public CommitmentsViewModel(IMailStore store, Account account)
    {
        _store = store;
        _account = account;
    }

    public ObservableCollection<FactRow> YouOwe { get; } = [];
    public ObservableCollection<FactRow> OwedToYou { get; } = [];
    public ObservableCollection<FactRow> OpenQuestions { get; } = [];
    public ObservableCollection<FactRow> Decisions { get; } = [];

    [ObservableProperty] private bool _showDone;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isEmpty;

    /// <summary>Set by the main window so a fact's source message can be opened.</summary>
    public Func<long, Task>? RequestOpenMessage { get; set; }

    partial void OnShowDoneChanged(bool value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        YouOwe.Clear(); OwedToYou.Clear(); OpenQuestions.Clear(); Decisions.Clear();
        var facts = await _store.GetThreadFactsAsync(_account.Id, includeDone: ShowDone);
        foreach (var f in facts)
        {
            var row = new FactRow(f, OnDoneChangedAsync);
            switch (f.Kind)
            {
                case "commitment": (f.IsYours ? YouOwe : OwedToYou).Add(row); break;
                case "question": OpenQuestions.Add(row); break;
                default: Decisions.Add(row); break;
            }
        }
        IsEmpty = facts.Count == 0;
        var overdue = facts.Count(f => f.IsOverdue);
        StatusText = $"{YouOwe.Count} you owe, {OwedToYou.Count} owed to you, {OpenQuestions.Count} open" + (overdue > 0 ? $", {overdue} overdue" : "")
                     + $". From {facts.Select(f => f.ThreadKey).Distinct().Count()} analysed conversation{(facts.Select(f => f.ThreadKey).Distinct().Count() == 1 ? "" : "s")}.";
    }

    private async Task OnDoneChangedAsync(FactRow row, bool done)
    {
        if (row.Fact.IsDone == done) return;
        row.Fact.IsDone = done;
        await _store.SetThreadFactDoneAsync(row.Fact.Id, done);
        if (done && !ShowDone)
        {
            await Task.Delay(350);
            YouOwe.Remove(row); OwedToYou.Remove(row); OpenQuestions.Remove(row);
        }
        StatusText = done ? $"Done: {row.Text}" : $"Reopened: {row.Text}";
    }

    [RelayCommand]
    private async Task OpenSourceAsync(FactRow? row)
    {
        if (row?.Fact.MessageId is { } id && RequestOpenMessage is not null) await RequestOpenMessage(id);
    }

    [RelayCommand]
    private async Task AddTaskAsync(FactRow? row)
    {
        if (row is null) return;
        var f = row.Fact;
        await _store.UpsertTaskAsync(new TaskItem
        {
            AccountId = _account.Id, Title = f.Text, DueUtcMs = f.DueUtcMs, Notes = $"Promised in: {f.Subject}",
            SourceMessageId = f.MessageId, SourceSubject = f.Subject, CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        StatusText = $"Task added: {f.Text}";
    }
}
