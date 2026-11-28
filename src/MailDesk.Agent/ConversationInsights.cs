using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailDesk.Core.Models;
using MailDesk.Core.Text;

namespace MailDesk.Agent;

/// <summary>One decision, commitment or open question found in a conversation, linked to the message it came from.</summary>
public sealed class ThreadItem
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    /// <summary>Who decided, who owes it, or who is waiting for the answer. "you" means the account owner.</summary>
    [JsonPropertyName("who")] public string Who { get; set; } = "";
    [JsonPropertyName("owner")] public string Owner { set => Who = value; get => Who; }
    [JsonPropertyName("due")] public string Due { get; set; } = "";
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("message_id")] public long? MessageId { get; set; }
    /// <summary>"stated" when the message says it in so many words; "inferred" when the model read it between the lines.</summary>
    [JsonPropertyName("basis")] public string Basis { get; set; } = "stated";

    [JsonIgnore] public string Kind { get; set; } = "";
    public bool IsInferred => !Basis.Equals("stated", StringComparison.OrdinalIgnoreCase);
    public bool IsYours => Who.Equals("you", StringComparison.OrdinalIgnoreCase) || Who.StartsWith("you ", StringComparison.OrdinalIgnoreCase);
    public bool HasSource => MessageId is not null;
    public bool CanAddTask => Kind == "commitment" && IsYours && !Done;
    public string Icon => Kind switch { "decision" => "", "commitment" => "", _ => "" };

    /// <summary>One line under the text: who, due, done.</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Who)) parts.Add(Kind == "commitment" ? (IsYours ? "You owe this" : $"{Who} owes this") : Kind == "question" ? $"{Who} is waiting" : Who);
            if (!string.IsNullOrWhiteSpace(Due)) parts.Add($"due {Due}");
            if (Done) parts.Add("done");
            return string.Join(" · ", parts);
        }
    }

    public DateTimeOffset? DueDate =>
        DateTime.TryParseExact(Due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)) : null;
}

/// <summary>Structured knowledge pulled from one conversation. Cached per thread until the thread grows.</summary>
public sealed class ConversationInsight
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("decisions")] public List<ThreadItem> Decisions { get; set; } = [];
    [JsonPropertyName("commitments")] public List<ThreadItem> Commitments { get; set; } = [];
    [JsonPropertyName("open_questions")] public List<ThreadItem> OpenQuestions { get; set; } = [];
    /// <summary>What the thread leaves unclear, or what the model could not determine.</summary>
    [JsonPropertyName("uncertain")] public List<string> Uncertain { get; set; } = [];

    [JsonPropertyName("thread_key")] public string ThreadKey { get; set; } = "";
    /// <summary>How many messages the thread had when this was produced; a longer thread invalidates the cache.</summary>
    [JsonPropertyName("message_count")] public int MessageCount { get; set; }
    [JsonPropertyName("model_note")] public string ModelNote { get; set; } = "";
    [JsonPropertyName("analysed_utc_ms")] public long AnalysedUtcMs { get; set; }

    public bool HasDecisions => Decisions.Count > 0;
    public bool HasCommitments => Commitments.Count > 0;
    public bool HasQuestions => OpenQuestions.Count > 0;
    public bool HasUncertain => Uncertain.Count > 0;
    public bool IsEmpty => !HasDecisions && !HasCommitments && !HasQuestions;
    public string Counts
    {
        get
        {
            var parts = new List<string>();
            if (HasDecisions) parts.Add($"{Decisions.Count} decision{(Decisions.Count == 1 ? "" : "s")}");
            if (HasCommitments) parts.Add($"{Commitments.Count} commitment{(Commitments.Count == 1 ? "" : "s")}");
            if (HasQuestions) parts.Add($"{OpenQuestions.Count} open question{(OpenQuestions.Count == 1 ? "" : "s")}");
            return parts.Count == 0 ? "Nothing was decided, promised or asked in this conversation." : string.Join(", ", parts);
        }
    }

    public string AnalysedText => AnalysedUtcMs == 0 ? "" : $"analysed {DateTimeOffset.FromUnixTimeMilliseconds(AnalysedUtcMs).ToLocalTime():d MMM HH:mm}";

    /// <summary>The items as rows for the cross-conversation Commitments view.</summary>
    public IReadOnlyList<ThreadFact> ToFacts(long accountId, string subject)
    {
        var facts = new List<ThreadFact>();
        foreach (var i in Decisions.Concat(Commitments).Concat(OpenQuestions))
        {
            facts.Add(new ThreadFact
            {
                AccountId = accountId, ThreadKey = ThreadKey, Kind = i.Kind, Text = i.Text, Who = i.Who, IsYours = i.IsYours,
                Due = i.Due, DueUtcMs = i.DueDate?.ToUnixTimeMilliseconds(), IsDone = i.Done, MessageId = i.MessageId,
                Subject = subject, Basis = i.IsInferred ? "inferred" : "stated", AnalysedUtcMs = AnalysedUtcMs,
            });
        }
        return facts;
    }
}

/// <summary>
/// Reads a whole conversation and returns its decisions, commitments and open questions, each tied to a message id, with
/// what was said kept apart from what was inferred. Message text goes to the model, so this runs only when the user clicks.
/// </summary>
public static class ConversationAnalyst
{
    private const int MaxTranscriptChars = 30_000;
    private const int MinPerMessage = 600;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string System(string readerName) => $$"""
        You read one email conversation for {{readerName}} ("you") and answer with JSON only, no prose and no markdown
        fences, in this shape:
        {"summary": "two or three plain sentences on what the conversation is about and where it stands now",
         "decisions": [{"text": "what was decided or agreed, one line", "who": "who agreed it", "message_id": 0, "basis": "stated"}],
         "commitments": [{"text": "what someone promised to do, one line", "owner": "you, or the person's name", "due": "YYYY-MM-DD if a date was given, else a short phrase or empty", "done": false, "message_id": 0, "basis": "stated"}],
         "open_questions": [{"text": "a question asked that no later message answers", "who": "who is waiting for the answer", "message_id": 0}],
         "uncertain": ["anything the thread leaves unclear or that you could not tell, one line each"]}
        Each message in the conversation is labelled "Message id=N". message_id must be one of those N: the message where the
        decision, promise or question appears. "basis" is "stated" only when that message says it in so many words; use
        "inferred" when you are reading it from context. Mark a commitment done=true only when a later message shows it was
        carried out. Leave out anything already withdrawn or superseded, and never invent dates. Quote amounts, dates and
        reference numbers exactly as written. Today is {{DateTime.Now:yyyy-MM-dd}}. Write in the language of the conversation.
        """;

    /// <summary>The conversation as the model sees it: each message's own words (quoted replies stripped), labelled with its local id.</summary>
    public static string BuildTranscript(IReadOnlyList<MessageSummary> thread, IReadOnlyDictionary<long, string> bodies, string ownerEmail)
    {
        var perMessage = Math.Max(MinPerMessage, MaxTranscriptChars / Math.Max(1, thread.Count));
        var sb = new StringBuilder();
        foreach (var m in thread)
        {
            var mine = m.FromAddress.Equals(ownerEmail, StringComparison.OrdinalIgnoreCase);
            bodies.TryGetValue(m.Id, out var raw);
            var text = ComposeBuilder.StripQuotedReply(raw ?? "");
            if (string.IsNullOrWhiteSpace(text)) text = m.Snippet;
            if (text.Length > perMessage) text = text[..perMessage] + "\n[truncated]";
            sb.Append("--- Message id=").Append(m.Id)
              .Append(" | From: ").Append(mine ? "you" : $"{m.FromDisplay} <{m.FromAddress}>")
              .Append(" | To: ").Append(m.ToAddresses)
              .Append(" | Date: ").Append(m.Date.ToString("ddd d MMM yyyy HH:mm"))
              .Append(" | Subject: ").Append(m.Subject).Append(" ---\n")
              .Append(text.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    public static async Task<ConversationInsight> AnalyzeAsync(IModelClient client, string readerName, string ownerEmail, IReadOnlyList<MessageSummary> thread,
        IReadOnlyDictionary<long, string> bodies, CancellationToken ct = default)
    {
        var transcript = BuildTranscript(thread, bodies, ownerEmail);
        var result = await client.CompleteAsync(System(readerName), transcript, maxTokens: 3000, ct);
        var insight = Parse(result.Text, thread.Select(m => m.Id).ToHashSet());
        insight.ThreadKey = thread.Count > 0 ? thread[0].ThreadKey : "";
        insight.MessageCount = thread.Count;
        insight.ModelNote = $"{client.Name}, {result.InputTokens:N0} in / {result.OutputTokens:N0} out";
        insight.AnalysedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return insight;
    }

    /// <summary>Tolerates fences and chatter around the JSON; drops blank items and message ids that are not in the thread.</summary>
    public static ConversationInsight Parse(string text, IReadOnlySet<long>? validIds = null)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return new ConversationInsight { Summary = text.Trim() };
        ConversationInsight insight;
        try { insight = JsonSerializer.Deserialize<ConversationInsight>(text[start..(end + 1)], Json) ?? new ConversationInsight(); }
        catch (JsonException) { return new ConversationInsight { Summary = text.Trim() }; }

        insight.Decisions = Clean(insight.Decisions, "decision", validIds);
        insight.Commitments = Clean(insight.Commitments, "commitment", validIds);
        insight.OpenQuestions = Clean(insight.OpenQuestions, "question", validIds);
        insight.Uncertain = insight.Uncertain.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(6).ToList();
        return insight;
    }

    private static List<ThreadItem> Clean(List<ThreadItem> items, string kind, IReadOnlySet<long>? validIds)
    {
        var list = new List<ThreadItem>();
        foreach (var i in items.Where(i => !string.IsNullOrWhiteSpace(i.Text)).Take(12))
        {
            i.Kind = kind;
            i.Text = i.Text.Trim();
            i.Who = i.Who.Trim();
            i.Due = i.Due.Trim();
            if (i.MessageId is { } id && (id <= 0 || (validIds is not null && !validIds.Contains(id)))) i.MessageId = null;
            list.Add(i);
        }
        return list;
    }

    public static string Serialize(ConversationInsight insight) => JsonSerializer.Serialize(insight, Json);

    public static ConversationInsight? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var insight = JsonSerializer.Deserialize<ConversationInsight>(json, Json);
            if (insight is null) return null;
            foreach (var d in insight.Decisions) d.Kind = "decision";
            foreach (var c in insight.Commitments) c.Kind = "commitment";
            foreach (var q in insight.OpenQuestions) q.Kind = "question";
            return insight;
        }
        catch (JsonException) { return null; }
    }
}
