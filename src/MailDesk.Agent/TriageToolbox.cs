using System.Text.Json;
using System.Text.Json.Serialization;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.Agent;

/// <summary>
/// The only things the model can do. Read-only queries over the local store for one folder, plus recorders
/// that turn the model's suggestions into AgentProposal objects. Nothing here touches the server, and every
/// message id the model sends back is checked against the folder it was given.
/// </summary>
public sealed class TriageToolbox
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IMailStore _store;
    private readonly long _accountId;
    private readonly long _folderId;
    private readonly bool _sharePreviews;
    private IReadOnlyList<MessageSummary>? _messages;
    private HashSet<long>? _ids;

    public TriageToolbox(IMailStore store, long accountId, long folderId, bool sharePreviews)
    {
        _store = store;
        _accountId = accountId;
        _folderId = folderId;
        _sharePreviews = sharePreviews;
    }

    public List<AgentProposal> Proposals { get; } = [];
    public string? FinishSummary { get; private set; }
    public bool Finished => FinishSummary is not null;

    private async Task<IReadOnlyList<MessageSummary>> MessagesAsync(CancellationToken ct)
    {
        _messages ??= await _store.GetMessagesAsync(_folderId, limit: 10_000, ct: ct);
        _ids ??= _messages.Select(m => m.Id).ToHashSet();
        return _messages;
    }

    // ---------------- Tool definitions (JSON schema) ----------------

    public static readonly IReadOnlyList<ToolSpec> Tools =
    [
        new("list_folders", "List the account's folders with their kind and message counts. Use folder names from here as move targets.",
            new { type = "object", properties = new { }, additionalProperties = false }),
        new("get_sender_stats", "Senders in this folder ranked by message count, with unread count and an example subject. Start here: it shows the bulk senders cheaply.",
            new { type = "object", properties = new { top = new { type = "integer", description = "How many senders to return (default 80, max 300)" } }, additionalProperties = false }),
        new("list_messages", "Page through message headers in this folder. Filter by sender address substring or subject substring to get the ids for one sender cheaply. Returns id, from, subject, date, read/flagged and a short preview.",
            new { type = "object", properties = new
            {
                from_filter = new { type = "string", description = "Case-insensitive substring of the sender address or name" },
                subject_filter = new { type = "string", description = "Case-insensitive substring of the subject" },
                offset = new { type = "integer" },
                limit = new { type = "integer", description = "Default 100, max 300" },
            }, additionalProperties = false }),
        new("propose_move", "Propose moving a set of messages to a folder. Use target 'Trash' for junk the user will never want, 'Junk' for scam or phishing, or any folder name (new names are created). The user reviews and approves; nothing moves now.",
            new { type = "object", properties = new
            {
                message_ids = new { type = "array", items = new { type = "integer" }, description = "Ids from list_messages" },
                target_folder = new { type = "string" },
                title = new { type = "string", description = "One line the user sees, e.g. 'Move 42 LinkedIn job alerts to Trash'" },
                rationale = new { type = "string", description = "One or two sentences on why" },
            }, required = new[] { "message_ids", "target_folder", "title", "rationale" }, additionalProperties = false }),
        new("propose_rules", "Propose standing rules so future mail from repeat automated senders never reaches the Inbox. One rule per sender. The user reviews and approves.",
            new { type = "object", properties = new
            {
                title = new { type = "string" },
                rationale = new { type = "string" },
                rules = new { type = "array", items = new { type = "object", properties = new
                {
                    name = new { type = "string" },
                    field = new { type = "string", @enum = new[] { "FromAddress", "FromName", "SenderDomain", "Subject" } },
                    @operator = new { type = "string", @enum = new[] { "Contains", "Equals", "StartsWith", "EndsWith" } },
                    value = new { type = "string" },
                    action = new { type = "string", @enum = new[] { "MoveToFolder", "MoveToTrash", "MarkRead", "Flag" } },
                    target_folder = new { type = "string", description = "Required for MoveToFolder" },
                }, required = new[] { "field", "operator", "value", "action" } } },
            }, required = new[] { "title", "rationale", "rules" }, additionalProperties = false }),
        new("propose_tasks", "Propose to-do items for mail that needs the person to act: a bill to pay, a form to return, a reply owed, a booking to confirm, a deadline. One task per message, with the message id, a short imperative title and the due date if the mail states one. The user reviews and approves; never for marketing or notifications.",
            new { type = "object", properties = new
            {
                title = new { type = "string", description = "One line for the review pane, e.g. 'Add 3 tasks for mail that needs a reply'" },
                rationale = new { type = "string" },
                tasks = new { type = "array", items = new { type = "object", properties = new
                {
                    title = new { type = "string", description = "Imperative, under 80 characters, e.g. 'Pay the rates notice'" },
                    notes = new { type = "string", description = "Optional detail: amount, who, what to send" },
                    due_date = new { type = "string", description = "YYYY-MM-DD if the mail states a deadline, else omit" },
                    message_id = new { type = "integer", description = "Id from list_messages" },
                }, required = new[] { "title", "message_id" } } },
            }, required = new[] { "title", "rationale", "tasks" }, additionalProperties = false }),
        new("finish", "Call when you have made all your proposals. Give a one-paragraph summary for the user.",
            new { type = "object", properties = new { summary = new { type = "string" } }, required = new[] { "summary" }, additionalProperties = false }),
    ];

    public sealed record ToolSpec(string Name, string Description, object Schema);

    // ---------------- Dispatch ----------------

    /// <summary>Runs one tool call and returns the JSON text to hand back to the model. Never throws for bad input; it explains instead.</summary>
    public async Task<string> ExecuteAsync(string name, JsonElement input, CancellationToken ct = default)
    {
        try
        {
            return name switch
            {
                "list_folders" => await ListFoldersAsync(ct),
                "get_sender_stats" => await SenderStatsAsync(Int(input, "top", 80, 300), ct),
                "list_messages" => await ListMessagesAsync(Str(input, "from_filter"), Str(input, "subject_filter"), Int(input, "offset", 0, int.MaxValue), Int(input, "limit", 100, 300), ct),
                "propose_move" => await ProposeMoveAsync(input, ct),
                "propose_rules" => ProposeRules(input),
                "propose_tasks" => await ProposeTasksAsync(input, ct),
                "finish" => Finish(Str(input, "summary") ?? ""),
                _ => Error($"Unknown tool '{name}'."),
            };
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private async Task<string> ListFoldersAsync(CancellationToken ct)
    {
        var folders = await _store.GetFoldersAsync(_accountId, ct);
        return JsonSerializer.Serialize(folders.Select(f => new { name = f.DisplayName, kind = f.Kind.ToString(), total = f.TotalCount, unread = f.UnreadCount, current = f.Id == _folderId }), Json);
    }

    private async Task<string> SenderStatsAsync(int top, CancellationToken ct)
    {
        var stats = await _store.GetSenderStatsAsync(_folderId, top, ct);
        return JsonSerializer.Serialize(stats.Select(s => new { address = s.FromAddress, name = s.FromName, count = s.Count, unread = s.UnreadCount, example_subject = Trim(s.ExampleSubject, 80) }), Json);
    }

    private async Task<string> ListMessagesAsync(string? fromFilter, string? subjectFilter, int offset, int limit, CancellationToken ct)
    {
        var all = await MessagesAsync(ct);
        IEnumerable<MessageSummary> q = all;
        if (!string.IsNullOrWhiteSpace(fromFilter))
            q = q.Where(m => m.FromAddress.Contains(fromFilter, StringComparison.OrdinalIgnoreCase) || m.FromName.Contains(fromFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(subjectFilter))
            q = q.Where(m => m.Subject.Contains(subjectFilter, StringComparison.OrdinalIgnoreCase));
        var filtered = q.ToList();
        var page = filtered.Skip(offset).Take(limit).Select(m => new
        {
            id = m.Id,
            from = m.FromDisplay,
            address = m.FromAddress,
            subject = Trim(m.Subject, 100),
            date = m.Date.ToString("yyyy-MM-dd"),
            read = m.IsRead,
            flagged = m.IsFlagged,
            preview = _sharePreviews ? Trim(m.Snippet, 120) : null,
        });
        return JsonSerializer.Serialize(new { total = filtered.Count, offset, returned = Math.Min(limit, Math.Max(0, filtered.Count - offset)), messages = page }, Json);
    }

    private async Task<string> ProposeMoveAsync(JsonElement input, CancellationToken ct)
    {
        await MessagesAsync(ct);
        var requested = input.TryGetProperty("message_ids", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.TryGetInt64(out _)).Select(e => e.GetInt64()).Distinct().ToList()
            : [];
        var valid = requested.Where(id => _ids!.Contains(id)).ToList();
        var unknown = requested.Count - valid.Count;
        var target = (Str(input, "target_folder") ?? "").Trim();
        if (valid.Count == 0) return Error("No valid message ids. Use ids returned by list_messages.");
        if (target.Length == 0) return Error("target_folder is required.");

        Proposals.Add(new AgentProposal
        {
            Kind = ProposalKind.MoveMessages,
            Title = Str(input, "title") ?? $"Move {valid.Count} messages to {target}",
            Rationale = Str(input, "rationale") ?? "",
            AccountId = _accountId, SourceFolderId = _folderId,
            MessageIds = valid, TargetFolderName = target,
        });
        return JsonSerializer.Serialize(new { recorded = true, proposal_index = Proposals.Count, messages = valid.Count, ignored_unknown_ids = unknown });
    }

    private string ProposeRules(JsonElement input)
    {
        var rules = new List<MailRule>();
        var problems = new List<string>();
        if (input.TryGetProperty("rules", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                if (!Enum.TryParse<RuleField>(Str(r, "field"), true, out var field) || field == RuleField.ToAddresses) { problems.Add("bad field"); continue; }
                if (!Enum.TryParse<RuleOperator>(Str(r, "operator"), true, out var op) || op == RuleOperator.Regex) { problems.Add("bad operator"); continue; }
                if (!Enum.TryParse<RuleAction>(Str(r, "action"), true, out var action)) { problems.Add("bad action"); continue; }
                var value = (Str(r, "value") ?? "").Trim();
                if (value.Length < 3) { problems.Add($"value too short: '{value}'"); continue; }
                var target = Str(r, "target_folder");
                if (action == RuleAction.MoveToFolder && string.IsNullOrWhiteSpace(target)) { problems.Add("MoveToFolder needs target_folder"); continue; }
                rules.Add(new MailRule
                {
                    AccountId = _accountId,
                    Name = Str(r, "name") is { Length: > 0 } n ? n : $"{MailRule.Label(field)} {MailRule.Label(op)} {value}",
                    Conditions = [new RuleCondition(field, op, value)],
                    Action = action,
                    TargetFolder = action == RuleAction.MoveToFolder ? target : null,
                });
            }
        }
        if (rules.Count == 0) return Error("No valid rules. " + string.Join("; ", problems));
        Proposals.Add(new AgentProposal
        {
            Kind = ProposalKind.CreateRule,
            Title = Str(input, "title") ?? $"Create {rules.Count} rules",
            Rationale = Str(input, "rationale") ?? "",
            AccountId = _accountId, SourceFolderId = _folderId, Rules = rules,
        });
        return JsonSerializer.Serialize(new { recorded = true, proposal_index = Proposals.Count, rules = rules.Count, rejected = problems });
    }

    private async Task<string> ProposeTasksAsync(JsonElement input, CancellationToken ct)
    {
        var all = await MessagesAsync(ct);
        var byId = all.ToDictionary(m => m.Id);
        var tasks = new List<TaskItem>();
        var problems = new List<string>();
        if (input.TryGetProperty("tasks", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var title = (Str(t, "title") ?? "").Trim();
                if (title.Length < 3) { problems.Add("title too short"); continue; }
                if (!(t.ValueKind == JsonValueKind.Object && t.TryGetProperty("message_id", out var idEl) && idEl.TryGetInt64(out var id)) || !byId.TryGetValue(id, out var source))
                { problems.Add($"unknown message_id for '{title}'"); continue; }
                long? due = null;
                if (Str(t, "due_date") is { Length: > 0 } d)
                {
                    if (DateTime.TryParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                        due = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)).ToUnixTimeMilliseconds();
                    else problems.Add($"bad due_date '{d}' ignored");
                }
                tasks.Add(new TaskItem
                {
                    AccountId = _accountId, Title = title.Length > 120 ? title[..120] : title, Notes = Str(t, "notes") ?? "",
                    DueUtcMs = due, SourceMessageId = source.Id, SourceSubject = source.Subject,
                });
            }
        }
        if (tasks.Count == 0) return Error("No valid tasks. " + string.Join("; ", problems));
        var list = string.Join("\n", tasks.Select(t => "• " + t.Title + (t.DueUtcMs is null ? "" : $" (due {t.DueText})")));
        Proposals.Add(new AgentProposal
        {
            Kind = ProposalKind.CreateTask,
            Title = Str(input, "title") ?? $"Add {tasks.Count} task{(tasks.Count == 1 ? "" : "s")}",
            Rationale = ((Str(input, "rationale") ?? "").Trim() + "\n" + list).Trim(),
            AccountId = _accountId, SourceFolderId = _folderId, Tasks = tasks,
            MessageIds = tasks.Select(t => t.SourceMessageId!.Value).ToList(),
        });
        return JsonSerializer.Serialize(new { recorded = true, proposal_index = Proposals.Count, tasks = tasks.Count, rejected = problems });
    }

    private string Finish(string summary)
    {
        FinishSummary = summary;
        return JsonSerializer.Serialize(new { done = true, proposals = Proposals.Count });
    }

    // ---------------- helpers ----------------

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string name, int fallback, int max)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? Math.Clamp(i, 0, max) : fallback;
}
