using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Core.Models;
using MailDesk.Core.Services;

namespace MailDesk.App.ViewModels;

/// <summary>A reminder row with its icon and the actions it supports.</summary>
public sealed class ReminderRow
{
    public ReminderRow(Reminder reminder) => Reminder = reminder;
    public Reminder Reminder { get; }
    public string Title => Reminder.Title;
    public string Detail => Reminder.Detail;
    public string AgeText => Reminder.AgeText;
    public bool CanNudge => Reminder.CanNudge;
    public bool CanOpen => Reminder.Message is not null || Reminder.Task is not null;
    public string Icon => Reminder.Kind switch
    {
        ReminderKind.NoReply => "",
        ReminderKind.CommitmentOverdue => "",
        ReminderKind.PromiseOverdue => "",
        _ => "",
    };
    public string KindText => Reminder.Kind switch
    {
        ReminderKind.NoReply => "Waiting on a reply",
        ReminderKind.CommitmentOverdue => "Owed to you",
        ReminderKind.PromiseOverdue => "You owe",
        _ => "Task",
    };
}

/// <summary>Unanswered sent mail, overdue commitments and overdue tasks, with Nudge, Open, Snooze and Dismiss.</summary>
public sealed partial class RemindersViewModel : ObservableObject
{
    private readonly ReminderService _service;
    private readonly Account _account;

    public RemindersViewModel(ReminderService service, Account account)
    {
        _service = service;
        _account = account;
    }

    public ObservableCollection<ReminderRow> Reminders { get; } = [];
    public IReadOnlyList<int> WaitingDayChoices { get; } = [2, 3, 5, 7, 10, 14, 21];

    [ObservableProperty] private int _waitingDays = ReminderService.DefaultWaitingDays;
    [ObservableProperty] private bool _showSnoozed;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isEmpty;
    private bool _loading;

    /// <summary>Set by the main window: open a message, open the tasks window, open a follow-up draft (true when something was sent or saved).</summary>
    public Func<long, Task>? RequestOpenMessage { get; set; }
    public Func<Task>? RequestOpenTasks { get; set; }
    public Func<Reminder, Task<bool>>? RequestNudge { get; set; }
    /// <summary>Raised after anything changed so the main window can refresh its badge.</summary>
    public Action? Changed { get; set; }

    partial void OnWaitingDaysChanged(int value)
    {
        if (_loading) return;
        _ = Task.Run(async () => { await _service.SetWaitingDaysAsync(value); }).ContinueWith(_ => LoadAsync(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    partial void OnShowSnoozedChanged(bool value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        _loading = true;
        WaitingDays = await _service.GetWaitingDaysAsync();
        _loading = false;
        Reminders.Clear();
        var items = await _service.ComputeAsync(_account, DateTimeOffset.UtcNow, includeSnoozed: ShowSnoozed);
        foreach (var r in items) Reminders.Add(new ReminderRow(r));
        IsEmpty = items.Count == 0;
        var waiting = items.Count(r => r.Kind == ReminderKind.NoReply);
        var owed = items.Count(r => r.Kind == ReminderKind.CommitmentOverdue);
        var yours = items.Count(r => r.Kind is ReminderKind.PromiseOverdue or ReminderKind.TaskOverdue);
        StatusText = items.Count == 0 ? "Nothing needs a nudge." : $"{waiting} waiting on replies, {owed} owed to you, {yours} on you.";
        Changed?.Invoke();
    }

    [RelayCommand]
    private async Task NudgeAsync(ReminderRow? row)
    {
        if (row is null || RequestNudge is null) return;
        var sent = await RequestNudge(row.Reminder);
        if (sent)
        {
            // Give them the same number of days again before asking for another nudge.
            await _service.SnoozeAsync(row.Reminder, TimeSpan.FromDays(WaitingDays));
            Reminders.Remove(row);
            StatusText = $"Follow-up sent. Reminder snoozed {WaitingDays} days.";
            Changed?.Invoke();
        }
    }

    [RelayCommand]
    private async Task OpenAsync(ReminderRow? row)
    {
        if (row is null) return;
        if (row.Reminder.Message is { } m && RequestOpenMessage is not null) await RequestOpenMessage(m.Id);
        else if (row.Reminder.Task is not null && RequestOpenTasks is not null) await RequestOpenTasks();
    }

    [RelayCommand]
    private Task Snooze3Async(ReminderRow? row) => SnoozeAsync(row, TimeSpan.FromDays(3), "3 days");

    [RelayCommand]
    private Task SnoozeWeekAsync(ReminderRow? row) => SnoozeAsync(row, TimeSpan.FromDays(7), "a week");

    [RelayCommand]
    private Task DismissAsync(ReminderRow? row) => SnoozeAsync(row, null, "for good");

    private async Task SnoozeAsync(ReminderRow? row, TimeSpan? duration, string label)
    {
        if (row is null) return;
        await _service.SnoozeAsync(row.Reminder, duration);
        if (!ShowSnoozed) Reminders.Remove(row);
        StatusText = duration is null ? $"Dismissed: {row.Title}" : $"Snoozed {label}: {row.Title}";
        Changed?.Invoke();
    }

    [RelayCommand]
    private async Task UnsnoozeAsync(ReminderRow? row)
    {
        if (row is null) return;
        await _service.UnsnoozeAsync(row.Reminder);
        StatusText = $"Back on the list: {row.Title}";
        Changed?.Invoke();
    }
}
