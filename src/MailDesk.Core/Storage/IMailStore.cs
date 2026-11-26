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
    Task UpsertMessagesAsync(IEnumerable<MessageSummary> messages, CancellationToken ct = default);
    Task UpdateFlagsAsync(long messageId, bool? isRead = null, bool? isFlagged = null, CancellationToken ct = default);
    Task MoveMessagesAsync(IEnumerable<long> messageIds, long targetFolderId, CancellationToken ct = default);
    Task DeleteMessagesAsync(IEnumerable<long> messageIds, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSummary>> SearchAsync(string query, long? accountId = null, int limit = 200, CancellationToken ct = default);

    // Bodies
    Task<MessageBody?> GetBodyAsync(long messageId, CancellationToken ct = default);
    Task SaveBodyAsync(MessageBody body, CancellationToken ct = default);

    // Stats used by the agent
    Task<IReadOnlyList<SenderStat>> GetSenderStatsAsync(long folderId, int top = 100, CancellationToken ct = default);

    // App settings (small key/value pairs; secrets are stored protected by the caller)
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task SetSettingAsync(string key, string? value, CancellationToken ct = default);

    // Rules
    Task<IReadOnlyList<MailRule>> GetRulesAsync(long accountId, CancellationToken ct = default);
    Task<long> UpsertRuleAsync(MailRule rule, CancellationToken ct = default);
    Task DeleteRuleAsync(long ruleId, CancellationToken ct = default);
    Task IncrementRuleHitsAsync(long ruleId, int count, CancellationToken ct = default);
}

public sealed record SenderStat(string FromAddress, string FromName, long Count, long UnreadCount, string ExampleSubject);
