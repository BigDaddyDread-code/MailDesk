using Dapper;
using MailDesk.Core.Models;
using Microsoft.Data.Sqlite;

namespace MailDesk.Core.Storage;

/// <summary>
/// SQLite-backed store with an FTS5 index over message headers. One file, WAL mode, safe for a single desktop process.
/// </summary>
public sealed class SqliteMailStore : IMailStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteMailStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = Open();
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        await conn.ExecuteAsync(Schema);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS accounts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Email TEXT NOT NULL,
            ImapHost TEXT NOT NULL,
            ImapPort INTEGER NOT NULL,
            ImapUseSsl INTEGER NOT NULL,
            SmtpHost TEXT NOT NULL DEFAULT '',
            SmtpPort INTEGER NOT NULL DEFAULT 587,
            SmtpUseStartTls INTEGER NOT NULL DEFAULT 1,
            Username TEXT NOT NULL,
            ProtectedPassword TEXT NOT NULL,
            CreatedUtcMs INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS folders (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId INTEGER NOT NULL REFERENCES accounts(Id) ON DELETE CASCADE,
            FullName TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            Kind INTEGER NOT NULL DEFAULT 0,
            UidValidity INTEGER NOT NULL DEFAULT 0,
            HighestUid INTEGER NOT NULL DEFAULT 0,
            TotalCount INTEGER NOT NULL DEFAULT 0,
            UnreadCount INTEGER NOT NULL DEFAULT 0,
            IsSubscribed INTEGER NOT NULL DEFAULT 1,
            UNIQUE(AccountId, FullName)
        );

        CREATE TABLE IF NOT EXISTS messages (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FolderId INTEGER NOT NULL REFERENCES folders(Id) ON DELETE CASCADE,
            Uid INTEGER NOT NULL,
            MessageId TEXT NOT NULL DEFAULT '',
            Subject TEXT NOT NULL DEFAULT '',
            FromName TEXT NOT NULL DEFAULT '',
            FromAddress TEXT NOT NULL DEFAULT '',
            ToAddresses TEXT NOT NULL DEFAULT '',
            DateUtcMs INTEGER NOT NULL DEFAULT 0,
            IsRead INTEGER NOT NULL DEFAULT 0,
            IsFlagged INTEGER NOT NULL DEFAULT 0,
            HasAttachments INTEGER NOT NULL DEFAULT 0,
            Size INTEGER NOT NULL DEFAULT 0,
            Snippet TEXT NOT NULL DEFAULT '',
            UNIQUE(FolderId, Uid)
        );
        CREATE INDEX IF NOT EXISTS ix_messages_folder_date ON messages(FolderId, DateUtcMs DESC);
        CREATE INDEX IF NOT EXISTS ix_messages_from ON messages(FromAddress);

        CREATE TABLE IF NOT EXISTS bodies (
            MessageId INTEGER PRIMARY KEY REFERENCES messages(Id) ON DELETE CASCADE,
            TextBody TEXT,
            HtmlBody TEXT,
            FetchedUtcMs INTEGER NOT NULL
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            Subject, FromName, FromAddress, Snippet,
            content='messages', content_rowid='Id', tokenize='unicode61'
        );
        CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts(rowid, Subject, FromName, FromAddress, Snippet)
            VALUES (new.Id, new.Subject, new.FromName, new.FromAddress, new.Snippet);
        END;
        CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, Subject, FromName, FromAddress, Snippet)
            VALUES ('delete', old.Id, old.Subject, old.FromName, old.FromAddress, old.Snippet);
        END;
        CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, Subject, FromName, FromAddress, Snippet)
            VALUES ('delete', old.Id, old.Subject, old.FromName, old.FromAddress, old.Snippet);
            INSERT INTO messages_fts(rowid, Subject, FromName, FromAddress, Snippet)
            VALUES (new.Id, new.Subject, new.FromName, new.FromAddress, new.Snippet);
        END;
        """;

    // ---------------- Accounts ----------------

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken ct = default)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<Account>("SELECT * FROM accounts ORDER BY Id");
        return rows.ToList();
    }

    public async Task<long> UpsertAccountAsync(Account a, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            if (a.Id == 0)
            {
                a.CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                a.Id = await conn.ExecuteScalarAsync<long>("""
                    INSERT INTO accounts (Name, Email, ImapHost, ImapPort, ImapUseSsl, SmtpHost, SmtpPort, SmtpUseStartTls, Username, ProtectedPassword, CreatedUtcMs)
                    VALUES (@Name, @Email, @ImapHost, @ImapPort, @ImapUseSsl, @SmtpHost, @SmtpPort, @SmtpUseStartTls, @Username, @ProtectedPassword, @CreatedUtcMs);
                    SELECT last_insert_rowid();
                    """, a);
            }
            else
            {
                await conn.ExecuteAsync("""
                    UPDATE accounts SET Name=@Name, Email=@Email, ImapHost=@ImapHost, ImapPort=@ImapPort, ImapUseSsl=@ImapUseSsl,
                        SmtpHost=@SmtpHost, SmtpPort=@SmtpPort, SmtpUseStartTls=@SmtpUseStartTls, Username=@Username, ProtectedPassword=@ProtectedPassword
                    WHERE Id=@Id
                    """, a);
            }
            return a.Id;
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteAccountAsync(long accountId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("PRAGMA foreign_keys=ON; DELETE FROM accounts WHERE Id=@accountId", new { accountId });
        }
        finally { _writeLock.Release(); }
    }

    // ---------------- Folders ----------------

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(long accountId, CancellationToken ct = default)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<MailFolder>(
            "SELECT * FROM folders WHERE AccountId=@accountId ORDER BY CASE Kind WHEN 1 THEN 0 WHEN 3 THEN 1 WHEN 2 THEN 2 WHEN 6 THEN 3 WHEN 5 THEN 4 WHEN 4 THEN 5 ELSE 6 END, FullName",
            new { accountId });
        return rows.ToList();
    }

    public async Task<MailFolder?> GetFolderAsync(long folderId, CancellationToken ct = default)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<MailFolder>("SELECT * FROM folders WHERE Id=@folderId", new { folderId });
    }

    public async Task<long> UpsertFolderAsync(MailFolder f, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            var id = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO folders (AccountId, FullName, DisplayName, Kind, UidValidity, HighestUid, TotalCount, UnreadCount, IsSubscribed)
                VALUES (@AccountId, @FullName, @DisplayName, @Kind, @UidValidity, @HighestUid, @TotalCount, @UnreadCount, @IsSubscribed)
                ON CONFLICT(AccountId, FullName) DO UPDATE SET
                    DisplayName=excluded.DisplayName, Kind=excluded.Kind, UidValidity=excluded.UidValidity,
                    HighestUid=excluded.HighestUid, TotalCount=excluded.TotalCount, UnreadCount=excluded.UnreadCount, IsSubscribed=excluded.IsSubscribed;
                SELECT Id FROM folders WHERE AccountId=@AccountId AND FullName=@FullName;
                """, f);
            f.Id = id;
            return id;
        }
        finally { _writeLock.Release(); }
    }

    public async Task ClearFolderMessagesAsync(long folderId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("DELETE FROM messages WHERE FolderId=@folderId; UPDATE folders SET HighestUid=0 WHERE Id=@folderId", new { folderId });
        }
        finally { _writeLock.Release(); }
    }

    // ---------------- Messages ----------------

    public async Task<IReadOnlyList<MessageSummary>> GetMessagesAsync(long folderId, int limit = 500, int offset = 0, CancellationToken ct = default)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<MessageSummary>(
            "SELECT * FROM messages WHERE FolderId=@folderId ORDER BY DateUtcMs DESC LIMIT @limit OFFSET @offset",
            new { folderId, limit, offset });
        return rows.ToList();
    }

    public async Task<MessageSummary?> GetMessageAsync(long messageId, CancellationToken ct = default)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<MessageSummary>("SELECT * FROM messages WHERE Id=@messageId", new { messageId });
    }

    public async Task UpsertMessagesAsync(IEnumerable<MessageSummary> messages, CancellationToken ct = default)
    {
        var list = messages.ToList();
        if (list.Count == 0) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("""
                INSERT INTO messages (FolderId, Uid, MessageId, Subject, FromName, FromAddress, ToAddresses, DateUtcMs, IsRead, IsFlagged, HasAttachments, Size, Snippet)
                VALUES (@FolderId, @Uid, @MessageId, @Subject, @FromName, @FromAddress, @ToAddresses, @DateUtcMs, @IsRead, @IsFlagged, @HasAttachments, @Size, @Snippet)
                ON CONFLICT(FolderId, Uid) DO UPDATE SET
                    IsRead=excluded.IsRead, IsFlagged=excluded.IsFlagged, Subject=excluded.Subject, Snippet=excluded.Snippet
                """, list, tx);
            var folderIds = list.Select(m => m.FolderId).Distinct().ToList();
            foreach (var fid in folderIds)
            {
                await conn.ExecuteAsync("""
                    UPDATE folders SET
                        HighestUid = (SELECT COALESCE(MAX(Uid),0) FROM messages WHERE FolderId=@fid),
                        TotalCount = (SELECT COUNT(*) FROM messages WHERE FolderId=@fid),
                        UnreadCount = (SELECT COUNT(*) FROM messages WHERE FolderId=@fid AND IsRead=0)
                    WHERE Id=@fid
                    """, new { fid }, tx);
            }
            tx.Commit();
        }
        finally { _writeLock.Release(); }
    }

    public async Task UpdateFlagsAsync(long messageId, bool? isRead = null, bool? isFlagged = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            if (isRead is not null)
                await conn.ExecuteAsync("UPDATE messages SET IsRead=@v WHERE Id=@messageId", new { v = isRead.Value, messageId });
            if (isFlagged is not null)
                await conn.ExecuteAsync("UPDATE messages SET IsFlagged=@v WHERE Id=@messageId", new { v = isFlagged.Value, messageId });
            await conn.ExecuteAsync("""
                UPDATE folders SET UnreadCount = (SELECT COUNT(*) FROM messages WHERE FolderId=folders.Id AND IsRead=0)
                WHERE Id = (SELECT FolderId FROM messages WHERE Id=@messageId)
                """, new { messageId });
        }
        finally { _writeLock.Release(); }
    }

    public async Task MoveMessagesAsync(IEnumerable<long> messageIds, long targetFolderId, CancellationToken ct = default)
    {
        // Local-only move: the server assigns new UIDs after a real IMAP move, so the sync layer should
        // delete the local rows and let the target folder resync. This method exists for optimistic UI updates.
        var ids = messageIds.ToList();
        if (ids.Count == 0) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("UPDATE messages SET FolderId=@targetFolderId, Uid=-(Id) WHERE Id IN @ids", new { targetFolderId, ids });
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteMessagesAsync(IEnumerable<long> messageIds, CancellationToken ct = default)
    {
        var ids = messageIds.ToList();
        if (ids.Count == 0) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("DELETE FROM messages WHERE Id IN @ids", new { ids });
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<MessageSummary>> SearchAsync(string query, long? accountId = null, int limit = 200, CancellationToken ct = default)
    {
        var fts = ToFtsQuery(query);
        if (string.IsNullOrWhiteSpace(fts)) return [];
        using var conn = Open();
        var sql = """
            SELECT m.* FROM messages_fts f
            JOIN messages m ON m.Id = f.rowid
            JOIN folders fo ON fo.Id = m.FolderId
            WHERE messages_fts MATCH @fts
            """ + (accountId is null ? "" : " AND fo.AccountId=@accountId") + " ORDER BY m.DateUtcMs DESC LIMIT @limit";
        var rows = await conn.QueryAsync<MessageSummary>(sql, new { fts, accountId, limit });
        return rows.ToList();
    }

    /// <summary>Turns free text into a safe FTS5 query: each word becomes a quoted prefix term.</summary>
    public static string ToFtsQuery(string query)
    {
        var terms = query.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", ""))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*");
        return string.Join(" ", terms);
    }

    // ---------------- Bodies ----------------

    public async Task<MessageBody?> GetBodyAsync(long messageId, CancellationToken ct = default)
    {
        using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<MessageBody>("SELECT * FROM bodies WHERE MessageId=@messageId", new { messageId });
    }

    public async Task SaveBodyAsync(MessageBody body, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            body.FetchedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await conn.ExecuteAsync("""
                INSERT INTO bodies (MessageId, TextBody, HtmlBody, FetchedUtcMs) VALUES (@MessageId, @TextBody, @HtmlBody, @FetchedUtcMs)
                ON CONFLICT(MessageId) DO UPDATE SET TextBody=excluded.TextBody, HtmlBody=excluded.HtmlBody, FetchedUtcMs=excluded.FetchedUtcMs
                """, body);
        }
        finally { _writeLock.Release(); }
    }

    // ---------------- Stats ----------------

    public async Task<IReadOnlyList<SenderStat>> GetSenderStatsAsync(long folderId, int top = 100, CancellationToken ct = default)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<SenderStat>("""
            SELECT FromAddress, MAX(FromName) AS FromName, COUNT(*) AS Count,
                   SUM(CASE WHEN IsRead=0 THEN 1 ELSE 0 END) AS UnreadCount,
                   MAX(Subject) AS ExampleSubject
            FROM messages WHERE FolderId=@folderId
            GROUP BY FromAddress ORDER BY Count DESC LIMIT @top
            """, new { folderId, top });
        return rows.ToList();
    }

    public async Task DeleteFolderAsync(long folderId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("PRAGMA foreign_keys=ON; DELETE FROM messages WHERE FolderId=@folderId; DELETE FROM folders WHERE Id=@folderId", new { folderId });
        }
        finally { _writeLock.Release(); }
    }
}
