using MailDesk.Core.Models;
using MailFolder = MailDesk.Core.Models.MailFolder;
using MessageSummary = MailDesk.Core.Models.MessageSummary;
using IMailStore = MailDesk.Core.Storage.IMailStore;
using MailDesk.Core.Security;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace MailDesk.Mail;

/// <summary>
/// MailKit-based IMAP sync. Opens a fresh connection per operation, which is simple and robust;
/// a pooled, IDLE-capable connection manager is the natural next step.
/// </summary>
public sealed class ImapMailSync : IMailSync
{
    private readonly IMailStore _store;
    private readonly ICredentialProtector _protector;

    public ImapMailSync(IMailStore store, ICredentialProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    private async Task<ImapClient> ConnectAsync(Account account, string? password, CancellationToken ct)
    {
        var client = new ImapClient();
        client.Timeout = 30_000;
        var options = account.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(account.ImapHost, account.ImapPort, options, ct);
        var pass = password ?? _protector.Unprotect(account.ProtectedPassword);
        await client.AuthenticateAsync(account.Username, pass, ct);
        return client;
    }

    public async Task TestConnectionAsync(Account account, string password, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(account, password, ct);
        await client.DisconnectAsync(true, ct);
    }

    public async Task SyncFoldersAsync(Account account, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(account, null, ct);
        var ns = client.PersonalNamespaces[0];
        var folders = await client.GetFoldersAsync(ns, false, ct);

        // Inbox is always present even if LIST omits it.
        var all = new List<IMailFolder> { client.Inbox };
        all.AddRange(folders.Where(f => f.FullName != client.Inbox.FullName));

        var known = (await _store.GetFoldersAsync(account.Id, ct)).ToDictionary(x => x.FullName);
        var seen = new HashSet<string>();
        foreach (var f in all)
        {
            if (f.Attributes.HasFlag(FolderAttributes.NonExistent)) continue;
            seen.Add(f.FullName);
            var local = new MailFolder
            {
                AccountId = account.Id,
                FullName = f.FullName,
                DisplayName = f.Name,
                Kind = ClassifyFolder(f),
                IsSubscribed = f.IsSubscribed || f.Attributes.HasFlag(FolderAttributes.Inbox),
            };
            // Preserve sync state if we already know this folder.
            if (known.TryGetValue(f.FullName, out var existing))
            {
                local.UidValidity = existing.UidValidity;
                local.HighestUid = existing.HighestUid;
                local.TotalCount = existing.TotalCount;
                local.UnreadCount = existing.UnreadCount;
            }
            await _store.UpsertFolderAsync(local, ct);
        }
        await client.DisconnectAsync(true, ct);

        // Folders that vanished from the server (deleted elsewhere) go from the cache too.
        foreach (var stale in known.Values.Where(k => !seen.Contains(k.FullName)))
            await _store.DeleteFolderAsync(stale.Id, ct);
    }

    private static FolderKind ClassifyFolder(IMailFolder f)
    {
        var a = f.Attributes;
        if (a.HasFlag(FolderAttributes.Inbox)) return FolderKind.Inbox;
        if (a.HasFlag(FolderAttributes.Sent)) return FolderKind.Sent;
        if (a.HasFlag(FolderAttributes.Drafts)) return FolderKind.Drafts;
        if (a.HasFlag(FolderAttributes.Trash)) return FolderKind.Trash;
        if (a.HasFlag(FolderAttributes.Junk)) return FolderKind.Junk;
        if (a.HasFlag(FolderAttributes.Archive)) return FolderKind.Archive;

        // Servers without SPECIAL-USE: fall back to common names.
        var n = f.Name.ToLowerInvariant();
        if (n == "inbox") return FolderKind.Inbox;
        if (n is "sent" or "sent items" or "sent mail") return FolderKind.Sent;
        if (n is "drafts") return FolderKind.Drafts;
        if (n is "trash" or "deleted items" or "deleted" or "bin") return FolderKind.Trash;
        if (n is "junk" or "junk email" or "junk e-mail" or "spam") return FolderKind.Junk;
        if (n is "archive" or "archives") return FolderKind.Archive;
        return FolderKind.Other;
    }

    public async Task<IReadOnlyList<MessageSummary>> SyncFolderAsync(Account account, MailFolder folder, int initialBatch = 500, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(account, null, ct);
        var remote = await client.GetFolderAsync(folder.FullName, ct);
        await remote.OpenAsync(FolderAccess.ReadOnly, ct);

        if (folder.UidValidity != 0 && folder.UidValidity != remote.UidValidity)
        {
            progress?.Report($"UIDVALIDITY changed for {folder.DisplayName}; clearing local cache.");
            await _store.ClearFolderMessagesAsync(folder.Id, ct);
            folder.HighestUid = 0;
        }
        folder.UidValidity = remote.UidValidity;

        const MessageSummaryItems items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
            | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText;

        IList<IMessageSummary> summaries;
        if (folder.HighestUid == 0)
        {
            // First sync: most recent N by index.
            if (remote.Count == 0) summaries = [];
            else
            {
                var start = Math.Max(0, remote.Count - initialBatch);
                progress?.Report($"Fetching {remote.Count - start} headers from {folder.DisplayName}...");
                summaries = await remote.FetchAsync(start, -1, items, ct);
            }
        }
        else
        {
            var range = new UniqueIdRange(new UniqueId((uint)folder.HighestUid + 1), UniqueId.MaxValue);
            progress?.Report($"Checking {folder.DisplayName} for new mail...");
            summaries = await remote.FetchAsync(range, items, ct);
        }

        var rows = summaries.Where(s => s.UniqueId.IsValid && s.UniqueId.Id > folder.HighestUid)
            .Select(s => ToSummary(folder.Id, s)).ToList();
        if (rows.Count > 0) await _store.UpsertMessagesAsync(rows, ct);

        folder.TotalCount = remote.Count;
        folder.UnreadCount = remote.Unread;
        folder.HighestUid = Math.Max(folder.HighestUid, rows.Count > 0 ? rows.Max(r => r.Uid) : 0);
        await _store.UpsertFolderAsync(folder, ct);

        progress?.Report(rows.Count > 0 ? $"{rows.Count} new in {folder.DisplayName}." : $"{folder.DisplayName} is up to date.");
        await client.DisconnectAsync(true, ct);

        // Re-read so callers get rows with their database ids (needed to move or flag them later).
        if (rows.Count == 0) return [];
        var minUid = rows.Min(r => r.Uid);
        return (await _store.GetMessagesAsync(folder.Id, limit: rows.Count + 50, ct: ct)).Where(m => m.Uid >= minUid).ToList();
    }

    private static MessageSummary ToSummary(long folderId, IMessageSummary s)
    {
        var env = s.Envelope;
        var from = env?.From?.Mailboxes.FirstOrDefault();
        var flags = s.Flags ?? MessageFlags.None;
        return new MessageSummary
        {
            FolderId = folderId,
            Uid = s.UniqueId.Id,
            MessageId = env?.MessageId ?? "",
            Subject = env?.Subject ?? "(no subject)",
            FromName = from?.Name ?? "",
            FromAddress = from?.Address ?? "",
            ToAddresses = env?.To is null ? "" : string.Join(", ", env.To.Mailboxes.Select(m => m.Address)),
            DateUtcMs = (env?.Date ?? s.InternalDate ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            IsRead = flags.HasFlag(MessageFlags.Seen),
            IsFlagged = flags.HasFlag(MessageFlags.Flagged),
            HasAttachments = s.Attachments?.Any() ?? false,
            Size = s.Size ?? 0,
            Snippet = s.PreviewText ?? "",
        };
    }

    public async Task<MessageBody> FetchBodyAsync(Account account, MessageSummary message, CancellationToken ct = default)
    {
        var cached = await _store.GetBodyAsync(message.Id, ct);
        if (cached is not null) return cached;

        var folder = await _store.GetFolderAsync(message.FolderId, ct) ?? throw new InvalidOperationException("Folder not found.");
        using var client = await ConnectAsync(account, null, ct);
        var remote = await client.GetFolderAsync(folder.FullName, ct);
        await remote.OpenAsync(FolderAccess.ReadOnly, ct);
        MimeMessage mime = await remote.GetMessageAsync(new UniqueId((uint)message.Uid), ct);
        await client.DisconnectAsync(true, ct);

        var body = new MessageBody { MessageId = message.Id, HtmlBody = mime.HtmlBody, TextBody = mime.TextBody, CalendarIcs = CalendarExtractor.Extract(mime) };
        await _store.SaveBodyAsync(body, ct);
        return body;
    }

    public async Task MoveAsync(Account account, IReadOnlyList<MessageSummary> messages, MailFolder target, CancellationToken ct = default)
    {
        if (messages.Count == 0) return;
        using var client = await ConnectAsync(account, null, ct);
        var dest = await client.GetFolderAsync(target.FullName, ct);

        foreach (var group in messages.GroupBy(m => m.FolderId))
        {
            var src = await _store.GetFolderAsync(group.Key, ct) ?? throw new InvalidOperationException("Source folder not found.");
            var remote = await client.GetFolderAsync(src.FullName, ct);
            await remote.OpenAsync(FolderAccess.ReadWrite, ct);
            var uids = group.Select(m => new UniqueId((uint)m.Uid)).ToList();
            await remote.MoveToAsync(uids, dest, ct);
            await remote.CloseAsync(false, ct);
            // The server assigns new UIDs in the destination; drop local rows and let the next sync pick them up.
            await _store.DeleteMessagesAsync(group.Select(m => m.Id), ct);
        }
        await client.DisconnectAsync(true, ct);
    }

    public async Task SetFlagsAsync(Account account, MessageSummary message, bool? isRead = null, bool? isFlagged = null, CancellationToken ct = default)
    {
        var folder = await _store.GetFolderAsync(message.FolderId, ct) ?? throw new InvalidOperationException("Folder not found.");
        using var client = await ConnectAsync(account, null, ct);
        var remote = await client.GetFolderAsync(folder.FullName, ct);
        await remote.OpenAsync(FolderAccess.ReadWrite, ct);
        var uid = new UniqueId((uint)message.Uid);
        if (isRead is not null)
        {
            if (isRead.Value) await remote.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            else await remote.RemoveFlagsAsync(uid, MessageFlags.Seen, true, ct);
        }
        if (isFlagged is not null)
        {
            if (isFlagged.Value) await remote.AddFlagsAsync(uid, MessageFlags.Flagged, true, ct);
            else await remote.RemoveFlagsAsync(uid, MessageFlags.Flagged, true, ct);
        }
        await client.DisconnectAsync(true, ct);
        await _store.UpdateFlagsAsync(message.Id, isRead, isFlagged, ct);
    }

    public async Task<MailFolder> CreateFolderAsync(Account account, string name, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(account, null, ct);
        var top = client.GetFolder(client.PersonalNamespaces[0]);
        var created = await top.CreateAsync(name, true, ct) ?? throw new InvalidOperationException($"Server did not return the created folder '{name}'.");
        try { await created.SubscribeAsync(ct); } catch { /* some servers reject SUBSCRIBE; harmless */ }
        await client.DisconnectAsync(true, ct);

        var local = new MailFolder { AccountId = account.Id, FullName = created.FullName, DisplayName = created.Name, Kind = FolderKind.Other };
        await _store.UpsertFolderAsync(local, ct);
        return local;
    }

    public async Task DeleteFolderAsync(Account account, MailFolder folder, CancellationToken ct = default)
    {
        if (folder.Kind != FolderKind.Other)
            throw new InvalidOperationException($"Refusing to delete special folder '{folder.DisplayName}'.");
        using var client = await ConnectAsync(account, null, ct);
        var remote = await client.GetFolderAsync(folder.FullName, ct);
        if (remote.IsOpen) await remote.CloseAsync(false, ct);
        await remote.DeleteAsync(ct);
        await client.DisconnectAsync(true, ct);
        await _store.DeleteFolderAsync(folder.Id, ct);
    }
}
