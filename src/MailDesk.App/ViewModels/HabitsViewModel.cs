using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.App.ViewModels;

/// <summary>One sender the user has binned mail from, with what they did and what can be done about it.</summary>
public sealed partial class HabitRow : ObservableObject
{
    public HabitRow(SenderHabit habit) { Habit = habit; }

    public SenderHabit Habit { get; }
    public string Display => Habit.Display;
    public string Address => Habit.FromAddress;
    public string Summary => Habit.Summary;
    public string Percent => $"{Habit.TrashRatio:P0} binned";
    public string When => Habit.LastTrashed is { } d ? $"last {d:d MMM}" : "";
    public int InInbox => Habit.InInbox;
    public bool HasInbox => Habit.InInbox > 0;
    public string InInboxText => Habit.InInbox == 1 ? "1 in Inbox now" : $"{Habit.InInbox} in Inbox now";

    public bool IsHabit => Habit.IsHabit;
    public bool Ignored => Habit.Ignored;
    public string Status => Habit.Ignored ? "Ignored" : Habit.IsHabit ? "Suggested in Clean up" : Habit.Trashed < SenderHabit.MinTrashed
        ? $"Binned {Habit.Trashed} of {SenderHabit.MinTrashed} needed" : "Kept too often to suggest";
    public string IgnoreLabel => Habit.Ignored ? "Suggest again" : "Ignore sender";

    public void Refresh()
    {
        OnPropertyChanged(nameof(Ignored));
        OnPropertyChanged(nameof(IsHabit));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IgnoreLabel));
    }
}

/// <summary>
/// What the client has learned about which senders you get rid of. Only your own moves to Trash count; the thresholds are
/// shown so the suggestions are never a mystery. Everything here is still a proposal: a rule has to be saved, a bin has to be clicked.
/// </summary>
public sealed partial class HabitsViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly Account _account;

    public HabitsViewModel(IMailStore store, Account account)
    {
        _store = store;
        _account = account;
    }

    public ObservableCollection<HabitRow> Rows { get; } = [];
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _showAll;

    /// <summary>Opens the rule editor seeded for the sender. Supplied by the main window.</summary>
    public Func<string, string, Task>? RequestRule { get; set; }
    /// <summary>Moves the sender's Inbox mail to Trash now; returns how many went.</summary>
    public Func<Account, string, Task<int>>? RequestTrashNow { get; set; }
    /// <summary>Called after anything changed so the Clean up badge can be recounted.</summary>
    public Action? Changed { get; set; }

    public string Explanation =>
        $"Learned from what you move to Trash yourself. A sender is suggested once you have binned at least {SenderHabit.MinTrashed} of their " +
        $"messages and binned at least {SenderHabit.MinRatio:P0} of the ones you decided on (read and kept counts the other way). " +
        "Rules and the agent never teach this list; only your own hand does.";

    partial void OnShowAllChanged(bool value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        Rows.Clear();
        var habits = await _store.GetSenderHabitsAsync(_account.Id, ShowAll ? 1 : SenderHabit.MinTrashed);
        foreach (var h in habits.OrderByDescending(h => h.IsHabit).ThenByDescending(h => h.Trashed)) Rows.Add(new HabitRow(h));
        var suggested = habits.Count(h => h.IsHabit);
        Summary = habits.Count == 0
            ? "Nothing learned yet. Move some mail to Trash and come back."
            : $"{habits.Count} sender{(habits.Count == 1 ? "" : "s")} you have binned mail from; {suggested} suggested in Clean up.";
    }

    [RelayCommand]
    private async Task MakeRuleAsync(HabitRow? row)
    {
        if (row is null || RequestRule is null) return;
        await RequestRule(row.Address, row.Habit.FromName);
    }

    [RelayCommand]
    private async Task TrashNowAsync(HabitRow? row)
    {
        if (row is null || RequestTrashNow is null) return;
        var moved = await RequestTrashNow(_account, row.Address);
        Summary = moved == 0 ? $"Nothing from {row.Display} in the Inbox." : $"Moved {moved} from {row.Display} to Trash.";
        await LoadAsync();
        Changed?.Invoke();
    }

    [RelayCommand]
    private async Task ToggleIgnoreAsync(HabitRow? row)
    {
        if (row is null) return;
        row.Habit.Ignored = !row.Habit.Ignored;
        await _store.SetSenderIgnoredAsync(_account.Id, row.Address, row.Habit.Ignored);
        row.Refresh();
        Changed?.Invoke();
    }
}
