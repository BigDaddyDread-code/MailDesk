namespace MailDesk.Core.Models;

public enum FolderKind
{
    Other = 0,
    Inbox = 1,
    Sent = 2,
    Drafts = 3,
    Trash = 4,
    Junk = 5,
    Archive = 6,
}

/// <summary>An IMAP folder cached locally. FullName is the server-side name including the delimiter path.</summary>
public sealed class MailFolder
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string FullName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public FolderKind Kind { get; set; }
    /// <summary>IMAP UIDVALIDITY. If it changes, cached UIDs for the folder are invalid and must be resynced.</summary>
    public long UidValidity { get; set; }
    public long HighestUid { get; set; }
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
    public bool IsSubscribed { get; set; } = true;
}
