namespace MailDesk.Core.Models;

/// <summary>A message being composed. Address fields hold comma-separated addresses, optionally with display names.</summary>
public sealed class OutgoingMessage
{
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TextBody { get; set; } = "";
    /// <summary>Local file paths to attach.</summary>
    public List<string> Attachments { get; set; } = [];
    /// <summary>Message-Id of the message being replied to, for threading.</summary>
    public string? InReplyTo { get; set; }
    public List<string> References { get; set; } = [];

    public bool IsEmpty => string.IsNullOrWhiteSpace(To) && string.IsNullOrWhiteSpace(Cc) && string.IsNullOrWhiteSpace(Bcc)
        && string.IsNullOrWhiteSpace(Subject) && string.IsNullOrWhiteSpace(TextBody) && Attachments.Count == 0;
}
