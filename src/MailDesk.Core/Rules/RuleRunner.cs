using MailDesk.Core.Models;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;

namespace MailDesk.Core.Rules;

public sealed record RuleRunResult(int Matched, int Moved, int Flagged, int MarkedRead, IReadOnlyList<string> Errors)
{
    public static readonly RuleRunResult Empty = new(0, 0, 0, 0, []);

    public string Summary()
    {
        if (Matched == 0) return "No rules matched.";
        var parts = new List<string>();
        if (Moved > 0) parts.Add($"{Moved} moved");
        if (Flagged > 0) parts.Add($"{Flagged} flagged");
        if (MarkedRead > 0) parts.Add($"{MarkedRead} marked read");
        var s = $"Rules: {string.Join(", ", parts)}.";
        return Errors.Count == 0 ? s : $"{s} {Errors.Count} error(s): {Errors[0]}";
    }
}

/// <summary>
/// Applies rules to a set of cached messages through the sync layer. Each message gets at most one rule
/// (the first match). Moves are batched per target folder so a thousand matches is one IMAP MOVE per folder.
/// </summary>
public sealed class RuleRunner
{
    private readonly IMailStore _store;
    private readonly IMailSync _sync;

    public RuleRunner(IMailStore store, IMailSync sync)
    {
        _store = store;
        _sync = sync;
    }

    public async Task<RuleRunResult> RunAsync(Account account, IReadOnlyList<MessageSummary> candidates,
        IReadOnlyList<MailRule>? rulesOverride = null, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return RuleRunResult.Empty;
        var rules = rulesOverride ?? await _store.GetRulesAsync(account.Id, ct);
        if (rules.Count == 0) return RuleRunResult.Empty;

        var hits = new Dictionary<MailRule, List<MessageSummary>>();
        foreach (var m in candidates)
        {
            var rule = RuleEngine.FirstMatch(rules, m);
            if (rule is null) continue;
            if (!hits.TryGetValue(rule, out var list)) hits[rule] = list = [];
            list.Add(m);
        }
        if (hits.Count == 0) return RuleRunResult.Empty;

        var folders = (await _store.GetFoldersAsync(account.Id, ct)).ToList();
        int matched = 0, moved = 0, flagged = 0, markedRead = 0;
        var errors = new List<string>();

        foreach (var (rule, messages) in hits)
        {
            matched += messages.Count;
            try
            {
                switch (rule.Action)
                {
                    case RuleAction.MoveToFolder:
                    case RuleAction.MoveToTrash:
                    {
                        var target = rule.Action == RuleAction.MoveToTrash
                            ? folders.FirstOrDefault(f => f.Kind == FolderKind.Trash)
                            : await ResolveOrCreateAsync(account, folders, rule.TargetFolder, ct);
                        if (target is null) { errors.Add($"{rule.Name}: no target folder."); break; }
                        var toMove = messages.Where(m => m.FolderId != target.Id).ToList();
                        if (toMove.Count == 0) break;
                        await _sync.MoveAsync(account, toMove, target, ct);
                        moved += toMove.Count;
                        break;
                    }
                    case RuleAction.MarkRead:
                        foreach (var m in messages.Where(m => !m.IsRead))
                        {
                            await _sync.SetFlagsAsync(account, m, isRead: true, ct: ct);
                            markedRead++;
                        }
                        break;
                    case RuleAction.Flag:
                        foreach (var m in messages.Where(m => !m.IsFlagged))
                        {
                            await _sync.SetFlagsAsync(account, m, isFlagged: true, ct: ct);
                            flagged++;
                        }
                        break;
                }
                if (rule.Id != 0) await _store.IncrementRuleHitsAsync(rule.Id, messages.Count, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"{rule.Name}: {ex.Message}");
            }
        }
        return new RuleRunResult(matched, moved, flagged, markedRead, errors);
    }

    private async Task<MailFolder?> ResolveOrCreateAsync(Account account, List<MailFolder> folders, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var found = folders.FirstOrDefault(f => string.Equals(f.FullName, name, StringComparison.OrdinalIgnoreCase))
            ?? folders.FirstOrDefault(f => string.Equals(f.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        if (found is not null) return found;
        var created = await _sync.CreateFolderAsync(account, name, ct);
        folders.Add(created);
        return created;
    }
}
