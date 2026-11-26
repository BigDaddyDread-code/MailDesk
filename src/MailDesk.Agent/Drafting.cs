using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using MailDesk.Core.Security;
using MailDesk.Core.Storage;
using OpenAI.Chat;

namespace MailDesk.Agent;

/// <summary>What the user wants written. The original text is the quoted message when replying, and it is sent to the model.</summary>
public sealed class DraftRequest
{
    /// <summary>The user's own words about what the email should say. May be empty when replying; then the model writes a sensible reply.</summary>
    public string Instructions { get; init; } = "";
    public string Subject { get; init; } = "";
    public string To { get; init; } = "";
    /// <summary>The message being replied to or forwarded, already quoted, or whatever is in the body so far.</summary>
    public string Context { get; init; } = "";
    public string SenderName { get; init; } = "";
}

/// <summary>Writes an email body from a request. It only returns text; the user edits and sends, so nothing here can send mail.</summary>
public interface IDraftingService
{
    string Name { get; }
    Task<string> DraftAsync(DraftRequest request, CancellationToken ct = default);
}

public static class DraftPrompts
{
    public static string System(string senderName) => $"""
        You draft emails on behalf of {senderName}, who will read and edit the draft before sending. Write only the body of
        the email as plain text: no subject line, no preamble, no commentary, no markdown. Match the language and formality
        of the message being answered. Be warm, specific and brief; say what is needed and stop. When replying, address the
        points the other person raised. If something is unknown (a date, a figure, a decision), write it as [square brackets]
        for {senderName} to fill in rather than inventing it. Sign off with {senderName.Split(' ')[0]} unless the instructions say otherwise.
        """;

    public static string User(DraftRequest r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.To)) parts.Add($"To: {r.To}");
        if (!string.IsNullOrWhiteSpace(r.Subject)) parts.Add($"Subject: {r.Subject}");
        parts.Add("");
        parts.Add(string.IsNullOrWhiteSpace(r.Instructions)
            ? "Write an appropriate reply to the message below."
            : "What the email should say: " + r.Instructions.Trim());
        if (!string.IsNullOrWhiteSpace(r.Context))
        {
            parts.Add("");
            parts.Add("The message being answered, and anything already written, follows between the markers.");
            parts.Add("<<<");
            parts.Add(r.Context.Trim());
            parts.Add(">>>");
        }
        return string.Join("\n", parts);
    }
}

public sealed class ClaudeDrafter : IDraftingService
{
    private readonly IMailStore _store;
    private readonly ICredentialProtector _protector;

    public ClaudeDrafter(IMailStore store, ICredentialProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    public string Name => "Claude";

    public async Task<string> DraftAsync(DraftRequest request, CancellationToken ct = default)
    {
        var settings = await AgentSettings.LoadAsync(_store, ct);
        var apiKey = await AgentSettings.LoadApiKeyAsync(_store, _protector, AgentSettings.KeyAnthropicApiKey, "ANTHROPIC_API_KEY", ct)
            ?? throw new InvalidOperationException("No Anthropic API key is set. Open AI settings and add one.");
        var client = new AnthropicClient { ApiKey = apiKey };
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = settings.ClaudeModel,
            MaxTokens = 4000,
            System = DraftPrompts.System(request.SenderName),
            Messages = [new BetaUserMessageParam { Content = DraftPrompts.User(request) }],
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
        }, cancellationToken: ct);
        if (response.StopReason == "refusal")
            throw new InvalidOperationException("Claude declined to write this: " + (response.StopDetails?.Explanation ?? "safety classifier"));
        var text = string.Join("", response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Claude returned no text.");
        return text.Trim();
    }
}

public sealed class OpenAIDrafter : IDraftingService
{
    private readonly IMailStore _store;
    private readonly ICredentialProtector _protector;

    public OpenAIDrafter(IMailStore store, ICredentialProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    public string Name => "OpenAI";

    public async Task<string> DraftAsync(DraftRequest request, CancellationToken ct = default)
    {
        var settings = await AgentSettings.LoadAsync(_store, ct);
        var apiKey = await AgentSettings.LoadApiKeyAsync(_store, _protector, AgentSettings.KeyOpenAIApiKey, "OPENAI_API_KEY", ct)
            ?? throw new InvalidOperationException("No OpenAI API key is set. Open AI settings and add one.");
        var client = new ChatClient(settings.OpenAIModel, apiKey);
        ChatCompletion completion = await client.CompleteChatAsync(
            [new SystemChatMessage(DraftPrompts.System(request.SenderName)), new UserChatMessage(DraftPrompts.User(request))],
            cancellationToken: ct);
        if (completion.FinishReason == ChatFinishReason.ContentFilter)
            throw new InvalidOperationException("The model's content filter stopped the draft.");
        var text = string.Join("", completion.Content.Select(c => c.Text));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The model returned no text.");
        return text.Trim();
    }
}

/// <summary>Routes to the provider chosen in AI settings. The local heuristic agent cannot write prose, so it asks for a provider.</summary>
public sealed class SwitchingDrafter : IDraftingService
{
    private readonly IMailStore _store;
    private readonly ClaudeDrafter _claude;
    private readonly OpenAIDrafter _openAi;

    public SwitchingDrafter(IMailStore store, ClaudeDrafter claude, OpenAIDrafter openAi)
    {
        _store = store;
        _claude = claude;
        _openAi = openAi;
    }

    public string Name => "AI";

    public async Task<string> DraftAsync(DraftRequest request, CancellationToken ct = default)
    {
        var settings = await AgentSettings.LoadAsync(_store, ct);
        IDraftingService drafter = settings.Provider switch
        {
            AgentProvider.Claude => _claude,
            AgentProvider.OpenAI => _openAi,
            _ => throw new InvalidOperationException("Drafting needs a model. Open AI settings (the gear icon) and choose Claude or OpenAI."),
        };
        return await drafter.DraftAsync(request, ct);
    }
}
