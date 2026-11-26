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
        await conn.ExecuteAsync(RulesSchema);
        await AddColumnIfMissingAsync(conn, "bodies", "CalendarIcs", "TEXT");
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
        """;

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
