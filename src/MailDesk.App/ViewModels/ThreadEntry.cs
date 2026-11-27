using MailDesk.Core.Models;

namespace MailDesk.App.ViewModels;

/// <summary>One message in the conversation timeline shown above the open message.</summary>
public sealed class ThreadEntry
{
    public ThreadEntry(MessageSummary message, bool isCurrent, bool isMine)
    {
        Message = message;
        IsCurrent = isCurrent;
        IsMine = isMine;
    }

    public MessageSummary Message { get; }
    /// <summary>True for the message currently open in the reader.</summary>
    public bool IsCurrent { get; }
    /// <summary>True when the account owner sent it.</summary>
    public bool IsMine { get; }

    public string Who => IsMine ? "You" : Message.FromDisplay;
    public string Initial => IsMine ? "You" : Message.FromDisplay;
    public string AvatarKey => Message.FromAddress;
    public string FolderName => Message.FolderName;
    public string When
    {
        get
        {
            var d = Message.Date;
            var today = DateTimeOffset.Now.Date;
            if (d.Date == today) return d.ToString("HH:mm");
            if (d.Date == today.AddDays(-1)) return "Yesterday " + d.ToString("HH:mm");
            return d.Year == today.Year ? d.ToString("ddd d MMM, HH:mm") : d.ToString("d MMM yyyy");
        }
    }
    public string Snippet => string.IsNullOrWhiteSpace(Message.Snippet) ? Message.Subject : Message.Snippet;
    public bool IsUnread => !Message.IsRead;
    public bool HasAttachments => Message.HasAttachments;
}
