using Dapper;
using MailDesk.Core.Models;
using MailDesk.Core.Text;
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
        await conn.ExecuteAsync(RulesSchema);
        await AddColumnIfMissingAsync(conn, "bodies", "CalendarIcs", "TEXT");
        await AddColumnIfMissingAsync(conn, "messages", "CcAddresses", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(conn, "messages", "InReplyTo", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(conn, "messages", "ReferencesHeader", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(conn, "messages", "Category", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(conn, "messages", "SubjectKey", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissingAsync(conn, "messages", "ThreadKey", "TEXT NOT NULL DEFAULT ''");
        await conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS ix_messages_subject_key ON messages(SubjectKey); CREATE INDEX IF NOT EXISTS ix_messages_in_reply_to ON messages(InReplyTo); CREATE INDEX IF NOT EXISTS ix_messages_thread ON messages(ThreadKey);");
        await BackfillClassificationAsync(conn);
        using (var tx = conn.BeginTransaction())
        {
            await AssignThreadKeysAsync(conn, tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// Gives every message without a thread key one: the first Message-ID in References, else In-Reply-To, else its own
    /// Message-ID (or a private key when it has none). Then two fix-ups: a key that points at a cached message which itself
    /// belongs to another thread is chased to that thread (clients that truncate References), and a Re:/Fwd: with no thread
    /// headers joins the earliest message in the account with the same subject key.
    /// </summary>
    private static async Task AssignThreadKeysAsync(SqliteConnection conn, SqliteTransaction tx)
    {
        await conn.ExecuteAsync("""
            UPDATE messages SET ThreadKey =
                CASE
                    WHEN ReferencesHeader <> '' THEN
                        CASE WHEN instr(ReferencesHeader, ' ') > 0 THEN substr(ReferencesHeader, 1, instr(ReferencesHeader, ' ') - 1) ELSE ReferencesHeader END
                    WHEN InReplyTo <> '' THEN InReplyTo
                    WHEN MessageId <> '' THEN MessageId
                    ELSE 'msg:' || Id
                END
            WHERE ThreadKey = ''
            """, transaction: tx);

        for (var pass = 0; pass < 2; pass++)
        {
            var changed = await conn.ExecuteAsync("""
                UPDATE messages SET ThreadKey = (SELECT p.ThreadKey FROM messages p WHERE p.MessageId = messages.ThreadKey AND p.ThreadKey <> '' LIMIT 1)
                WHERE ThreadKey <> MessageId
                  AND EXISTS (SELECT 1 FROM messages p WHERE p.MessageId = messages.ThreadKey AND p.ThreadKey <> '' AND p.ThreadKey <> messages.ThreadKey)
                """, transaction: tx);
            if (changed == 0) break;
        }

        var orphans = (await conn.QueryAsync<(long Id, long AccountId, string Subject, string SubjectKey)>("""
            SELECT m.Id, f.AccountId, m.Subject, m.SubjectKey FROM messages m JOIN folders f ON f.Id = m.FolderId
            WHERE m.ReferencesHeader = '' AND m.InReplyTo = '' AND m.SubjectKey <> '' AND m.ThreadKey = m.MessageId
            """, transaction: tx)).Where(o => MessageClassifier.IsReplyOrForward(o.Subject)).ToList();
        foreach (var o in orphans)
        {
            var key = await conn.QuerySingleOrDefaultAsync<string>("""
                SELECT m.ThreadKey FROM messages m JOIN folders f ON f.Id = m.FolderId
                WHERE f.AccountId=@acc AND m.SubjectKey=@key AND m.Id<>@id AND m.ThreadKey<>''
                ORDER BY m.DateUtcMs LIMIT 1
                """, new { acc = o.AccountId, key = o.SubjectKey, id = o.Id }, tx);
            if (!string.IsNullOrEmpty(key))
                await conn.ExecuteAsync("UPDATE messages SET ThreadKey=@key WHERE Id=@id", new { key, id = o.Id }, tx);
        }
    }

    /// <summary>Per-thread totals over the account (Trash and Junk excluded), joined onto list rows.</summary>
    private const string ThreadStats = """
        LEFT JOIN (SELECT x.ThreadKey, COUNT(*) AS Cnt, SUM(CASE WHEN x.IsRead=0 THEN 1 ELSE 0 END) AS Unread
                   FROM messages x JOIN folders xf ON xf.Id = x.FolderId
                   WHERE xf.AccountId=@accountId AND xf.Kind NOT IN (4, 5) GROUP BY x.ThreadKey) ts ON ts.ThreadKey = m.ThreadKey
        """;

    /// <summary>Rows cached before classification existed get a subject key and a sender-based category, once.</summary>
    private static async Task BackfillClassificationAsync(SqliteConnection conn)
    {
        var rows = (await conn.QueryAsync<(long Id, string Subject, string FromAddress)>(
            "SELECT Id, Subject, FromAddress FROM messages WHERE SubjectKey = '' AND Subject <> ''")).ToList();
        if (rows.Count == 0) return;
        using var tx = conn.BeginTransaction();
        foreach (var (id, subject, from) in rows)
        {
            await conn.ExecuteAsync("UPDATE messages SET SubjectKey=@key, Category=@cat WHERE Id=@id",
                new { id, key = MessageClassifier.SubjectKey(subject), cat = (int)MessageClassifier.Classify(from, subject, MessageClassifier.NoHeaders) }, tx);
        }
        tx.Commit();
    }

    /// <summary>Tiny forward-only migration: CREATE TABLE IF NOT EXISTS never adds columns to existing databases.</summary>
    private static async Task AddColumnIfMissingAsync(SqliteConnection conn, string table, string column, string type)
    {
        var cols = (await conn.QueryAsync<string>($"SELECT name FROM pragma_table_info('{table}')")).ToList();
        if (!cols.Contains(column, StringComparer.OrdinalIgnoreCase))
            await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private const string RulesSchema = """
        CREATE TABLE IF NOT EXISTS rules (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId INTEGER NOT NULL REFERENCES accounts(Id) ON DELETE CASCADE,
            Name TEXT NOT NULL,
            Enabled INTEGER NOT NULL DEFAULT 1,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            ConditionsJson TEXT NOT NULL DEFAULT '[]',
            Action TEXT NOT NULL,
            TargetFolder TEXT,
            CreatedUtcMs INTEGER NOT NULL,
            HitCount INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_rules_account ON rules(AccountId, SortOrder);
        CREATE TABLE IF NOT EXISTS settings (
            Key TEXT PRIMARY KEY,
            Value TEXT
        );
        CREATE TABLE IF NOT EXISTS calendar_items (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId INTEGER NOT NULL REFERENCES accounts(Id) ON DELETE CASCADE,
            Uid TEXT NOT NULL,
            Sequence INTEGER NOT NULL DEFAULT 0,
            Summary TEXT NOT NULL DEFAULT '',
            Description TEXT NOT NULL DEFAULT '',
            Location TEXT NOT NULL DEFAULT '',
            StartUtcMs INTEGER NOT NULL,
            EndUtcMs INTEGER NOT NULL,
            AllDay INTEGER NOT NULL DEFAULT 0,
            OrganizerEmail TEXT NOT NULL DEFAULT '',
            OrganizerName TEXT NOT NULL DEFAULT '',
            AttendeesJson TEXT NOT NULL DEFAULT '[]',
            JoinUrl TEXT,
            RecurrenceRule TEXT,
            Status TEXT NOT NULL DEFAULT 'CONFIRMED',
            MyResponse TEXT NOT NULL DEFAULT 'NeedsAction',
            SourceMessageId INTEGER,
            RawIcs TEXT NOT NULL DEFAULT '',
            UpdatedUtcMs INTEGER NOT NULL,
            UNIQUE(AccountId, Uid)
        );
        CREATE INDEX IF NOT EXISTS ix_calendar_start ON calendar_items(AccountId, StartUtcMs);
        CREATE TABLE IF NOT EXISTS tasks (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId INTEGER NOT NULL REFERENCES accounts(Id) ON DELETE CASCADE,
            Title TEXT NOT NULL,
            Notes TEXT NOT NULL DEFAULT '',
            DueUtcMs INTEGER,
            IsDone INTEGER NOT NULL DEFAULT 0,
            SourceMessageId INTEGER,
            SourceSubject TEXT NOT NULL DEFAULT '',
            CreatedUtcMs INTEGER NOT NULL,
            CompletedUtcMs INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_tasks_account ON tasks(AccountId, IsDone, DueUtcMs);
        """;

    // ---------------- Tasks ----------------

    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(long accountId, bool includeDone, CancellationToken ct = default)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<TaskItem>(
            "SELECT * FROM tasks WHERE AccountId=@accountId" + (includeDone ? "" : " AND IsDone=0") +
            " ORDER BY IsDone, CASE WHEN DueUtcMs IS NULL THEN 1 ELSE 0 END, DueUtcMs, CreatedUtcMs DESC",
            new { accountId });
        return rows.ToList();
    }

    public async Task<long> UpsertTaskAsync(TaskItem task, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            if (task.CreatedUtcMs == 0) task.CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (task.Id == 0)
            {
                task.Id = await conn.ExecuteScalarAsync<long>("""
                    INSERT INTO tasks (AccountId, Title, Notes, DueUtcMs, IsDone, SourceMessageId, SourceSubject, CreatedUtcMs, CompletedUtcMs)
                    VALUES (@AccountId, @Title, @Notes, @DueUtcMs, @IsDone, @SourceMessageId, @SourceSubject, @CreatedUtcMs, @CompletedUtcMs);
                    SELECT last_insert_rowid();
                    """, task);
            }
            else
            {
                await conn.ExecuteAsync("""
                    UPDATE tasks SET Title=@Title, Notes=@Notes, DueUtcMs=@DueUtcMs, IsDone=@IsDone, SourceMessageId=@SourceMessageId,
                        SourceSubject=@SourceSubject, CompletedUtcMs=@CompletedUtcMs WHERE Id=@Id
                    """, task);
            }
            return task.Id;
        }
        finally { _writeLock.Release(); }
    }

    public async Task SetTaskDoneAsync(long taskId, bool done, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("UPDATE tasks SET IsDone=@done, CompletedUtcMs=@completed WHERE Id=@taskId",
                new { done, completed = done ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : (long?)null, taskId });
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteTaskAsync(long taskId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("DELETE FROM tasks WHERE Id=@taskId", new { taskId });
        }
        finally { _writeLock.Release(); }
    }

    // ---------------- Calendar ----------------

    private sealed class CalendarRow
    {
        public long Id { get; set; }
        public long AccountId { get; set; }
        public string Uid { get; set; } = "";
        public int Sequence { get; set; }
        public string Summary { get; set; } = "";
        public string Description { get; set; } = "";
        public string Location { get; set; } = "";
        public long StartUtcMs { get; set; }
        public long EndUtcMs { get; set; }
        public bool AllDay { get; set; }
        public string OrganizerEmail { get; set; } = "";
        public string OrganizerName { get; set; } = "";
        public string AttendeesJson { get; set; } = "[]";
        public string? JoinUrl { get; set; }
        public string? RecurrenceRule { get; set; }
        public string Status { get; set; } = "CONFIRMED";
        public string MyResponse { get; set; } = "NeedsAction";
        public long? SourceMessageId { get; set; }
        public string RawIcs { get; set; } = "";
        public long UpdatedUtcMs { get; set; }

        public CalendarItem ToItem() => new()
        {
            Id = Id, AccountId = AccountId, Uid = Uid, Sequence = Sequence, Summary = Summary, Description = Description, Location = Location,
            StartUtcMs = StartUtcMs, EndUtcMs = EndUtcMs, AllDay = AllDay, OrganizerEmail = OrganizerEmail, OrganizerName = OrganizerName,
            Attendees = System.Text.Json.JsonSerializer.Deserialize<List<CalendarAttendee>>(AttendeesJson) ?? [],
            JoinUrl = JoinUrl, RecurrenceRule = RecurrenceRule, Status = Status,
            MyResponse = Enum.TryParse<RsvpStatus>(MyResponse, out var r) ? r : RsvpStatus.NeedsAction,
            SourceMessageId = SourceMessageId, RawIcs = RawIcs, UpdatedUtcMs = UpdatedUtcMs,
        };
    }

    public async Task<IReadOnlyList<CalendarItem>> GetCalendarItemsAsync(long accountId, long fromUtcMs, long toUtcMs, CancellationToken ct = default)
    {
        using var conn = Open();
        // Recurring items are returned regardless of their first start so the caller can expand them into the window.
        var rows = await conn.QueryAsync<CalendarRow>("""
            SELECT * FROM calendar_items WHERE AccountId=@accountId
              AND ((EndUtcMs >= @fromUtcMs AND StartUtcMs <= @toUtcMs) OR (RecurrenceRule IS NOT NULL AND StartUtcMs <= @toUtcMs))
            ORDER BY StartUtcMs
            """, new { accountId, fromUtcMs, toUtcMs });
        return rows.Select(r => r.ToItem()).ToList();
    }

    public async Task<CalendarItem?> GetCalendarItemAsync(long accountId, string uid, CancellationToken ct = default)
    {
        using var conn = Open();
        var row = await conn.QuerySingleOrDefaultAsync<CalendarRow>("SELECT * FROM calendar_items WHERE AccountId=@accountId AND Uid=@uid", new { accountId, uid });
        return row?.ToItem();
    }

    public async Task<CalendarItem> UpsertCalendarItemAsync(CalendarItem item, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            var existing = await conn.QuerySingleOrDefaultAsync<CalendarRow>("SELECT * FROM calendar_items WHERE AccountId=@AccountId AND Uid=@Uid", item);
            if (existing is not null && existing.Sequence > item.Sequence)
                return existing.ToItem();

            item.UpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var p = new
            {
                item.AccountId, item.Uid, item.Sequence, item.Summary, item.Description, item.Location, item.StartUtcMs, item.EndUtcMs, item.AllDay,
                item.OrganizerEmail, item.OrganizerName, AttendeesJson = System.Text.Json.JsonSerializer.Serialize(item.Attendees),
                item.JoinUrl, item.RecurrenceRule, item.Status,
                MyResponse = (existing is not null && item.MyResponse == RsvpStatus.NeedsAction ? existing.MyResponse : item.MyResponse.ToString()),
                SourceMessageId = item.SourceMessageId ?? existing?.SourceMessageId, item.RawIcs, item.UpdatedUtcMs,
            };
            item.Id = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO calendar_items (AccountId, Uid, Sequence, Summary, Description, Location, StartUtcMs, EndUtcMs, AllDay, OrganizerEmail, OrganizerName,
                    AttendeesJson, JoinUrl, RecurrenceRule, Status, MyResponse, SourceMessageId, RawIcs, UpdatedUtcMs)
                VALUES (@AccountId, @Uid, @Sequence, @Summary, @Description, @Location, @StartUtcMs, @EndUtcMs, @AllDay, @OrganizerEmail, @OrganizerName,
                    @AttendeesJson, @JoinUrl, @RecurrenceRule, @Status, @MyResponse, @SourceMessageId, @RawIcs, @UpdatedUtcMs)
                ON CONFLICT(AccountId, Uid) DO UPDATE SET
                    Sequence=excluded.Sequence, Summary=excluded.Summary, Description=excluded.Description, Location=excluded.Location,
                    StartUtcMs=excluded.StartUtcMs, EndUtcMs=excluded.EndUtcMs, AllDay=excluded.AllDay, OrganizerEmail=excluded.OrganizerEmail,
                    OrganizerName=excluded.OrganizerName, AttendeesJson=excluded.AttendeesJson, JoinUrl=excluded.JoinUrl, RecurrenceRule=excluded.RecurrenceRule,
                    Status=excluded.Status, MyResponse=excluded.MyResponse, SourceMessageId=excluded.SourceMessageId, RawIcs=excluded.RawIcs, UpdatedUtcMs=excluded.UpdatedUtcMs;
                SELECT Id FROM calendar_items WHERE AccountId=@AccountId AND Uid=@Uid;
                """, p);
            item.MyResponse = Enum.Parse<RsvpStatus>(p.MyResponse);
            return item;
        }
        finally { _writeLock.Release(); }
    }

    public async Task SetCalendarResponseAsync(long itemId, RsvpStatus response, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("UPDATE calendar_items SET MyResponse=@r, UpdatedUtcMs=@now WHERE Id=@itemId",
                new { r = response.ToString(), now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), itemId });
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteCalendarItemAsync(long itemId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("DELETE FROM calendar_items WHERE Id=@itemId", new { itemId });
        }
        finally { _writeLock.Release(); }
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<string?>("SELECT Value FROM settings WHERE Key=@key", new { key });
    }

    public async Task SetSettingAsync(string key, string? value, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            if (value is null) await conn.ExecuteAsync("DELETE FROM settings WHERE Key=@key", new { key });
            else await conn.ExecuteAsync("INSERT INTO settings (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", new { key, value });
        }
        finally { _writeLock.Release(); }
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
            CalendarIcs TEXT,
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
        var accountId = await conn.ExecuteScalarAsync<long?>("SELECT AccountId FROM folders WHERE Id=@folderId", new { folderId }) ?? 0;
        var rows = await conn.QueryAsync<MessageSummary>(
            $"SELECT m.*, ts.Cnt AS ThreadCount, ts.Unread AS ThreadUnread FROM messages m {ThreadStats} WHERE m.FolderId=@folderId ORDER BY m.DateUtcMs DESC LIMIT @limit OFFSET @offset",
            new { folderId, accountId, limit, offset });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MessageSummary>> GetThreadsAsync(long folderId, int limit = 500, CancellationToken ct = default)
    {
        using var conn = Open();
        var accountId = await conn.ExecuteScalarAsync<long?>("SELECT AccountId FROM folders WHERE Id=@folderId", new { folderId }) ?? 0;
        var rows = await conn.QueryAsync<MessageSummary>($"""
            WITH ranked AS (
                SELECT r.*, ROW_NUMBER() OVER (PARTITION BY r.ThreadKey ORDER BY r.DateUtcMs DESC, r.Id DESC) AS rn
                FROM messages r WHERE r.FolderId=@folderId)
            SELECT m.*, ts.Cnt AS ThreadCount, ts.Unread AS ThreadUnread
            FROM ranked m {ThreadStats}
            WHERE m.rn = 1 ORDER BY m.DateUtcMs DESC LIMIT @limit
            """, new { folderId, accountId, limit });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MessageSummary>> GetThreadAsync(long accountId, string threadKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(threadKey)) return [];
        using var conn = Open();
        var rows = (await conn.QueryAsync<MessageSummary>("""
            SELECT m.*, fo.DisplayName AS FolderName FROM messages m JOIN folders fo ON fo.Id = m.FolderId
            WHERE fo.AccountId=@accountId AND fo.Kind <> 5 AND m.ThreadKey=@threadKey
            ORDER BY m.DateUtcMs, m.Id
            """, new { accountId, threadKey })).ToList();
        var counted = rows.Count(r => r.FolderName != "Trash");
        foreach (var r in rows) { r.ThreadCount = counted; r.ThreadUnread = rows.Count(x => !x.IsRead); }
        return rows;
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
        foreach (var m in list)
            if (string.IsNullOrEmpty(m.SubjectKey)) m.SubjectKey = MessageClassifier.SubjectKey(m.Subject);
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("""
                INSERT INTO messages (FolderId, Uid, MessageId, Subject, FromName, FromAddress, ToAddresses, CcAddresses, DateUtcMs, IsRead, IsFlagged, HasAttachments, Size, Snippet,
                                      InReplyTo, ReferencesHeader, Category, SubjectKey)
                VALUES (@FolderId, @Uid, @MessageId, @Subject, @FromName, @FromAddress, @ToAddresses, @CcAddresses, @DateUtcMs, @IsRead, @IsFlagged, @HasAttachments, @Size, @Snippet,
                        @InReplyTo, @ReferencesHeader, @Category, @SubjectKey)
                ON CONFLICT(FolderId, Uid) DO UPDATE SET
                    IsRead=excluded.IsRead, IsFlagged=excluded.IsFlagged, Subject=excluded.Subject, Snippet=excluded.Snippet, SubjectKey=excluded.SubjectKey
                """, list, tx);
            await AssignThreadKeysAsync(conn, tx);
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

    public async Task<IReadOnlyList<MessageSummary>> SearchAsync(string query, long? accountId = null, long? folderId = null, int limit = 200, CancellationToken ct = default)
    {
        var q = SearchQuery.Parse(query);
        if (q.IsEmpty) return [];
        var (positiveText, negatives) = SplitNegations(q.Text);
        var fts = ToFtsQuery(positiveText);

        var where = new List<string>();
        var args = new DynamicParameters();
        args.Add("limit", limit);
        if (accountId is not null) { where.Add("fo.AccountId=@accountId"); args.Add("accountId", accountId); }
        if (folderId is not null) { where.Add("m.FolderId=@folderId"); args.Add("folderId", folderId); }

        void Like(string column, IReadOnlyList<string> values, string prefix)
        {
            for (var i = 0; i < values.Count; i++)
            {
                where.Add($"({column}) LIKE @{prefix}{i} ESCAPE '\\'");
                args.Add($"{prefix}{i}", "%" + EscapeLike(values[i]) + "%");
            }
        }
        Like("fo.DisplayName", q.Folders, "folder");
        Like("m.FromAddress || ' ' || m.FromName", q.From, "from");
        Like("m.ToAddresses || ' ' || m.CcAddresses", q.To, "to");
        Like("m.Subject", q.Subject, "subject");
        if (q.HasAttachment is { } att) where.Add(att ? "m.HasAttachments=1" : "m.HasAttachments=0");
        if (q.IsRead is { } read) where.Add(read ? "m.IsRead=1" : "m.IsRead=0");
        if (q.IsFlagged is { } flagged) where.Add(flagged ? "m.IsFlagged=1" : "m.IsFlagged=0");
        if (q.BeforeUtcMs is { } before) { where.Add("m.DateUtcMs < @before"); args.Add("before", before); }
        if (q.AfterUtcMs is { } after) { where.Add("m.DateUtcMs >= @after"); args.Add("after", after); }
        if (q.MinSize is { } min) { where.Add("m.Size >= @minSize"); args.Add("minSize", min); }
        if (q.MaxSize is { } max) { where.Add("m.Size <= @maxSize"); args.Add("maxSize", max); }

        if (negatives.Count > 0)
        {
            args.Add("ftsNot", string.Join(" OR ", negatives.Select(n => $"\"{n}\"*")));
            where.Add("m.Id NOT IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH @ftsNot)");
        }
        if (!string.IsNullOrWhiteSpace(fts))
        {
            args.Add("fts", fts);
            var match = "m.Id IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH @fts)";
            if (q.Folders.Count == 0 && !HasBooleanOperators(q.Text))
            {
                // No explicit folder filter: a plain word also finds everything in a folder of that name.
                args.Add("folderLike", "%" + EscapeLike(q.Text.Trim()) + "%");
                match = $"({match} OR fo.DisplayName LIKE @folderLike ESCAPE '\\')";
            }
            where.Add(match);
        }

        var sql = "SELECT m.*, fo.DisplayName AS FolderName FROM messages m JOIN folders fo ON fo.Id = m.FolderId WHERE "
                  + string.Join(" AND ", where) + " ORDER BY m.DateUtcMs DESC LIMIT @limit";
        using var conn = Open();
        var rows = await conn.QueryAsync<MessageSummary>(sql, args);
        return rows.ToList();
    }

    /// <summary>Pulls "in:name" and "folder:name" tokens out of a query. Returns the remaining text (other operators included) and the folder terms.</summary>
    public static (string text, List<string> folders) SplitFolderTerms(string query)
    {
        var q = SearchQuery.Parse(query);
        return (q.Text, q.Folders);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Takes "-word" tokens out of free text. FTS5 has no unary NOT, so they become a separate exclusion.</summary>
    public static (string positive, List<string> negatives) SplitNegations(string text)
    {
        var positive = new List<string>();
        var negatives = new List<string>();
        foreach (var t in text.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length > 1 && t[0] == '-') negatives.Add(t[1..].Replace("\"", ""));
            else positive.Add(t);
        }
        return (string.Join(' ', positive), negatives);
    }

    private static bool IsOperator(string token) => token is "AND" or "OR" or "NOT";
    private static bool HasBooleanOperators(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t => IsOperator(t) || (t.Length > 1 && t[0] == '-'));

    /// <summary>
    /// Turns free text into a safe FTS5 query: each word becomes a quoted prefix term. Upper-case AND, OR and NOT between
    /// words are kept as operators, and "-word" means NOT word; operators with nothing to join are dropped.
    /// </summary>
    public static string ToFtsQuery(string query)
    {
        var tokens = new List<string>();
        foreach (var raw in query.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsOperator(raw)) { tokens.Add(raw); continue; }
            var word = raw.Replace("\"", "");
            if (word.Length == 0) continue;
            tokens.Add($"\"{word}\"*");
        }

        // Drop operators that would leave FTS5 with a syntax error: leading, trailing, or doubled up.
        var cleaned = new List<string>();
        foreach (var t in tokens)
        {
            if (IsOperator(t))
            {
                if (cleaned.Count == 0) continue;
                if (!IsOperator(cleaned[^1])) cleaned.Add(t); // a second operator in a row is ignored
            }
            else cleaned.Add(t);
        }
        while (cleaned.Count > 0 && IsOperator(cleaned[^1])) cleaned.RemoveAt(cleaned.Count - 1);
        return string.Join(" ", cleaned);
    }

    // ---------------- Smart views ----------------

    private const string NotViewableKinds = "(2, 3, 4, 5)"; // Sent, Drafts, Trash, Junk

    public async Task<IReadOnlyList<MessageSummary>> GetSmartViewAsync(long accountId, SmartView view, int limit = 500, int waitingDays = 45, CancellationToken ct = default)
    {
        using var conn = Open();
        const string select = "SELECT m.*, fo.DisplayName AS FolderName FROM messages m JOIN folders fo ON fo.Id = m.FolderId WHERE fo.AccountId=@accountId ";
        switch (view)
        {
            case SmartView.Priority:
                return (await conn.QueryAsync<MessageSummary>(select + "AND fo.Kind=@inbox AND m.Category=@cat ORDER BY m.DateUtcMs DESC LIMIT @limit",
                    new { accountId, inbox = (int)FolderKind.Inbox, cat = (int)MessageCategory.Primary, limit })).ToList();
            case SmartView.Newsletters:
            case SmartView.Notifications:
                var category = view == SmartView.Newsletters ? MessageCategory.Newsletter : MessageCategory.Notification;
                return (await conn.QueryAsync<MessageSummary>(select + $"AND fo.Kind NOT IN {NotViewableKinds} AND m.Category=@cat ORDER BY m.DateUtcMs DESC LIMIT @limit",
                    new { accountId, cat = (int)category, limit })).ToList();
            case SmartView.Waiting:
                return await WaitingAsync(conn, accountId, limit, waitingDays);
            default:
                return [];
        }
    }

    /// <summary>
    /// Sent mail nobody has answered. A reply is any later message in a non-Sent folder of the account that names the sent
    /// message in In-Reply-To or References, or that comes from one of its recipients with the same normalised subject.
    /// Mail sent only to yourself, or with no recipients, is skipped.
    /// </summary>
    private static async Task<IReadOnlyList<MessageSummary>> WaitingAsync(SqliteConnection conn, long accountId, int limit, int waitingDays)
    {
        var account = await conn.QuerySingleOrDefaultAsync<Account>("SELECT * FROM accounts WHERE Id=@accountId", new { accountId });
        if (account is null) return [];
        var since = DateTimeOffset.UtcNow.AddDays(-waitingDays).ToUnixTimeMilliseconds();
        var rows = (await conn.QueryAsync<MessageSummary>("""
            SELECT s.*, fo.DisplayName AS FolderName
            FROM messages s JOIN folders fo ON fo.Id = s.FolderId
            WHERE fo.AccountId=@accountId AND fo.Kind=@sent AND s.DateUtcMs >= @since
              AND s.ToAddresses <> '' AND lower(s.ToAddresses) <> lower(@me)
              AND NOT EXISTS (
                  SELECT 1 FROM messages r JOIN folders rf ON rf.Id = r.FolderId
                  WHERE rf.AccountId=@accountId AND rf.Kind NOT IN (2, 3) AND r.DateUtcMs > s.DateUtcMs
                    AND lower(r.FromAddress) <> lower(@me)
                    AND (
                        (s.ThreadKey <> '' AND r.ThreadKey = s.ThreadKey)
                        OR (s.MessageId <> '' AND (r.InReplyTo = s.MessageId OR instr(r.ReferencesHeader, s.MessageId) > 0))
                        OR (s.SubjectKey <> '' AND r.SubjectKey = s.SubjectKey AND r.FromAddress <> ''
                            AND instr(lower(s.ToAddresses || ',' || s.CcAddresses), lower(r.FromAddress)) > 0)
                    ))
            ORDER BY s.DateUtcMs DESC LIMIT @limit
            """, new { accountId, sent = (int)FolderKind.Sent, since, me = account.Email, limit })).ToList();

        var now = DateTimeOffset.UtcNow;
        foreach (var m in rows)
        {
            m.ShowRecipients = true;
            var days = (int)Math.Floor((now - DateTimeOffset.FromUnixTimeMilliseconds(m.DateUtcMs)).TotalDays);
            m.Badge = days switch { <= 0 => "Sent today", 1 => "Waiting 1 day", _ => $"Waiting {days} days" };
        }
        return rows;
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
                INSERT INTO bodies (MessageId, TextBody, HtmlBody, CalendarIcs, FetchedUtcMs) VALUES (@MessageId, @TextBody, @HtmlBody, @CalendarIcs, @FetchedUtcMs)
                ON CONFLICT(MessageId) DO UPDATE SET TextBody=excluded.TextBody, HtmlBody=excluded.HtmlBody, CalendarIcs=excluded.CalendarIcs, FetchedUtcMs=excluded.FetchedUtcMs
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

    // ---------------- Rules ----------------

    private static readonly System.Text.Json.JsonSerializerOptions RuleJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private sealed class RuleRow
    {
        public long Id { get; set; }
        public long AccountId { get; set; }
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public int SortOrder { get; set; }
        public string ConditionsJson { get; set; } = "[]";
        public string Action { get; set; } = "";
        public string? TargetFolder { get; set; }
        public long CreatedUtcMs { get; set; }
        public int HitCount { get; set; }

        public MailRule ToRule() => new()
        {
            Id = Id, AccountId = AccountId, Name = Name, Enabled = Enabled, SortOrder = SortOrder,
            Conditions = System.Text.Json.JsonSerializer.Deserialize<List<RuleCondition>>(ConditionsJson, RuleJson) ?? [],
            Action = Enum.TryParse<RuleAction>(Action, out var a) ? a : RuleAction.MoveToFolder,
            TargetFolder = TargetFolder, CreatedUtcMs = CreatedUtcMs, HitCount = HitCount,
        };
    }

    public async Task<IReadOnlyList<MailRule>> GetRulesAsync(long accountId, CancellationToken ct = default)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<RuleRow>("SELECT * FROM rules WHERE AccountId=@accountId ORDER BY SortOrder, Id", new { accountId });
        return rows.Select(r => r.ToRule()).ToList();
    }

    public async Task<long> UpsertRuleAsync(MailRule rule, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            var p = new
            {
                rule.Id, rule.AccountId, rule.Name, rule.Enabled, rule.SortOrder,
                ConditionsJson = System.Text.Json.JsonSerializer.Serialize(rule.Conditions, RuleJson),
                Action = rule.Action.ToString(), rule.TargetFolder,
                CreatedUtcMs = rule.CreatedUtcMs == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : rule.CreatedUtcMs,
                rule.HitCount,
            };
            if (rule.Id == 0)
            {
                if (rule.SortOrder == 0)
                    rule.SortOrder = (await conn.ExecuteScalarAsync<int?>("SELECT MAX(SortOrder) FROM rules WHERE AccountId=@AccountId", p) ?? 0) + 1;
                rule.Id = await conn.ExecuteScalarAsync<long>("""
                    INSERT INTO rules (AccountId, Name, Enabled, SortOrder, ConditionsJson, Action, TargetFolder, CreatedUtcMs, HitCount)
                    VALUES (@AccountId, @Name, @Enabled, @SortOrder, @ConditionsJson, @Action, @TargetFolder, @CreatedUtcMs, @HitCount);
                    SELECT last_insert_rowid();
                    """, new { p.AccountId, p.Name, p.Enabled, rule.SortOrder, p.ConditionsJson, p.Action, p.TargetFolder, p.CreatedUtcMs, p.HitCount });
                rule.CreatedUtcMs = p.CreatedUtcMs;
            }
            else
            {
                await conn.ExecuteAsync("""
                    UPDATE rules SET Name=@Name, Enabled=@Enabled, SortOrder=@SortOrder, ConditionsJson=@ConditionsJson,
                        Action=@Action, TargetFolder=@TargetFolder WHERE Id=@Id
                    """, p);
            }
            return rule.Id;
        }
        finally { _writeLock.Release(); }
    }

    public async Task DeleteRuleAsync(long ruleId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("DELETE FROM rules WHERE Id=@ruleId", new { ruleId });
        }
        finally { _writeLock.Release(); }
    }

    public async Task IncrementRuleHitsAsync(long ruleId, int count, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = Open();
            await conn.ExecuteAsync("UPDATE rules SET HitCount = HitCount + @count WHERE Id=@ruleId", new { ruleId, count });
        }
        finally { _writeLock.Release(); }
    }
}
