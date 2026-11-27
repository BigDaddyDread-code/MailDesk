using System.Text.Json;
using System.Text.Json.Serialization;
using MailDesk.Core.Models;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;
using MailDesk.Core.Text;

namespace MailDesk.Agent;

/// <summary>
/// Read-only tools for answering questions about the mailbox: search, list, read one message. Reading a message sends
/// its text to the model, which is what the user asked for by asking the question. Nothing here can change anything.
/// </summary>
public sealed class MailboxToolbox
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const int MaxBodyChars = 8_000;

    private readonly IMailStore _store;
    private readonly IMailSync? _sync;
    private readonly Account _account;

    public MailboxToolbox(IMailStore store, IMailSync? sync, Account account)
    {
        _store = store;
        _sync = sync;
        _account = account;
    }

    /// <summary>Messages the model opened or found, in order, so the answer can link to them.</summary>
    public List<MessageSummary> Consulted { get; } = [];

    public static readonly IReadOnlyList<TriageToolbox.ToolSpec> Tools =
    [
        new("search_messages", "Search across every folder: free text over subject, sender and preview (AND/OR/NOT and -word work), plus operators from:, to:, subject:, in:folder, has:attachment, is:unread, is:flagged, before:/after:/on: YYYY-MM-DD (or YYYY-MM, YYYY, today, yesterday, 7d), larger:/smaller: 10mb. A word that is also a folder name returns that folder's mail. Returns id, folder, from, subject, date, read state and preview.",
            new { type = "object", properties = new
            {
                query = new { type = "string" },
                limit = new { type = "integer", description = "Default 30, max 100" },
            }, required = new[] { "query" }, additionalProperties = false }),
        new("list_messages", "Newest messages in a folder (default Inbox), optionally unread only. Returns the same fields as search_messages.",
            new { type = "object", properties = new
            {
                folder = new { type = "string", description = "Folder display name, e.g. Inbox, Sent, Finance & Bills" },
                unread_only = new { type = "boolean" },
                limit = new { type = "integer", description = "Default 30, max 100" },
            }, additionalProperties = false }),
        new("get_message", "The full text of one message by id, plus headers. Use it when the preview is not enough to answer.",
            new { type = "object", properties = new { id = new { type = "integer" } }, required = new[] { "id" }, additionalProperties = false }),
        new("get_conversation", "The whole conversation a message belongs to, across folders (their messages and the user's replies), oldest first, with each message's cached text. Use it to build a timeline, find what was agreed, or see whether the user already answered.",
            new { type = "object", properties = new { message_id = new { type = "integer" } }, required = new[] { "message_id" }, additionalProperties = false }),
        new("list_folders", "Folders with counts.",
            new { type = "object", properties = new { }, additionalProperties = false }),
        new("list_events", "Calendar entries between two dates (default today to 14 days ahead): title, start, end, location, organiser, the user's response.",
            new { type = "object", properties = new
            {
                from_date = new { type = "string", description = "YYYY-MM-DD, inclusive" },
                to_date = new { type = "string", description = "YYYY-MM-DD, inclusive" },
            }, additionalProperties = false }),
        new("list_tasks", "The user's to-do list: open tasks with due dates, or everything with include_done.",
            new { type = "object", properties = new { include_done = new { type = "boolean" } }, additionalProperties = false }),
        new("list_waiting", "Mail the user sent that nobody has answered yet (who owes them a reply), newest first, with how many days each has waited. Also accepts view = priority, newsletters or notifications to list that smart view instead.",
            new { type = "object", properties = new
            {
                view = new { type = "string", description = "waiting (default), priority, newsletters or notifications" },
                limit = new { type = "integer", description = "Default 30, max 100" },
            }, additionalProperties = false }),
        new("suggest_event", "Offer a calendar entry the user can add with one click (an appointment, deadline or meeting found in mail or asked for). It is not added until they click. Use local times.",
            new { type = "object", properties = new
            {
                title = new { type = "string" },
                start = new { type = "string", description = "YYYY-MM-DDTHH:MM local, or YYYY-MM-DD for all day" },
                end = new { type = "string", description = "Same format; optional" },
                location = new { type = "string" },
                notes = new { type = "string" },
                all_day = new { type = "boolean" },
                message_id = new { type = "integer", description = "The message it came from, if any" },
            }, required = new[] { "title", "start" }, additionalProperties = false }),
        new("suggest_task", "Offer a to-do the user can add with one click. Not added until they click.",
            new { type = "object", properties = new
            {
                title = new { type = "string" },
                notes = new { type = "string" },
                due_date = new { type = "string", description = "YYYY-MM-DD, optional" },
                message_id = new { type = "integer", description = "The message it came from, if any" },
            }, required = new[] { "title" }, additionalProperties = false }),
    ];

    /// <summary>Events and tasks the model offered. The UI turns each into an Add button.</summary>
    public List<(EventSuggestion Event, long? MessageId)> SuggestedEvents { get; } = [];
    public List<TaskItem> SuggestedTasks { get; } = [];

    public async Task<string> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default)
    {
        try
        {
            return name switch
            {
                "search_messages" => await SearchAsync(Str(input, "query") ?? "", Int(input, "limit", 30, 100), ct),
                "list_messages" => await ListAsync(Str(input, "folder"), Bool(input, "unread_only"), Int(input, "limit", 30, 100), ct),
                "get_message" => await GetAsync(input, ct),
                "get_conversation" => await ConversationAsync(input, ct),
                "list_folders" => await FoldersAsync(ct),
                "list_events" => await EventsAsync(Str(input, "from_date"), Str(input, "to_date"), ct),
                "list_tasks" => await TasksAsync(Bool(input, "include_done"), ct),
                "list_waiting" => await ViewAsync(Str(input, "view"), Int(input, "limit", 30, 100), ct),
                "suggest_event" => SuggestEvent(input),
                "suggest_task" => SuggestTask(input),
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool '{name}'." }),
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<string> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return JsonSerializer.Serialize(new { error = "query is required" });
        var hits = await _store.SearchAsync(query, _account.Id, null, limit, ct);
        Remember(hits);
        return JsonSerializer.Serialize(new { total = hits.Count, messages = hits.Select(Row) }, Json);
    }

    private async Task<string> ViewAsync(string? viewName, int limit, CancellationToken ct)
    {
        var view = (viewName ?? "waiting").Trim().ToLowerInvariant() switch
        {
            "priority" or "important" => SmartView.Priority,
            "newsletters" or "newsletter" => SmartView.Newsletters,
            "notifications" or "notification" => SmartView.Notifications,
            _ => SmartView.Waiting,
        };
        var rows = (await _store.GetSmartViewAsync(_account.Id, view, limit, ct: ct)).ToList();
        Remember(rows);
        return JsonSerializer.Serialize(new
        {
            view = view.ToString().ToLowerInvariant(),
            total = rows.Count,
            messages = rows.Select(m => new
            {
                id = m.Id, folder = m.FolderName, from = m.FromDisplay, to = m.ToAddresses, subject = m.Subject,
                date = m.Date.ToString("yyyy-MM-dd HH:mm"), status = string.IsNullOrEmpty(m.Badge) ? null : m.Badge,
                read = m.IsRead, flagged = m.IsFlagged, preview = m.Snippet.Length > 160 ? m.Snippet[..160] + "…" : m.Snippet,
            }),
        }, Json);
    }

    private async Task<string> ListAsync(string? folderName, bool unreadOnly, int limit, CancellationToken ct)
    {
        var folders = await _store.GetFoldersAsync(_account.Id, ct);
        var folder = string.IsNullOrWhiteSpace(folderName)
            ? folders.FirstOrDefault(f => f.Kind == FolderKind.Inbox)
            : folders.FirstOrDefault(f => f.DisplayName.Equals(folderName, StringComparison.OrdinalIgnoreCase))
              ?? folders.FirstOrDefault(f => f.DisplayName.Contains(folderName, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return JsonSerializer.Serialize(new { error = $"No folder named '{folderName}'. Use list_folders." });
        var all = await _store.GetMessagesAsync(folder.Id, limit: unreadOnly ? 2000 : limit, ct: ct);
        var page = (unreadOnly ? all.Where(m => !m.IsRead) : all).Take(limit).ToList();
        foreach (var m in page) m.FolderName = folder.DisplayName;
        Remember(page);
        return JsonSerializer.Serialize(new { folder = folder.DisplayName, total_in_folder = folder.TotalCount, unread = folder.UnreadCount, messages = page.Select(Row) }, Json);
    }

    private async Task<string> ConversationAsync(JsonElement input, CancellationToken ct)
    {
        if (!(input.ValueKind == JsonValueKind.Object && input.TryGetProperty("message_id", out var idEl) && idEl.TryGetInt64(out var id)))
            return JsonSerializer.Serialize(new { error = "message_id is required" });
        var seed = await _store.GetMessageAsync(id, ct);
        var folder = seed is null ? null : await _store.GetFolderAsync(seed.FolderId, ct);
        if (seed is null || folder is null || folder.AccountId != _account.Id) return JsonSerializer.Serialize(new { error = "No message with that id." });

        var thread = await _store.GetThreadAsync(_account.Id, seed.ThreadKey, ct);
        if (thread.Count == 0) { seed.FolderName = folder.DisplayName; thread = [seed]; }
        Remember(thread);

        const int perMessage = 2_500;
        var budget = MaxBodyChars * 2;
        var items = new List<object>();
        foreach (var m in thread)
        {
            string? text = null;
            if (budget > 0)
            {
                var body = await _store.GetBodyAsync(m.Id, ct);
                text = ComposeBuilder.BodyText(body);
                text = ComposeBuilder.StripQuotedReply(text);
                if (text.Length > perMessage) text = text[..perMessage] + "\n[truncated]";
                budget -= text.Length;
                if (string.IsNullOrWhiteSpace(text)) text = null;
            }
            items.Add(new
            {
                id = m.Id, folder = m.FolderName, from = m.FromDisplay, address = m.FromAddress, to = m.ToAddresses,
                from_user = m.FromAddress.Equals(_account.Email, StringComparison.OrdinalIgnoreCase),
                date = m.Date.ToString("yyyy-MM-dd HH:mm"), subject = m.Subject, preview = m.Snippet.Length > 160 ? m.Snippet[..160] + "…" : m.Snippet,
                body = text ?? (budget > 0 ? "(not downloaded; use get_message)" : "(omitted for length; use get_message)"),
            });
        }
        return JsonSerializer.Serialize(new { subject = thread[0].Subject, messages = items.Count, timeline = items }, Json);
    }

    private async Task<string> GetAsync(JsonElement input, CancellationToken ct)
    {
        if (!(input.ValueKind == JsonValueKind.Object && input.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)))
            return JsonSerializer.Serialize(new { error = "id is required" });
        var message = await _store.GetMessageAsync(id, ct);
        if (message is null) return JsonSerializer.Serialize(new { error = "No message with that id." });
        var folder = await _store.GetFolderAsync(message.FolderId, ct);
        if (folder is null || folder.AccountId != _account.Id) return JsonSerializer.Serialize(new { error = "No message with that id." });
        message.FolderName = folder.DisplayName;
        Remember([message]);

        var body = await _store.GetBodyAsync(message.Id, ct);
        if (body is null && _sync is not null)
        {
            try { body = await _sync.FetchBodyAsync(_account, message, ct); }
            catch (Exception ex) { return JsonSerializer.Serialize(new { id = message.Id, error = $"Body could not be downloaded: {ex.Message}", preview = message.Snippet }); }
        }
        var text = ComposeBuilder.BodyText(body);
        if (text.Length > MaxBodyChars) text = text[..MaxBodyChars] + "\n[truncated]";
        return JsonSerializer.Serialize(new
        {
            id = message.Id, folder = message.FolderName, from = message.FromDisplay, address = message.FromAddress, to = message.ToAddresses,
            subject = message.Subject, date = message.Date.ToString("yyyy-MM-dd HH:mm"), has_attachments = message.HasAttachments,
            body = string.IsNullOrWhiteSpace(text) ? "(no cached text)" : text,
        }, Json);
    }

    private async Task<string> FoldersAsync(CancellationToken ct)
    {
        var folders = await _store.GetFoldersAsync(_account.Id, ct);
        return JsonSerializer.Serialize(folders.Select(f => new { name = f.DisplayName, kind = f.Kind.ToString(), total = f.TotalCount, unread = f.UnreadCount }), Json);
    }

    private async Task<string> EventsAsync(string? fromText, string? toText, CancellationToken ct)
    {
        var from = EventSuggestion.ParseLocal(fromText) ?? new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        var to = (EventSuggestion.ParseLocal(toText) ?? from.AddDays(14)).Date.AddDays(1);
        var toOffset = new DateTimeOffset(to, TimeZoneInfo.Local.GetUtcOffset(to));
        var items = await _store.GetCalendarItemsAsync(_account.Id, from.ToUnixTimeMilliseconds(), toOffset.ToUnixTimeMilliseconds(), ct);
        return JsonSerializer.Serialize(new
        {
            from = from.ToString("yyyy-MM-dd"), to = toOffset.AddDays(-1).ToString("yyyy-MM-dd"),
            events = items.OrderBy(i => i.StartUtcMs).Select(i => new
            {
                title = i.Summary, start = i.Start.ToString("yyyy-MM-dd HH:mm"), end = i.End.ToString("yyyy-MM-dd HH:mm"), all_day = i.AllDay,
                location = i.Location, organiser = string.IsNullOrWhiteSpace(i.OrganizerName) ? i.OrganizerEmail : i.OrganizerName,
                my_response = i.MyResponse.ToString(), recurring = i.IsRecurring, status = i.Status,
            }),
        }, Json);
    }

    private async Task<string> TasksAsync(bool includeDone, CancellationToken ct)
    {
        var tasks = await _store.GetTasksAsync(_account.Id, includeDone, ct);
        return JsonSerializer.Serialize(tasks.Select(t => new
        {
            title = t.Title, notes = t.Notes, due = t.Due?.ToString("yyyy-MM-dd"), overdue = t.IsOverdue, done = t.IsDone, from_message = t.SourceSubject,
        }), Json);
    }

    private string SuggestEvent(JsonElement input)
    {
        var e = new EventSuggestion
        {
            Title = Str(input, "title") ?? "", Start = Str(input, "start") ?? "", End = Str(input, "end") ?? "",
            Location = Str(input, "location") ?? "", Notes = Str(input, "notes") ?? "", AllDay = Bool(input, "all_day"),
        };
        if (!e.IsValid) return JsonSerializer.Serialize(new { error = "title and a parseable start (YYYY-MM-DDTHH:MM or YYYY-MM-DD) are required." });
        long? messageId = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("message_id", out var m) && m.TryGetInt64(out var id) ? id : null;
        SuggestedEvents.Add((e, messageId));
        return JsonSerializer.Serialize(new { offered = true, when = e.WhenText, note = "Shown to the user as an Add to calendar button." });
    }

    private string SuggestTask(JsonElement input)
    {
        var title = (Str(input, "title") ?? "").Trim();
        if (title.Length < 3) return JsonSerializer.Serialize(new { error = "title is required." });
        long? messageId = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("message_id", out var m) && m.TryGetInt64(out var id) ? id : null;
        var due = EventSuggestion.ParseLocal(Str(input, "due_date"));
        var source = messageId is null ? null : Consulted.FirstOrDefault(c => c.Id == messageId);
        SuggestedTasks.Add(new TaskItem
        {
            AccountId = _account.Id, Title = title, Notes = Str(input, "notes") ?? "", DueUtcMs = due?.ToUnixTimeMilliseconds(),
            SourceMessageId = source?.Id, SourceSubject = source?.Subject ?? "",
        });
        return JsonSerializer.Serialize(new { offered = true, note = "Shown to the user as an Add task button." });
    }

    private void Remember(IEnumerable<MessageSummary> messages)
    {
        foreach (var m in messages)
            if (Consulted.All(c => c.Id != m.Id)) Consulted.Add(m);
    }

    private static object Row(MessageSummary m) => new
    {
        id = m.Id, folder = m.FolderName, from = m.FromDisplay, address = m.FromAddress, subject = m.Subject,
        date = m.Date.ToString("yyyy-MM-dd HH:mm"), read = m.IsRead, flagged = m.IsFlagged, attachments = m.HasAttachments,
        preview = m.Snippet.Length > 160 ? m.Snippet[..160] + "…" : m.Snippet,
    };

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string name, int fallback, int max)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? Math.Clamp(i, 1, max) : fallback;
    private static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

public sealed class AssistantAnswer
{
    public string Text { get; init; } = "";
    /// <summary>Messages the model opened or searched up while answering, for the user to click.</summary>
    public IReadOnlyList<MessageSummary> Sources { get; init; } = [];
    public IReadOnlyList<(EventSuggestion Event, long? MessageId)> SuggestedEvents { get; init; } = [];
    public IReadOnlyList<TaskItem> SuggestedTasks { get; init; } = [];
    public string ModelNote { get; init; } = "";
}

/// <summary>"Ask your mailbox": natural-language questions answered by searching and reading mail through read-only tools.</summary>
public sealed class MailboxAssistant
{
    private readonly IMailStore _store;
    private readonly IMailSync? _sync;

    public MailboxAssistant(IMailStore store, IMailSync? sync)
    {
        _store = store;
        _sync = sync;
    }

    public static string System(string userName, string userEmail) => $"""
        You are the mailbox assistant inside {userName}'s email client ({userEmail}). Today is {DateTime.Now:dddd d MMMM yyyy}.
        Answer questions about their mail, calendar and to-do list by using the tools: search first, read a message with
        get_message only when the preview is not enough, get_conversation for the whole exchange behind a message (what was
        asked, what was agreed, whether they already replied), list_events for what is coming up, list_tasks for what is
        owed, list_waiting for who has not replied to them, and keep tool calls few. When summarising a conversation,
        separate what was actually said from what you infer, and say what is still open. Use search operators (from:, to:, after:,
        has:attachment) instead of scanning lists when the question names a person, a date range or attachments. Answer in plain text, briefly and specifically, quoting dates, amounts and names from the
        mail. Mention which message something came from by sender and subject so the person can find it. If the mail does
        not contain the answer, say so rather than guessing. You cannot send, move or change anything yourself. When the
        person asks to put something in the calendar or on the to-do list, or mail clearly contains an appointment or a
        deadline they would want, call suggest_event or suggest_task: that shows them a button, and they decide. Say in
        your answer that you have offered it.
        """;

    public async Task<AssistantAnswer> AskAsync(IModelClient client, Account account, IReadOnlyList<ChatTurn> history, string question, CancellationToken ct = default)
    {
        var toolbox = new MailboxToolbox(_store, _sync, account);
        var result = await client.RunToolsAsync(System(account.Name, account.Email), history, question, MailboxToolbox.Tools, (n, i) => toolbox.ExecuteAsync(n, i, ct), maxTurns: 15, ct);
        return new AssistantAnswer
        {
            Text = string.IsNullOrWhiteSpace(result.Text) ? "I could not find an answer in your mail." : result.Text,
            Sources = toolbox.Consulted.Take(8).ToList(),
            SuggestedEvents = toolbox.SuggestedEvents,
            SuggestedTasks = toolbox.SuggestedTasks,
            ModelNote = $"{client.Name}, {result.InputTokens:N0} in / {result.OutputTokens:N0} out",
        };
    }
}
