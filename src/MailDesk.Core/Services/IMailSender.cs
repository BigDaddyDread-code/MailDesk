using MailDesk.Core.Models;

namespace MailDesk.Core.Services;

/// <summary>Outbound mail. Implemented with MailKit SMTP in MailDesk.Mail. Never exposed to the agent.</summary>
public interface IMailSender
{
    /// <summary>Connects and authenticates to the account's SMTP server without sending anything. Throws on failure.</summary>
    Task TestSmtpAsync(Account account, CancellationToken ct = default);

    /// <summary>Sends the message and appends a copy to the Sent folder. Returns the Message-Id.</summary>
    Task<string> SendAsync(Account account, OutgoingMessage message, CancellationToken ct = default);

    /// <summary>Appends the message to the Drafts folder without sending. Returns the Message-Id.</summary>
    Task<string> SaveDraftAsync(Account account, OutgoingMessage message, CancellationToken ct = default);
}
