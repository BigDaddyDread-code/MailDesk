namespace MailDesk.Core.Models;

/// <summary>
/// What the user has done with one sender's mail over time, learned from their own actions: how many messages they moved
/// to Trash versus read and kept. Nothing is inferred from content. A habit becomes a suggestion only once enough of the
/// mail has gone the same way; the user can ignore a sender so it is never suggested again.
/// </summary>
public sealed class SenderHabit
{
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    /// <summary>Messages from this sender the user moved to Trash (by hand, not by rule).</summary>
    public int Trashed { get; set; }
    /// <summary>Messages from this sender the user read and left where they were (outside Trash and Junk).</summary>
    public int Kept { get; set; }
    /// <summary>Messages from this sender still sitting in an Inbox, decided or not.</summary>
    public int InInbox { get; set; }
    public long LastTrashedUtcMs { get; set; }
    /// <summary>True once the user said "stop suggesting this sender".</summary>
    public bool Ignored { get; set; }

    /// <summary>Share of decided messages that went to Trash, 0..1.</summary>
    public double TrashRatio => Trashed + Kept == 0 ? 0 : (double)Trashed / (Trashed + Kept);

    /// <summary>At least this many binned before a sender is called a habit.</summary>
    public const int MinTrashed = 3;
    /// <summary>At least this share binned (the rest read and kept) before a sender is called a habit.</summary>
    public const double MinRatio = 0.75;

    /// <summary>True when the user nearly always gets rid of this sender's mail and has not asked us to stop suggesting it.</summary>
    public bool IsHabit => !Ignored && Trashed >= MinTrashed && TrashRatio >= MinRatio;

    public string Display => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName;
    public string Summary => Kept == 0 ? $"Binned all {Trashed}" : $"Binned {Trashed}, kept {Kept}";
    public DateTimeOffset? LastTrashed => LastTrashedUtcMs == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(LastTrashedUtcMs).ToLocalTime();
}
