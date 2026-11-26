using MailDesk.Core.Models;

namespace MailDesk.App.ViewModels;

/// <summary>One bubble in the Ask-your-mailbox conversation.</summary>
public sealed class ChatEntry
{
    public bool FromUser { get; init; }
    public string Text { get; init; } = "";
    public bool IsError { get; init; }
    /// <summary>Messages the assistant looked at, shown as chips the user can click to open.</summary>
    public List<MessageSummary> Sources { get; init; } = [];
    public string Note { get; init; } = "";
    public bool HasSources => Sources.Count > 0;
}
