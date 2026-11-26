namespace MailDesk.Agent;

/// <summary>
/// Produces proposals for a folder. Implementations: HeuristicTriageAgent (local rules, no network) and
/// ClaudeTriageAgent (Anthropic API with read-only tools). SwitchingAgent picks one from settings. All return
/// the same proposal type, so the review UI does not care which one produced them.
/// </summary>
public interface IAgentService
{
    string Name { get; }
    Task<IReadOnlyList<AgentProposal>> ProposeTriageAsync(long accountId, long folderId, CancellationToken ct = default);

    /// <summary>Optional note about the last run for the user: the model's summary, token usage. Null when there is nothing to say.</summary>
    string? LastRunNote => null;
}
