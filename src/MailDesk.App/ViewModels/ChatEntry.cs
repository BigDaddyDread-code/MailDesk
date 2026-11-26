using MailDesk.Agent;
using MailDesk.Core.Models;

namespace MailDesk.App.ViewModels;

/// <summary>An event the assistant offered, with its Add button state.</summary>
public sealed partial class OfferedEvent : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public OfferedEvent(EventSuggestion suggestion, long? messageId) { Suggestion = suggestion; MessageId = messageId; }
    public EventSuggestion Suggestion { get; }
    public long? MessageId { get; }
    public string Title => Suggestion.Title;
    public string WhenText => Suggestion.WhenText;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _added;
}

/// <summary>A task the assistant offered, with its Add button state.</summary>
public sealed partial class OfferedTask : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public OfferedTask(TaskItem task) => Task = task;
    public TaskItem Task { get; }
    public string Title => Task.Title;
    public string DueText => Task.DueText;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _added;
}

/// <summary>One bubble in the Ask-your-mailbox conversation.</summary>
public sealed class ChatEntry
{
    public bool FromUser { get; init; }
    public string Text { get; init; } = "";
    public bool IsError { get; init; }
    /// <summary>Messages the assistant looked at, shown as chips the user can click to open.</summary>
    public List<MessageSummary> Sources { get; init; } = [];
    public List<OfferedEvent> Events { get; init; } = [];
    public List<OfferedTask> Tasks { get; init; } = [];
    public string Note { get; init; } = "";
    public bool HasSources => Sources.Count > 0;
    public bool HasEvents => Events.Count > 0;
    public bool HasTasks => Tasks.Count > 0;
}
