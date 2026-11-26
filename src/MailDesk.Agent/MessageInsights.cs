using System.Text.Json;
using System.Text.Json.Serialization;
using MailDesk.Core.Models;

namespace MailDesk.Agent;

/// <summary>What the model made of one message: a summary, the things it asks the reader to do, and short reply options.</summary>
public sealed class MessageInsight
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("action_items")] public List<string> ActionItems { get; set; } = [];
    [JsonPropertyName("suggested_replies")] public List<string> SuggestedReplies { get; set; } = [];
    [JsonPropertyName("tone")] public string Tone { get; set; } = "";
    [JsonIgnore] public long MessageId { get; set; }
    [JsonIgnore] public string ModelNote { get; set; } = "";

    public bool HasActions => ActionItems.Count > 0;
    public bool HasReplies => SuggestedReplies.Count > 0;
}

/// <summary>One call, one message, strict JSON back. The body goes to the model, so this only runs when the user clicks Summarise.</summary>
public static class InsightsService
{
    private const int MaxBodyChars = 12_000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string System(string readerName) => $$"""
        You read one email for {{readerName}} and answer with JSON only, no prose and no markdown fences, in this shape:
        {"summary": "two or three plain sentences saying what the email is and what matters",
         "action_items": ["imperative, one line each, only things {{readerName}} actually needs to do; empty list if nothing"],
         "suggested_replies": ["up to three complete short replies {{readerName}} could send as-is, each under 60 words; empty list for mail that needs no reply such as newsletters or receipts"],
         "tone": "one or two words, e.g. friendly, formal, urgent, marketing"}
        Quote amounts, dates and reference numbers exactly as written. If the email is a scam or phishing attempt, say so in
        the summary and give no replies. Write in the language of the email.
        """;

    public static async Task<MessageInsight> AnalyzeAsync(IModelClient client, string readerName, MessageSummary message, string bodyText, CancellationToken ct = default)
    {
        var body = bodyText.Length > MaxBodyChars ? bodyText[..MaxBodyChars] + "\n[truncated]" : bodyText;
        var user = $"From: {message.FromDisplay} <{message.FromAddress}>\nTo: {message.ToAddresses}\nDate: {message.Date:ddd d MMM yyyy HH:mm}\nSubject: {message.Subject}\n\n{body}";
        var result = await client.CompleteAsync(System(readerName), user, maxTokens: 2000, ct);
        var insight = Parse(result.Text);
        insight.MessageId = message.Id;
        insight.ModelNote = $"{client.Name}, {result.InputTokens:N0} in / {result.OutputTokens:N0} out";
        return insight;
    }

    /// <summary>Tolerates fences and chatter around the JSON object.</summary>
    public static MessageInsight Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return new MessageInsight { Summary = text.Trim() };
        try
        {
            var insight = JsonSerializer.Deserialize<MessageInsight>(text[start..(end + 1)], Json) ?? new MessageInsight();
            insight.ActionItems = insight.ActionItems.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            insight.SuggestedReplies = insight.SuggestedReplies.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToList();
            return insight;
        }
        catch (JsonException)
        {
            return new MessageInsight { Summary = text.Trim() };
        }
    }
}
