namespace MailDesk.Core.Models;

/// <summary>
/// One thing a conversation established, kept so it can be listed across conversations: a decision, a commitment (who owes
/// what, by when) or an open question. Written when a conversation is analysed; the done flag is the user's and survives
/// re-analysis of the same thread when the text is unchanged.
/// </summary>
public sealed class ThreadFact
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string ThreadKey { get; set; } = "";
    /// <summary>"decision", "commitment" or "question".</summary>
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Who decided, who owes it, or who is waiting. "you" is the account owner.</summary>
    public string Who { get; set; } = "";
    public bool IsYours { get; set; }
    /// <summary>The due date as the model gave it (a date or a phrase), for display.</summary>
    public string Due { get; set; } = "";
    /// <summary>Start of the due day, Unix milliseconds UTC, when <see cref="Due"/> was a real date.</summary>
    public long? DueUtcMs { get; set; }
    public bool IsDone { get; set; }
    /// <summary>The message the item came from, when the model tied it to one.</summary>
    public long? MessageId { get; set; }
    /// <summary>Subject of the conversation, for the list.</summary>
    public string Subject { get; set; } = "";
    /// <summary>"stated" or "inferred".</summary>
    public string Basis { get; set; } = "stated";
    public long AnalysedUtcMs { get; set; }

    public bool IsInferred => !Basis.Equals("stated", StringComparison.OrdinalIgnoreCase);
    public bool IsOverdue => !IsDone && DueUtcMs is { } d && d < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 86_400_000L + 1;
    public string DueText
    {
        get
        {
            if (DueUtcMs is not { } ms) return Due;
            var d = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().Date;
            var today = DateTime.Today;
            if (d == today) return "Today";
            if (d == today.AddDays(1)) return "Tomorrow";
            if (d == today.AddDays(-1)) return "Yesterday";
            return d.Year == today.Year ? d.ToString("ddd d MMM") : d.ToString("d MMM yyyy");
        }
    }
}
