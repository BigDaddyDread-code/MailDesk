using MailDesk.Core.Models;
using MailDesk.Core.Security;
using MailDesk.Core.Services;
using MailDesk.Core.Text;
using MailDesk.Core.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using IMailStore = MailDesk.Core.Storage.IMailStore;

namespace MailDesk.Mail;

/// <summary>SMTP send plus IMAP APPEND into Sent or Drafts, so the mailbox shows what the client did.</summary>
public sealed class MailKitSender : IMailSender
{
    private readonly IMailStore _store;
    private readonly ICredentialProtector _protector;

    public MailKitSender(IMailStore store, ICredentialProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    private async Task<SmtpClient> ConnectSmtpAsync(Account account, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(account.SmtpHost))
            throw new InvalidOperationException("No SMTP host is set for this account. Edit the account to add one.");
        var client = new SmtpClient { Timeout = 30_000 };
        var options = account.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect
            : account.SmtpUseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(account.SmtpHost, account.SmtpPort, options, ct);
        await client.AuthenticateAsync(account.Username, _protector.Unprotect(account.ProtectedPassword), ct);
        return client;
    }

    public async Task TestSmtpAsync(Account account, CancellationToken ct = default)
    {
        using var client = await ConnectSmtpAsync(account, ct);
        await client.DisconnectAsync(true, ct);
    }

    public async Task<string> SendAsync(Account account, OutgoingMessage message, CancellationToken ct = default)
    {
        var mime = Build(account, message);
        if (mime.To.Count + mime.Cc.Count + mime.Bcc.Count == 0)
            throw new InvalidOperationException("Add at least one recipient.");

        using (var smtp = await ConnectSmtpAsync(account, ct))
        {
            await smtp.SendAsync(mime, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        // Best effort: a failed Sent copy must not look like a failed send.
        try { await AppendAsync(account, FolderKind.Sent, mime, MessageFlags.Seen, ct); } catch { }
        return mime.MessageId!;
    }

    public async Task<string> SaveDraftAsync(Account account, OutgoingMessage message, CancellationToken ct = default)
    {
        var mime = Build(account, message);
        await AppendAsync(account, FolderKind.Drafts, mime, MessageFlags.Draft | MessageFlags.Seen, ct);
        return mime.MessageId!;
    }

    private static MimeMessage Build(Account account, OutgoingMessage m)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(account.Name, account.Email));
        AddAddresses(mime.To, m.To);
        AddAddresses(mime.Cc, m.Cc);
        AddAddresses(mime.Bcc, m.Bcc);
        mime.Subject = m.Subject ?? "";
        mime.MessageId = MimeUtils.GenerateMessageId(Domain(account.Email));
        if (!string.IsNullOrWhiteSpace(m.InReplyTo))
        {
            mime.InReplyTo = m.InReplyTo;
            foreach (var r in m.References) mime.References.Add(r);
        }

        var builder = new BodyBuilder { TextBody = m.TextBody ?? "", HtmlBody = HtmlText.ToSimpleHtml(m.TextBody ?? "") };
        foreach (var path in m.Attachments)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"Attachment not found: {path}");
            builder.Attachments.Add(path);
        }
        mime.Body = builder.ToMessageBody();
        return mime;
    }

    private static void AddAddresses(InternetAddressList list, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!InternetAddressList.TryParse(text, out var parsed))
            throw new FormatException($"Could not understand the address list: {text}");
        list.AddRange(parsed);
    }

    private static string Domain(string email)
    {
        var at = email.LastIndexOf('@');
        return at < 0 ? "localhost" : email[(at + 1)..];
    }

    private async Task AppendAsync(Account account, FolderKind kind, MimeMessage mime, MessageFlags flags, CancellationToken ct)
    {
        var local = (await _store.GetFoldersAsync(account.Id, ct)).FirstOrDefault(f => f.Kind == kind)
            ?? throw new InvalidOperationException($"No {kind} folder is known for this account. Sync folders first.");

        using var imap = new ImapClient { Timeout = 30_000 };
        var options = account.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await imap.ConnectAsync(account.ImapHost, account.ImapPort, options, ct);
        await imap.AuthenticateAsync(account.Username, _protector.Unprotect(account.ProtectedPassword), ct);
        var folder = await imap.GetFolderAsync(local.FullName, ct);
        await folder.AppendAsync(mime, flags, ct);
        await imap.DisconnectAsync(true, ct);
    }
}
