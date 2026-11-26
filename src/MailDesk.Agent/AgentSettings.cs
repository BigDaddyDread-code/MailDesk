using MailDesk.Core.Security;
using MailDesk.Core.Storage;

namespace MailDesk.Agent;

public enum AgentProvider
{
    Heuristic,
    Claude,
    OpenAI,
}

/// <summary>Which agent runs and how each provider is configured. Persisted in the settings table; keys are DPAPI-protected.</summary>
public sealed class AgentSettings
{
    public const string DefaultClaudeModel = "claude-opus-5-5";
    public const string DefaultOpenAIModel = "gpt-5";

    public const string KeyProvider = "agent.provider";
    public const string KeySharePreviews = "agent.share_previews";
    public const string KeyAnthropicApiKey = "anthropic.api_key";
    public const string KeyClaudeModel = "agent.model";
    public const string KeyOpenAIApiKey = "openai.api_key";
    public const string KeyOpenAIModel = "openai.model";

    public AgentProvider Provider { get; set; } = AgentProvider.Heuristic;
    /// <summary>When true, the first line of each message (the server preview text) is sent along with headers. Bodies are never sent.</summary>
    public bool SharePreviews { get; set; } = true;

    public bool HasAnthropicKey { get; set; }
    public string ClaudeModel { get; set; } = DefaultClaudeModel;
    public bool HasOpenAIKey { get; set; }
    public string OpenAIModel { get; set; } = DefaultOpenAIModel;

    public static async Task<AgentSettings> LoadAsync(IMailStore store, CancellationToken ct = default)
    {
        return new AgentSettings
        {
            Provider = (await store.GetSettingAsync(KeyProvider, ct)) switch
            {
                "claude" => AgentProvider.Claude,
                "openai" => AgentProvider.OpenAI,
                _ => AgentProvider.Heuristic,
            },
            SharePreviews = await store.GetSettingAsync(KeySharePreviews, ct) != "false",
            HasAnthropicKey = !string.IsNullOrEmpty(await store.GetSettingAsync(KeyAnthropicApiKey, ct)),
            ClaudeModel = await store.GetSettingAsync(KeyClaudeModel, ct) is { Length: > 0 } cm ? cm : DefaultClaudeModel,
            HasOpenAIKey = !string.IsNullOrEmpty(await store.GetSettingAsync(KeyOpenAIApiKey, ct)),
            OpenAIModel = await store.GetSettingAsync(KeyOpenAIModel, ct) is { Length: > 0 } om ? om : DefaultOpenAIModel,
        };
    }

    public static async Task SaveAsync(IMailStore store, AgentSettings s, CancellationToken ct = default)
    {
        await store.SetSettingAsync(KeyProvider, s.Provider switch { AgentProvider.Claude => "claude", AgentProvider.OpenAI => "openai", _ => "heuristic" }, ct);
        await store.SetSettingAsync(KeySharePreviews, s.SharePreviews ? "true" : "false", ct);
        await store.SetSettingAsync(KeyClaudeModel, string.IsNullOrWhiteSpace(s.ClaudeModel) ? DefaultClaudeModel : s.ClaudeModel.Trim(), ct);
        await store.SetSettingAsync(KeyOpenAIModel, string.IsNullOrWhiteSpace(s.OpenAIModel) ? DefaultOpenAIModel : s.OpenAIModel.Trim(), ct);
    }

    /// <summary>Stores a key protected under the given settings key. Pass null or empty to clear it.</summary>
    public static Task SaveApiKeyAsync(IMailStore store, ICredentialProtector protector, string settingKey, string? apiKey, CancellationToken ct = default)
        => store.SetSettingAsync(settingKey, string.IsNullOrWhiteSpace(apiKey) ? null : protector.Protect(apiKey.Trim()), ct);

    /// <summary>Saved key first, then the environment variable, else null.</summary>
    public static async Task<string?> LoadApiKeyAsync(IMailStore store, ICredentialProtector protector, string settingKey, string envVar, CancellationToken ct = default)
    {
        var stored = await store.GetSettingAsync(settingKey, ct);
        if (!string.IsNullOrEmpty(stored)) return protector.Unprotect(stored);
        var env = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
