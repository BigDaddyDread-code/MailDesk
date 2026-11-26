namespace MailDesk.Core.Models;

/// <summary>A to-do, usually lifted from an email. Lives only in the local store; nothing on the server knows about it.</summary>
public sealed class TaskItem
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Due date as Unix milliseconds UTC, or null for no deadline.</summary>
    public long? DueUtcMs { get; set; }
    public bool IsDone { get; set; }
    /// <summary>Local message id the task came from, if any.</summary>
    public long? SourceMessageId { get; set; }
    /// <summary>Subject of the source message, kept so the task still reads well after the mail moves or is deleted.</summary>
    public string SourceSubject { get; set; } = "";
    public long CreatedUtcMs { get; set; }
    public long? CompletedUtcMs { get; set; }

    public DateTimeOffset? Due => DueUtcMs is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(DueUtcMs.Value).ToLocalTime();
    public bool IsOverdue => !IsDone && Due is { } d && d.Date < DateTime.Today;
    public string DueText => Due is null ? "" : Due.Value.Date == DateTime.Today ? "Today" : Due.Value.Date == DateTime.Today.AddDays(1) ? "Tomorrow" : Due.Value.ToString("ddd d MMM");
}
