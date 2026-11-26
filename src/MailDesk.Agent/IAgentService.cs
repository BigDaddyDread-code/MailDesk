namespace MailDesk.Agent;

/// <summary>
/// Produces proposals for a folder. Implementations: HeuristicTriageAgent (local rules, no network) today;
/// a Claude-backed agent using the Anthropic C# SDK tool runner is the planned next implementation. Both return
/// the same proposal type, so the review UI does not care which one produced them.
/// </summary>
public interface IAgentService
{
    string Name { get; }
    Task<IReadOnlyList<AgentProposal>> ProposeTriageAsync(long accountId, long folderId, CancellationToken ct = default);
}
