using MailDesk.Core.Security;
using MailDesk.Core.Storage;

namespace MailDesk.Agent;

/// <summary>Which agent runs and how Claude is configured. Persisted in the settings table; the key is DPAPI-protected.</summary>
public sealed class AgentSettings
{
    public const string DefaultModel = "claude-opus-5-5";

    public const string KeyProvider = "agent.provider";
    public const string KeyApiKey = "anthropic.api_key";
    public const string KeyModel = "agent.model";
    public const string KeySharePreviews = "agent.share_previews";

    public bool UseClaude { get; set; }
    public bool HasApiKey { get; set; }
    public string Model { get; set; } = DefaultModel;
    /// <summary>When true, the first line of each message (the server preview text) is sent along with headers. Bodies are never sent.</summary>
    public bool SharePreviews { get; set; } = true;

    public static async Task<AgentSettings> LoadAsync(IMailStore store, CancellationToken ct = default)
    {
        var s = new AgentSettings
        {
            UseClaude = await store.GetSettingAsync(KeyProvider, ct) == "claude",
            HasApiKey = !string.IsNullOrEmpty(await store.GetSettingAsync(KeyApiKey, ct)),
            Model = await store.GetSettingAsync(KeyModel, ct) is { Length: > 0 } m ? m : DefaultModel,
            SharePreviews = await store.GetSettingAsync(KeySharePreviews, ct) != "false",
        };
        return s;
    }

    public static async Task SaveAsync(IMailStore store, AgentSettings s, CancellationToken ct = default)
    {
        await store.SetSettingAsync(KeyProvider, s.UseClaude ? "claude" : "heuristic", ct);
        await store.SetSettingAsync(KeyModel, string.IsNullOrWhiteSpace(s.Model) ? DefaultModel : s.Model.Trim(), ct);
        await store.SetSettingAsync(KeySharePreviews, s.SharePreviews ? "true" : "false", ct);
    }

    /// <summary>Stores the key protected. Pass null or empty to clear it.</summary>
    public static Task SaveApiKeyAsync(IMailStore store, ICredentialProtector protector, string? apiKey, CancellationToken ct = default)
        => store.SetSettingAsync(KeyApiKey, string.IsNullOrWhiteSpace(apiKey) ? null : protector.Protect(apiKey.Trim()), ct);

    public static async Task<string?> LoadApiKeyAsync(IMailStore store, ICredentialProtector protector, CancellationToken ct = default)
    {
        var stored = await store.GetSettingAsync(KeyApiKey, ct);
        if (!string.IsNullOrEmpty(stored)) return protector.Unprotect(stored);
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
