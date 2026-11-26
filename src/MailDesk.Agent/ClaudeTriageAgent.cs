using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using MailDesk.Core.Security;
using MailDesk.Core.Storage;

namespace MailDesk.Agent;

/// <summary>
/// Claude-backed triage. The model gets read-only tools over the folder's headers and two proposal tools.
/// It cannot move, flag, send or delete anything; it can only describe what it would do, and the review
/// pane shows that to the user. Message bodies are never sent; previews are optional.
/// </summary>
public sealed class ClaudeTriageAgent : IAgentService
{
    private readonly IMailStore _store;
    private readonly ICredentialProtector _protector;

    public ClaudeTriageAgent(IMailStore store, ICredentialProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    public string Name { get; private set; } = "Claude";

    /// <summary>Upper bound on model turns per run, so a confused model cannot loop forever on the user's bill.</summary>
    public int MaxTurns { get; init; } = 30;

    public string? LastSummary { get; private set; }
    public long LastInputTokens { get; private set; }
    public long LastOutputTokens { get; private set; }

    public string? LastRunNote => LastSummary is null && LastInputTokens == 0 ? null
        : $"{LastSummary}\n\nTokens: {LastInputTokens:N0} in, {LastOutputTokens:N0} out.";

    /// <summary>Checks the key and model by retrieving the model's metadata. Throws with a readable message on failure.</summary>
    public static async Task TestAsync(string apiKey, string model, CancellationToken ct = default)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        _ = await client.Models.Retrieve(model);
    }

    public async Task<IReadOnlyList<AgentProposal>> ProposeTriageAsync(long accountId, long folderId, CancellationToken ct = default)
    {
        var settings = await AgentSettings.LoadAsync(_store, ct);
        var apiKey = await AgentSettings.LoadApiKeyAsync(_store, _protector, ct)
            ?? throw new InvalidOperationException("No Anthropic API key is set. Open AI settings and add one.");
        Name = $"Claude ({settings.Model})";

        var folder = await _store.GetFolderAsync(folderId, ct) ?? throw new InvalidOperationException("Folder not found.");
        var toolbox = new TriageToolbox(_store, accountId, folderId, settings.SharePreviews);
        var client = new AnthropicClient { ApiKey = apiKey };

        var tools = TriageToolbox.Tools.Select(t => new BetaToolUnion(new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = ToInputSchema(t.Schema),
        })).ToList();

        List<BetaMessageParam> messages =
        [
            new BetaUserMessageParam
            {
                Content = $"Triage the folder \"{folder.DisplayName}\" ({folder.TotalCount} messages, {folder.UnreadCount} unread). " +
                          "Start with get_sender_stats, then use list_messages with from_filter to collect ids per sender. " +
                          "Make your proposals, then call finish.",
            },
        ];

        LastInputTokens = LastOutputTokens = 0;
        for (var turn = 0; turn < MaxTurns && !toolbox.Finished; turn++)
        {
            var response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = settings.Model,
                MaxTokens = 16000,
                System = SystemPrompt,
                Tools = tools,
                Messages = messages,
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
            }, cancellationToken: ct);

            LastInputTokens += response.Usage.InputTokens;
            LastOutputTokens += response.Usage.OutputTokens;

            if (response.StopReason == "refusal")
            {
                var why = response.StopDetails?.Explanation ?? "the request was declined by a safety classifier";
                throw new InvalidOperationException($"Claude declined to triage this folder: {why}");
            }

            // Echo the assistant turn back, then answer every tool call in one user turn.
            List<BetaContentBlockParam> assistant = [];
            List<BetaContentBlockParam> results = [];
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text)) assistant.Add(new BetaTextBlockParam { Text = text.Text });
                else if (block.TryPickThinking(out var thinking)) assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                else if (block.TryPickRedactedThinking(out var redacted)) assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                else if (block.TryPickToolUse(out var toolUse))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    var input = JsonSerializer.SerializeToElement(toolUse.Input);
                    var output = await toolbox.ExecuteAsync(toolUse.Name, input, ct);
                    results.Add(new BetaToolResultBlockParam { ToolUseID = toolUse.ID, Content = output });
                }
            }
            if (assistant.Count > 0) messages.Add(new BetaAssistantMessageParam { Content = assistant });

            if (response.StopReason != "tool_use" || results.Count == 0)
            {
                // The model stopped talking without calling finish; take whatever it proposed.
                if (response.StopReason == "max_tokens") throw new InvalidOperationException("Claude ran out of output tokens before finishing.");
                break;
            }
            messages.Add(new BetaUserMessageParam { Content = results });
        }

        LastSummary = toolbox.FinishSummary;
        return toolbox.Proposals;
    }

    /// <summary>The SDK schema type takes properties as JSON elements plus a required list; build it from the plain schema object.</summary>
    private static InputSchema ToInputSchema(object schema)
    {
        var el = JsonSerializer.SerializeToElement(schema);
        var props = new Dictionary<string, JsonElement>();
        if (el.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
            foreach (var prop in p.EnumerateObject()) props[prop.Name] = prop.Value.Clone();
        var required = el.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString()!).ToList()
            : [];
        return new InputSchema { Properties = props, Required = required };
    }

    private const string SystemPrompt = """
        You are the triage assistant inside a desktop email client. You are looking at one folder of one person's mailbox
        through tools. You cannot move, delete, flag or send anything: you can only propose, and the person approves each
        proposal in a review pane. Be useful and decisive, but never propose touching mail that could matter.

        Goals, in order:
        1. Automated bulk mail the person will never want: job-board alerts, marketing and sale promotions, newsletters,
           expired one-time codes and sign-in links, bounce notices, auto-replies, social-network notifications.
           Propose moving these to "Trash", grouped by kind, with a clear title and rationale.
        2. Scam and phishing: a brand name in the display name with a mismatched or throwaway domain, lookalike characters,
           urgency about storage, renewals, payments, logins or prizes. Propose moving these to "Junk".
        3. Repeat automated senders (three or more messages): also propose standing rules with propose_rules
           (field FromAddress, operator Equals, action MoveToTrash) so future mail skips the Inbox.
        4. Optionally propose moving clearly groupable real mail into named folders, for example receipts, bills,
           statements, a school, a club. Only when the grouping is obvious from sender and subject.

        Never propose moving: personal correspondence, anything from a lawyer, court, government agency, bank, lender,
        debt collector, employer, school or doctor, invoices, statements, contracts, or anything that reads like a reply
        to the person. When unsure, leave it in place. Keep to at most 12 proposals. Use exact message ids from
        list_messages. Work efficiently: get_sender_stats first, then list_messages with from_filter per sender; avoid
        paging through the whole folder unless it is small. Finish with a short summary of what you proposed and what
        you deliberately left alone.
        """;
}
