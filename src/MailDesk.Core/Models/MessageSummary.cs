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
    public string CcAddresses { get; set; } = "";
    /// <summary>Message date as Unix milliseconds UTC.</summary>
    public long DateUtcMs { get; set; }
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public bool HasAttachments { get; set; }
    public long Size { get; set; }
    public string Snippet { get; set; } = "";
    /// <summary>The Message-ID this message answers, if any.</summary>
    public string InReplyTo { get; set; } = "";
    /// <summary>The References header: every Message-ID in the thread, space separated.</summary>
    public string ReferencesHeader { get; set; } = "";
    /// <summary>Local classification from headers; see <see cref="Text.MessageClassifier"/>.</summary>
    public MessageCategory Category { get; set; }
    /// <summary>Subject without Re:/Fwd: prefixes, lower-cased, for matching replies to originals. Filled in by the store when empty.</summary>
    public string SubjectKey { get; set; } = "";
    /// <summary>
    /// Conversation key, assigned by the store: the first Message-ID in References, else In-Reply-To, else the message's own
    /// Message-ID. A Re: with no thread headers joins the earliest message with the same subject key.
    /// </summary>
    public string ThreadKey { get; set; } = "";

    /// <summary>Messages in this conversation across the account (Trash and Junk excluded). Filled by list queries; 0 when unknown.</summary>
    public int ThreadCount { get; set; }
    /// <summary>Unread messages in the conversation. Filled by list queries.</summary>
    public int ThreadUnread { get; set; }
    public string ThreadBadge => ThreadCount > 1 ? ThreadCount.ToString() : "";

    /// <summary>Display name of the containing folder. Filled by search results and views only; not a stored column.</summary>
    public string FolderName { get; set; } = "";
    /// <summary>Short status shown as a badge in the list, e.g. how long a sent message has waited. Not stored.</summary>
    public string Badge { get; set; } = "";
    /// <summary>Account name, filled by the unified inbox when more than one account is set up. Not stored.</summary>
    public string AccountName { get; set; } = "";
    /// <summary>True when the row should lead with who the message went to rather than who sent it (Sent folder, Waiting view). Not stored.</summary>
    public bool ShowRecipients { get; set; }

    public DateTimeOffset Date => DateTimeOffset.FromUnixTimeMilliseconds(DateUtcMs).ToLocalTime();
    public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
    public string ToDisplay
    {
        get
        {
            var all = ToAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (all.Length == 0) return "(no recipients)";
            return all.Length == 1 ? $"To: {all[0]}" : $"To: {all[0]} +{all.Length - 1}";
        }
    }
    /// <summary>What the list row leads with: the sender, or the recipients for sent mail.</summary>
    public string Correspondent => ShowRecipients ? ToDisplay : FromDisplay;
    public string AvatarKey => ShowRecipients ? ToAddresses : FromAddress;
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
