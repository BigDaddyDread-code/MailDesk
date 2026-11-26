using MailDesk.Core.Models;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;

namespace MailDesk.Agent;

/// <summary>Executes an approved proposal through the normal sync layer. The only path from proposal to mailbox.</summary>
public sealed class ProposalApplier
{
    private readonly IMailStore _store;
    private readonly IMailSync _sync;

    public ProposalApplier(IMailStore store, IMailSync sync)
    {
        _store = store;
        _sync = sync;
    }

    public async Task ApplyAsync(AgentProposal proposal, CancellationToken ct = default)
    {
        if (proposal.Status != ProposalStatus.Approved)
            throw new InvalidOperationException("Only approved proposals can be applied.");

        try
        {
            var account = (await _store.GetAccountsAsync(ct)).FirstOrDefault(a => a.Id == proposal.AccountId)
                ?? throw new InvalidOperationException("Account not found.");

            switch (proposal.Kind)
            {
                case ProposalKind.MoveMessages:
                {
                    var target = await ResolveOrCreateFolderAsync(account, proposal.TargetFolderName!, ct);
                    var messages = new List<MessageSummary>();
                    foreach (var id in proposal.MessageIds)
                    {
                        var m = await _store.GetMessageAsync(id, ct);
                        if (m is not null) messages.Add(m);
                    }
                    await _sync.MoveAsync(account, messages, target, ct);
                    break;
                }
                case ProposalKind.CreateFolder:
                    await ResolveOrCreateFolderAsync(account, proposal.TargetFolderName!, ct);
                    break;
                case ProposalKind.MarkRead:
                case ProposalKind.Flag:
                    foreach (var id in proposal.MessageIds)
                    {
                        var m = await _store.GetMessageAsync(id, ct);
                        if (m is null) continue;
                        if (proposal.Kind == ProposalKind.MarkRead) await _sync.SetFlagsAsync(account, m, isRead: true, ct: ct);
                        else await _sync.SetFlagsAsync(account, m, isFlagged: true, ct: ct);
                    }
                    break;
                case ProposalKind.CreateRule:
                {
                    // Skip rules that already exist with identical conditions and action, so re-approving is harmless.
                    var existing = await _store.GetRulesAsync(account.Id, ct);
                    foreach (var rule in proposal.Rules)
                    {
                        rule.AccountId = account.Id;
                        var dup = existing.Any(e => e.Action == rule.Action && e.TargetFolder == rule.TargetFolder && e.Conditions.SequenceEqual(rule.Conditions));
                        if (!dup) await _store.UpsertRuleAsync(rule, ct);
                    }
                    break;
                }
            }
            proposal.Status = ProposalStatus.Applied;
        }
        catch (Exception ex)
        {
            proposal.Status = ProposalStatus.Failed;
            proposal.Error = ex.Message;
            throw;
        }
    }

    private async Task<MailFolder> ResolveOrCreateFolderAsync(Account account, string name, CancellationToken ct)
    {
        var folders = await _store.GetFoldersAsync(account.Id, ct);
        var kindMatch = name.ToLowerInvariant() switch
        {
            "trash" => FolderKind.Trash,
            "junk" => FolderKind.Junk,
            "archive" => FolderKind.Archive,
            _ => (FolderKind?)null,
        };
        var found = (kindMatch is not null ? folders.FirstOrDefault(f => f.Kind == kindMatch) : null)
            ?? folders.FirstOrDefault(f => string.Equals(f.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            ?? folders.FirstOrDefault(f => string.Equals(f.FullName, name, StringComparison.OrdinalIgnoreCase));
        return found ?? await _sync.CreateFolderAsync(account, name, ct);
    }
}
