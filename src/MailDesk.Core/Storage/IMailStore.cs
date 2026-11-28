using MailDesk.Core.Models;

namespace MailDesk.Core.Storage;

/// <summary>Local cache of accounts, folders and messages. The UI reads only from here; sync writes into it.</summary>
public interface IMailStore
{
    Task InitializeAsync(CancellationToken ct = default);

    // Accounts
    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default);
    Task<long> UpsertAccountAsync(Account account, CancellationToken ct = default);
    Task DeleteAccountAsync(long accountId, CancellationToken ct = default);

    // Folders
    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(long accountId, CancellationToken ct = default);
    Task<MailFolder?> GetFolderAsync(long folderId, CancellationToken ct = default);
    Task<long> UpsertFolderAsync(MailFolder folder, CancellationToken ct = default);
    Task ClearFolderMessagesAsync(long folderId, CancellationToken ct = default);
    Task DeleteFolderAsync(long folderId, CancellationToken ct = default);

    // Messages
    Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(long folderId, int limit = 500, int offset = 0, CancellationToken ct = default);
    Task<MessageSummary?> GetMessageAsync(long messageId, CancellationToken ct = default);
    /// <summary>One row per conversation in a folder: the newest message of each thread, newest first, with ThreadCount and ThreadUnread filled.</summary>
    Task<IReadOnlyList<MessageSummary>> GetThreadsAsync(long folderId, int limit = 500, CancellationToken ct = default);
    /// <summary>Every message of a conversation across the account's folders (Junk excluded), oldest first, with FolderName filled.</summary>
    Task<IReadOnlyList<MessageSummary>> GetThreadAsync(long accountId, string threadKey, CancellationToken ct = default);
    Task UpsertMessagesAsync(IEnumerable<MessageSummary> messages, CancellationToken ct = default);
    Task UpdateFlagsAsync(long messageId, bool? isRead = null, bool? isFlagged = null, CancellationToken ct = default);
    Task MoveMessagesAsync(IEnumerable<long> messageIds, long targetFolderId, CancellationToken ct = default);
    Task DeleteMessagesAsync(IEnumerable<long> messageIds, CancellationToken ct = default);
    /// <summary>
    /// Full-text search over subject, sender and preview. Results carry <see cref="MessageSummary.FolderName"/>.
    /// A query also matches folder names: "finance" lists mail in a folder called Finance, and "in:finance invoice"
    /// (or "folder:finance") limits the text search to folders whose name contains "finance".
    /// <paramref name="folderId"/> restricts the search to one folder.
    /// </summary>
    Task<IReadOnlyList<MessageSummary>> SearchAsync(string query, long? accountId = null, long? folderId = null, int limit = 200, CancellationToken ct = default);
    /// <summary>
    /// A dynamic list over the cache. Priority: Inbox mail classified Primary. Newsletters / Notifications: that category in
    /// every folder except Sent, Drafts, Trash and Junk. Waiting: Sent mail from the last <paramref name="waitingDays"/>
    /// days with no later message from a recipient that answers it (by In-Reply-To, References or normalised subject).
    /// Rows carry <see cref="MessageSummary.FolderName"/>; Waiting rows also set ShowRecipients and a Badge.
    /// </summary>
    Task<IReadOnlyList<MessageSummary>> GetSmartViewAsync(long accountId, SmartView view, int limit = 500, int waitingDays = 45, CancellationToken ct = default);
    /// <summary>Every account's Inbox messages, newest first, with FolderName and AccountName filled.</summary>
    Task<IReadOnlyList<MessageSummary>> GetUnifiedInboxAsync(int limit = 500, CancellationToken ct = default);

    // Bodies
    Task<MessageBody?> GetBodyAsync(long messageId, CancellationToken ct = default);
    Task SaveBodyAsync(MessageBody body, CancellationToken ct = default);

    // Stats used by the agent
    Task<IReadOnlyList<SenderStat>> GetSenderStatsAsync(long folderId, int top = 100, CancellationToken ct = default);

    // App settings (small key/value pairs; secrets are stored protected by the caller)
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task SetSettingAsync(string key, string? value, CancellationToken ct = default);
    Task<IReadOnlyList<(string Key, string Value)>> GetSettingsByPrefixAsync(string prefix, CancellationToken ct = default);

    // Facts extracted from conversations (decisions, commitments, open questions)
    /// <summary>Replaces a thread's facts. A fact whose kind and text match an existing one keeps that one's done flag.</summary>
    Task ReplaceThreadFactsAsync(long accountId, string threadKey, IEnumerable<ThreadFact> facts, CancellationToken ct = default);
    /// <summary>All facts for the account: open first, dated before undated, soonest first.</summary>
    Task<IReadOnlyList<ThreadFact>> GetThreadFactsAsync(long accountId, bool includeDone, CancellationToken ct = default);
    Task SetThreadFactDoneAsync(long factId, bool done, CancellationToken ct = default);

    // Saved searches
    /// <summary>Every saved search in sidebar order.</summary>
    Task<IReadOnlyList<SavedSearch>> GetSavedSearchesAsync(CancellationToken ct = default);
    /// <summary>Inserts (Id 0, placed last) or updates by Id. Returns the id.</summary>
    Task<long> UpsertSavedSearchAsync(SavedSearch search, CancellationToken ct = default);
    Task DeleteSavedSearchAsync(long id, CancellationToken ct = default);

    // Calendar
    Task<IReadOnlyList<CalendarItem>> GetCalendarItemsAsync(long accountId, long fromUtcMs, long toUtcMs, CancellationToken ct = default);
    Task<CalendarItem?> GetCalendarItemAsync(long accountId, string uid, CancellationToken ct = default);
    /// <summary>Inserts or updates by (AccountId, Uid). An older Sequence never overwrites a newer one; the response is preserved on update.</summary>
    Task<CalendarItem> UpsertCalendarItemAsync(CalendarItem item, CancellationToken ct = default);
    Task SetCalendarResponseAsync(long itemId, RsvpStatus response, CancellationToken ct = default);
    Task DeleteCalendarItemAsync(long itemId, CancellationToken ct = default);

    // Tasks
    Task<IReadOnlyList<TaskItem>> GetTasksAsync(long accountId, bool includeDone, CancellationToken ct = default);
    Task<long> UpsertTaskAsync(TaskItem task, CancellationToken ct = default);
    Task SetTaskDoneAsync(long taskId, bool done, CancellationToken ct = default);
    Task DeleteTaskAsync(long taskId, CancellationToken ct = default);

    // Rules
    Task<IReadOnlyList<MailRule>> GetRulesAsync(long accountId, CancellationToken ct = default);
    Task<long> UpsertRuleAsync(MailRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(long ruleId, CancellationToken ct = default);
    Task IncrementRuleHitsAsync(long ruleId, int count, CancellationToken ct = default);
}

public sealed record SenderStat(string FromAddress, string FromName, long Count, long UnreadCount, string ExampleSubject);
