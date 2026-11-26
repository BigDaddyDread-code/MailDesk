using MailDesk.Core.Storage;

namespace MailDesk.Agent;

/// <summary>Picks the configured agent at call time, so changing the setting takes effect without a restart.</summary>
public sealed class SwitchingAgent : IAgentService
{
    private readonly IMailStore _store;
    private readonly HeuristicTriageAgent _heuristic;
    private readonly ClaudeTriageAgent _claude;
    private string _name = "Local rules";

    public SwitchingAgent(IMailStore store, HeuristicTriageAgent heuristic, ClaudeTriageAgent claude)
    {
        _store = store;
        _heuristic = heuristic;
        _claude = claude;
    }

    public string Name => _name;
    public string? LastRunNote => _lastNote;
    private string? _lastNote;

    public async Task<IReadOnlyList<AgentProposal>> ProposeTriageAsync(long accountId, long folderId, CancellationToken ct = default)
    {
        var settings = await AgentSettings.LoadAsync(_store, ct);
        IAgentService agent = settings.UseClaude ? _claude : _heuristic;
        _name = agent.Name;
        var result = await agent.ProposeTriageAsync(accountId, folderId, ct);
        _name = agent.Name;
        _lastNote = agent.LastRunNote;
        return result;
    }
}
