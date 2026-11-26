using MailDesk.Core.Models;

namespace MailDesk.Core.Services;

/// <summary>
/// Talks to the mail server and writes results into the store. Implemented with MailKit in MailDesk.Mail.
/// The UI and the agent never call the server directly; they go through this.
/// </summary>
public interface IMailSync
{
    /// <summary>Verifies credentials by connecting and authenticating. Throws on failure.</summary>
    Task TestConnectionAsync(Account account, string password, CancellationToken ct = default);

    /// <summary>Refreshes the folder list for the account into the store.</summary>
    Task SyncFoldersAsync(Account account, CancellationToken ct = default);

    /// <summary>Fetches new headers for a folder. On first sync, pulls the most recent <paramref name="initialBatch"/> messages.</summary>
    Task SyncFolderAsync(Account account, MailFolder folder, int initialBatch = 500, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Downloads and caches the body of a message, returning it.</summary>
    Task<MessageBody> FetchBodyAsync(Account account, MessageSummary message, CancellationToken ct = default);

    /// <summary>Moves messages on the server and updates the local cache.</summary>
    Task MoveAsync(Account account, IReadOnlyList<MessageSummary> messages, MailFolder target, CancellationToken ct = default);

    /// <summary>Sets the \Seen or \Flagged flag on the server and locally.</summary>
    Task SetFlagsAsync(Account account, MessageSummary message, bool? isRead = null, bool? isFlagged = null, CancellationToken ct = default);

    /// <summary>Creates a top-level folder on the server and records it locally.</summary>
    Task<MailFolder> CreateFolderAsync(Account account, string name, CancellationToken ct = default);

    /// <summary>Deletes a folder and everything in it on the server, then locally. Refuses special folders (Inbox, Sent, Trash...).</summary>
    Task DeleteFolderAsync(Account account, MailFolder folder, CancellationToken ct = default);
}
