using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.App.ViewModels;

/// <summary>A task row with a checkbox that writes through to the store.</summary>
public sealed partial class TaskRow : ObservableObject
{
    private readonly Func<TaskRow, bool, Task> _onDone;

    public TaskRow(TaskItem task, Func<TaskRow, bool, Task> onDone)
    {
        Task = task;
        _onDone = onDone;
        _isDone = task.IsDone;
    }

    public TaskItem Task { get; }
    public string Title => Task.Title;
    public string Notes => Task.Notes;
    public string DueText => Task.DueText;
    public bool IsOverdue => Task.IsOverdue;
    public bool HasDue => Task.DueUtcMs is not null;
    public bool HasSource => !string.IsNullOrWhiteSpace(Task.SourceSubject);
    public string SourceText => HasSource ? $"From: {Task.SourceSubject}" : "";

    [ObservableProperty] private bool _isDone;

    partial void OnIsDoneChanged(bool value) => _ = _onDone(this, value);
}

/// <summary>The to-do list. Local only; tasks come from the agent, from "Create task from message", or typed in here.</summary>
public sealed partial class TasksViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly long _accountId;

    public TasksViewModel(IMailStore store, long accountId)
    {
        _store = store;
        _accountId = accountId;
    }

    public ObservableCollection<TaskRow> Tasks { get; } = [];

    [ObservableProperty] private string _newTitle = "";
    [ObservableProperty] private DateTime? _newDue;
    [ObservableProperty] private bool _showDone;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Set by the main window so a task's source message can be opened.</summary>
    public Func<long, Task>? RequestOpenMessage { get; set; }

    partial void OnShowDoneChanged(bool value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        Tasks.Clear();
        var items = await _store.GetTasksAsync(_accountId, includeDone: ShowDone);
        foreach (var t in items) Tasks.Add(new TaskRow(t, OnDoneChangedAsync));
        var open = items.Count(t => !t.IsDone);
        var overdue = items.Count(t => t.IsOverdue);
        StatusText = $"{open} open" + (overdue > 0 ? $", {overdue} overdue" : "");
    }

    private async Task OnDoneChangedAsync(TaskRow row, bool done)
    {
        if (row.Task.IsDone == done) return;
        row.Task.IsDone = done;
        await _store.SetTaskDoneAsync(row.Task.Id, done);
        if (done && !ShowDone)
        {
            // Give the tick a moment to show before the row leaves the list.
            await Task.Delay(350);
            Tasks.Remove(row);
        }
        StatusText = done ? $"Done: {row.Title}" : $"Reopened: {row.Title}";
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var title = NewTitle.Trim();
        if (title.Length == 0) return;
        var task = new TaskItem { AccountId = _accountId, Title = title };
        if (NewDue is { } due) task.DueUtcMs = new DateTimeOffset(due.Date, TimeZoneInfo.Local.GetUtcOffset(due.Date)).ToUnixTimeMilliseconds();
        await _store.UpsertTaskAsync(task);
        NewTitle = "";
        NewDue = null;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(TaskRow? row)
    {
        if (row is null) return;
        await _store.DeleteTaskAsync(row.Task.Id);
        Tasks.Remove(row);
        StatusText = $"Deleted: {row.Title}";
    }

    [RelayCommand]
    private async Task OpenSourceAsync(TaskRow? row)
    {
        if (row?.Task.SourceMessageId is { } id && RequestOpenMessage is not null) await RequestOpenMessage(id);
    }
}
