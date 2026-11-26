namespace MailDesk.Core.Models;

/// <summary>Header-level data for a message, enough to render a list row without touching the server.</summary>
public sealed class MessageSummary
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public long Uid { get; set; }
    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string FromName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string ToAddresses { get; set; } = "";
    /// <summary>Message date as Unix milliseconds UTC.</summary>
    public long DateUtcMs { get; set; }
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public bool HasAttachments { get; set; }
    public long Size { get; set; }
    public string Snippet { get; set; } = "";

    public DateTimeOffset Date => DateTimeOffset.FromUnixTimeMilliseconds(DateUtcMs).ToLocalTime();
    public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
}

/// <summary>Full body for a message, fetched on demand and cached.</summary>
public sealed class MessageBody
{
    public long MessageId { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    /// <summary>Raw iCalendar text when the message carries a text/calendar part or an .ics attachment.</summary>
    public string? CalendarIcs { get; set; }
    public long FetchedUtcMs { get; set; }
}
