using System.Text.Json;
using MailDesk.Core.Security;
using MailDesk.Core.Storage;
using OpenAI;
using OpenAI.Chat;

namespace MailDesk.Agent;

/// <summary>
/// OpenAI-backed triage using the same read-only toolbox as the Claude agent, through Chat Completions
/// function calling. Same safety contract: the model proposes, the user approves, bodies are never sent.
/// </summary>
public sealed class OpenAITriageAgent : IAgentService
{
    private readonly IMailStore _store;
    private readonly ICredentialProtector _protector;

    public OpenAITriageAgent(IMailStore store, ICredentialProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    public string Name { get; private set; } = "OpenAI";
    public int MaxTurns { get; init; } = 30;
    public string? LastSummary { get; private set; }
    public long LastInputTokens { get; private set; }
    public long LastOutputTokens { get; private set; }

    public string? LastRunNote => LastSummary is null && LastInputTokens == 0 ? null
        : $"{LastSummary}\n\nTokens: {LastInputTokens:N0} in, {LastOutputTokens:N0} out.";

    /// <summary>Checks the key and model by retrieving the model's metadata. Throws with a readable message on failure.</summary>
    public static async Task TestAsync(string apiKey, string model, CancellationToken ct = default)
    {
        var client = new OpenAIClient(apiKey);
        _ = await client.GetOpenAIModelClient().GetModelAsync(model, ct);
    }

    public async Task<IReadOnlyList<AgentProposal>> ProposeTriageAsync(long accountId, long folderId, CancellationToken ct = default)
    {
        var settings = await AgentSettings.LoadAsync(_store, ct);
        var apiKey = await AgentSettings.LoadApiKeyAsync(_store, _protector, AgentSettings.KeyOpenAIApiKey, "OPENAI_API_KEY", ct)
            ?? throw new InvalidOperationException("No OpenAI API key is set. Open AI settings and add one.");
        var model = settings.OpenAIModel;
        Name = $"OpenAI ({model})";

        var folder = await _store.GetFolderAsync(folderId, ct) ?? throw new InvalidOperationException("Folder not found.");
        var toolbox = new TriageToolbox(_store, accountId, folderId, settings.SharePreviews);
        var client = new ChatClient(model, apiKey);

        var options = new ChatCompletionOptions();
        foreach (var t in TriageToolbox.Tools)
            options.Tools.Add(ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(JsonSerializer.Serialize(t.Schema))));

        List<ChatMessage> messages =
        [
            new SystemChatMessage(TriagePrompts.System),
            new UserChatMessage($"Triage the folder \"{folder.DisplayName}\" ({folder.TotalCount} messages, {folder.UnreadCount} unread). " +
                                "Start with get_sender_stats, then use list_messages with from_filter to collect ids per sender. " +
                                "Make your proposals, then call finish."),
        ];

        LastInputTokens = LastOutputTokens = 0;
        for (var turn = 0; turn < MaxTurns && !toolbox.Finished; turn++)
        {
            ChatCompletion completion = await client.CompleteChatAsync(messages, options, ct);
            if (completion.Usage is { } usage)
            {
                LastInputTokens += usage.InputTokenCount;
                LastOutputTokens += usage.OutputTokenCount;
            }

            if (completion.FinishReason == ChatFinishReason.ToolCalls && completion.ToolCalls.Count > 0)
            {
                messages.Add(new AssistantChatMessage(completion));
                foreach (var call in completion.ToolCalls)
                {
                    JsonElement input;
                    try { input = JsonSerializer.Deserialize<JsonElement>(call.FunctionArguments); }
                    catch (JsonException) { input = JsonSerializer.SerializeToElement(new { }); }
                    var output = await toolbox.ExecuteAsync(call.FunctionName, input, ct);
                    messages.Add(new ToolChatMessage(call.Id, output));
                }
                continue;
            }

            if (completion.FinishReason == ChatFinishReason.Length)
                throw new InvalidOperationException("The model ran out of output tokens before finishing.");
            if (completion.FinishReason == ChatFinishReason.ContentFilter)
                throw new InvalidOperationException("The model's content filter stopped the run.");
            break; // Stop: the model is done talking; take whatever it proposed.
        }

        LastSummary = toolbox.FinishSummary;
        return toolbox.Proposals;
    }
}
