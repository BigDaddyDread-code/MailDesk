using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using MailDesk.Core.Security;
using MailDesk.Core.Storage;
using OpenAI.Chat;

namespace MailDesk.Agent;

/// <summary>One prior exchange in a conversation, text only. Tool traffic is never carried between turns.</summary>
public sealed record ChatTurn(bool FromUser, string Text);

public sealed class ModelResult
{
    public string Text { get; init; } = "";
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
}

/// <summary>
/// The two things every AI feature needs from a provider: a plain completion, and a tool loop that runs until the
/// model stops calling tools. Provider differences stay in here so features are written once.
/// </summary>
public interface IModelClient
{
    string Name { get; }
    Task<ModelResult> CompleteAsync(string system, string user, int maxTokens = 4000, CancellationToken ct = default);
    Task<ModelResult> RunToolsAsync(string system, IReadOnlyList<ChatTurn> history, string user, IReadOnlyList<TriageToolbox.ToolSpec> tools,
        Func<string, JsonElement, Task<string>> execute, int maxTurns = 20, CancellationToken ct = default);
}

/// <summary>Builds the client for the provider chosen in AI settings. The local heuristic provider has no model, so it throws a readable error.</summary>
public static class ModelClients
{
    public static async Task<IModelClient> CreateAsync(IMailStore store, ICredentialProtector protector, CancellationToken ct = default)
    {
        var settings = await AgentSettings.LoadAsync(store, ct);
        switch (settings.Provider)
        {
            case AgentProvider.Claude:
            {
                var key = await AgentSettings.LoadApiKeyAsync(store, protector, AgentSettings.KeyAnthropicApiKey, "ANTHROPIC_API_KEY", ct)
                    ?? throw new InvalidOperationException("No Anthropic API key is set. Open AI settings and add one.");
                return new ClaudeModelClient(key, settings.ClaudeModel);
            }
            case AgentProvider.OpenAI:
            {
                var key = await AgentSettings.LoadApiKeyAsync(store, protector, AgentSettings.KeyOpenAIApiKey, "OPENAI_API_KEY", ct)
                    ?? throw new InvalidOperationException("No OpenAI API key is set. Open AI settings and add one.");
                return new OpenAIModelClient(key, settings.OpenAIModel);
            }
            default:
                throw new InvalidOperationException("This needs a model. Open AI settings (the gear icon) and choose Claude or OpenAI.");
        }
    }
}

public sealed class ClaudeModelClient : IModelClient
{
    private readonly AnthropicClient _client;
    private readonly string _model;

    public ClaudeModelClient(string apiKey, string model)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
        _model = model;
    }

    public string Name => $"Claude ({_model})";

    public async Task<ModelResult> CompleteAsync(string system, string user, int maxTokens = 4000, CancellationToken ct = default)
    {
        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = _model,
            MaxTokens = maxTokens,
            System = system,
            Messages = [new BetaUserMessageParam { Content = user }],
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
        }, cancellationToken: ct);
        ThrowIfRefused(response);
        return new ModelResult { Text = TextOf(response), InputTokens = response.Usage.InputTokens, OutputTokens = response.Usage.OutputTokens };
    }

    public async Task<ModelResult> RunToolsAsync(string system, IReadOnlyList<ChatTurn> history, string user, IReadOnlyList<TriageToolbox.ToolSpec> tools,
        Func<string, JsonElement, Task<string>> execute, int maxTurns = 20, CancellationToken ct = default)
    {
        var toolParams = tools.Select(t => new BetaToolUnion(new BetaTool { Name = t.Name, Description = t.Description, InputSchema = ToInputSchema(t.Schema) })).ToList();
        List<BetaMessageParam> messages = [];
        foreach (var turn in history)
        {
            if (turn.FromUser) messages.Add(new BetaUserMessageParam { Content = turn.Text });
            else messages.Add(new BetaAssistantMessageParam { Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = turn.Text } } });
        }
        messages.Add(new BetaUserMessageParam { Content = user });

        long inTok = 0, outTok = 0;
        var finalText = "";
        for (var turn = 0; turn < maxTurns; turn++)
        {
            var response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 8000,
                System = system,
                Tools = toolParams,
                Messages = messages,
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
            }, cancellationToken: ct);
            inTok += response.Usage.InputTokens;
            outTok += response.Usage.OutputTokens;
            ThrowIfRefused(response);

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
                    var output = await execute(toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input));
                    results.Add(new BetaToolResultBlockParam { ToolUseID = toolUse.ID, Content = output });
                }
            }
            if (assistant.Count > 0) messages.Add(new BetaAssistantMessageParam { Content = assistant });
            finalText = TextOf(response);
            if (response.StopReason != "tool_use" || results.Count == 0)
            {
                if (response.StopReason == "max_tokens") throw new InvalidOperationException("Claude ran out of output tokens before finishing.");
                break;
            }
            messages.Add(new BetaUserMessageParam { Content = results });
        }
        return new ModelResult { Text = finalText, InputTokens = inTok, OutputTokens = outTok };
    }

    private static void ThrowIfRefused(BetaMessage response)
    {
        if (response.StopReason == "refusal")
            throw new InvalidOperationException("Claude declined: " + (response.StopDetails?.Explanation ?? "a safety classifier stopped the request"));
    }

    private static string TextOf(BetaMessage response) =>
        string.Join("", response.Content.Select(b => b.TryPickText(out var t) ? t.Text : "")).Trim();

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
}

public sealed class OpenAIModelClient : IModelClient
{
    private readonly ChatClient _client;
    private readonly string _model;

    public OpenAIModelClient(string apiKey, string model)
    {
        _client = new ChatClient(model, apiKey);
        _model = model;
    }

    public string Name => $"OpenAI ({_model})";

    public async Task<ModelResult> CompleteAsync(string system, string user, int maxTokens = 4000, CancellationToken ct = default)
    {
        ChatCompletion completion = await _client.CompleteChatAsync([new SystemChatMessage(system), new UserChatMessage(user)], cancellationToken: ct);
        if (completion.FinishReason == ChatFinishReason.ContentFilter) throw new InvalidOperationException("The model's content filter stopped the request.");
        return new ModelResult
        {
            Text = string.Join("", completion.Content.Select(c => c.Text)).Trim(),
            InputTokens = completion.Usage?.InputTokenCount ?? 0,
            OutputTokens = completion.Usage?.OutputTokenCount ?? 0,
        };
    }

    public async Task<ModelResult> RunToolsAsync(string system, IReadOnlyList<ChatTurn> history, string user, IReadOnlyList<TriageToolbox.ToolSpec> tools,
        Func<string, JsonElement, Task<string>> execute, int maxTurns = 20, CancellationToken ct = default)
    {
        var options = new ChatCompletionOptions();
        foreach (var t in tools)
            options.Tools.Add(ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(JsonSerializer.Serialize(t.Schema))));

        List<ChatMessage> messages = [new SystemChatMessage(system)];
        foreach (var turn in history)
            messages.Add(turn.FromUser ? new UserChatMessage(turn.Text) : new AssistantChatMessage(turn.Text));
        messages.Add(new UserChatMessage(user));

        long inTok = 0, outTok = 0;
        var finalText = "";
        for (var turn = 0; turn < maxTurns; turn++)
        {
            ChatCompletion completion = await _client.CompleteChatAsync(messages, options, ct);
            inTok += completion.Usage?.InputTokenCount ?? 0;
            outTok += completion.Usage?.OutputTokenCount ?? 0;
            if (completion.FinishReason == ChatFinishReason.ToolCalls && completion.ToolCalls.Count > 0)
            {
                messages.Add(new AssistantChatMessage(completion));
                foreach (var call in completion.ToolCalls)
                {
                    JsonElement input;
                    try { input = JsonSerializer.Deserialize<JsonElement>(call.FunctionArguments); }
                    catch (JsonException) { input = JsonSerializer.SerializeToElement(new { }); }
                    messages.Add(new ToolChatMessage(call.Id, await execute(call.FunctionName, input)));
                }
                continue;
            }
            if (completion.FinishReason == ChatFinishReason.ContentFilter) throw new InvalidOperationException("The model's content filter stopped the request.");
            if (completion.FinishReason == ChatFinishReason.Length) throw new InvalidOperationException("The model ran out of output tokens before finishing.");
            finalText = string.Join("", completion.Content.Select(c => c.Text)).Trim();
            break;
        }
        return new ModelResult { Text = finalText, InputTokens = inTok, OutputTokens = outTok };
    }
}
