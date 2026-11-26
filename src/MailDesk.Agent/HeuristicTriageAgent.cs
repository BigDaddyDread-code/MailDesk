using System.Text.RegularExpressions;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.Agent;

/// <summary>
/// Rule-based triage that needs no API key. It groups a folder's mail into buckets by sender and subject
/// patterns and emits one proposal per bucket, plus rule proposals for repeat senders so the same mail
/// never reaches the Inbox again. Deliberately conservative: it only proposes, never acts.
/// </summary>
public sealed class HeuristicTriageAgent : IAgentService
{
    private readonly IMailStore _store;

    public HeuristicTriageAgent(IMailStore store) => _store = store;

    public string Name => "Local rules";

    /// <summary>A sender needs this many messages in a bucket before a permanent rule is proposed.</summary>
    public int RuleThreshold { get; init; } = 3;

    private static readonly string[] JobAlertMarkers = ["jobalerts", "jobrapido", "seek.co", "ziprecruiter", "jobhop", "indeed", "glassdoor", "linkedin.com"];
    private static readonly string[] PromoMarkers = ["noreply@", "no-reply@", "newsletter", "marketing@", "promotions@", "deals", "offers", "edm.", "mailer@", "email@my.", "info@send."];
    private static readonly string[] PromoSubjectMarkers = ["% off", "sale", "black friday", "deal", "offer", "last chance", "don't miss", "free shipping", "🔥", "🎁"];
    private static readonly string[] CodeSubjectMarkers = ["verification code", "one-time", "is your code", "secure link", "verify your email", "password reset", "security alert"];
    private static readonly Regex Homoglyph = new(@"[^\u0000-\u007F]", RegexOptions.Compiled);
    private static readonly string[] Brands = ["google", "microsoft", "apple", "icloud", "netflix", "paypal", "amazon", "mcafee", "norton", "bank", "bnz", "anz", "asb", "nzta", "spotify", "disney"];

    public async Task<IReadOnlyList<AgentProposal>> ProposeTriageAsync(long accountId, long folderId, CancellationToken ct = default)
    {
        var messages = await _store.GetMessagesAsync(folderId, limit: 10_000, ct: ct);
        var scam = new List<MessageSummary>();
        var codes = new List<MessageSummary>();
        var jobs = new List<MessageSummary>();
        var promos = new List<MessageSummary>();

        foreach (var m in messages)
        {
            var from = m.FromAddress.ToLowerInvariant();
            var name = m.FromName.ToLowerInvariant();
            var subject = m.Subject.ToLowerInvariant();
            var domain = from.Contains('@') ? from[(from.IndexOf('@') + 1)..] : from;

            if (LooksLikeScam(name, domain, subject)) { scam.Add(m); continue; }
            if (CodeSubjectMarkers.Any(subject.Contains)) { codes.Add(m); continue; }
            if (JobAlertMarkers.Any(from.Contains) && !m.IsFlagged) { jobs.Add(m); continue; }
            if (PromoMarkers.Any(from.Contains) && PromoSubjectMarkers.Any(subject.Contains)) { promos.Add(m); continue; }
        }

        var proposals = new List<AgentProposal>();
        void AddMove(string title, string why, List<MessageSummary> list, string target)
        {
            if (list.Count == 0) return;
            proposals.Add(new AgentProposal
            {
                Kind = ProposalKind.MoveMessages, Title = title, Rationale = why, AccountId = accountId, SourceFolderId = folderId,
                MessageIds = list.Select(m => m.Id).ToList(), TargetFolderName = target,
            });
        }

        AddMove($"Move {scam.Count} likely scam or phishing emails to Junk",
            "Sender display name imitates a known brand but the domain does not match, or the name uses lookalike characters.", scam, "Junk");
        AddMove($"Move {codes.Count} expired one-time codes and login links to Trash",
            "Verification codes and sign-in links are useless once used.", codes, "Trash");
        AddMove($"Move {jobs.Count} job-board alerts to Trash",
            "Automated job alerts from LinkedIn, SEEK and similar. The listings remain on those sites.", jobs, "Trash");
        AddMove($"Move {promos.Count} promotional emails to Trash",
            "Bulk marketing from no-reply senders with sale wording in the subject.", promos, "Trash");

        // Permanent rules for repeat senders in the automated buckets. Scam and codes are left out:
        // scam domains are throwaway, and codes are matched on subject, which is too broad for a standing rule.
        AddRuleProposal(proposals, accountId, folderId, jobs, "job-board alert");
        AddRuleProposal(proposals, accountId, folderId, promos, "promotional");
        return proposals;
    }

    private void AddRuleProposal(List<AgentProposal> proposals, long accountId, long folderId, List<MessageSummary> bucket, string label)
    {
        var repeat = bucket.GroupBy(m => m.FromAddress.ToLowerInvariant())
            .Where(g => g.Key.Length > 0 && g.Count() >= RuleThreshold)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (repeat.Count == 0) return;

        var rules = repeat.Select(g => new MailRule
        {
            AccountId = accountId,
            Name = $"Trash {label}s from {g.First().FromDisplay}",
            Conditions = [new RuleCondition(RuleField.FromAddress, RuleOperator.Equals, g.Key)],
            Action = RuleAction.MoveToTrash,
        }).ToList();

        proposals.Add(new AgentProposal
        {
            Kind = ProposalKind.CreateRule,
            Title = $"Create {rules.Count} rule{(rules.Count == 1 ? "" : "s")}: always trash {label} mail from repeat senders",
            Rationale = "Senders: " + string.Join(", ", repeat.Select(g => $"{g.First().FromDisplay} ({g.Count()})")) + ". Future mail from them skips the Inbox.",
            AccountId = accountId, SourceFolderId = folderId, Rules = rules,
        });
    }

    private static bool LooksLikeScam(string displayName, string domain, string subject)
    {
        if (Homoglyph.IsMatch(displayName) && Brands.Any(b => displayName.Replace(" ", "").Contains(b))) return true;
        foreach (var brand in Brands)
        {
            if (displayName.Contains(brand) && !domain.Contains(brand))
            {
                // Brand in the display name, brand absent from the domain, and an urgency hook in the subject.
                if (subject.Contains("urgent") || subject.Contains("suspend") || subject.Contains("expire") || subject.Contains("verify") || subject.Contains("renew") || subject.Contains("storage"))
                    return true;
            }
        }
        return false;
    }
}
