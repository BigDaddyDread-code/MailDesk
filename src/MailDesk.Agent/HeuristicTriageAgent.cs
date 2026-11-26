using System.Text.RegularExpressions;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.Agent;

/// <summary>
/// Rule-based triage that needs no API key. It groups a folder's mail into buckets by sender and subject
/// patterns and emits one proposal per bucket. Deliberately conservative: it only proposes, never acts.
/// </summary>
public sealed class HeuristicTriageAgent : IAgentService
{
    private readonly IMailStore _store;

    public HeuristicTriageAgent(IMailStore store) => _store = store;

    public string Name => "Local rules";

    private static readonly string[] JobAlertMarkers = ["jobalerts", "jobrapido", "seek.co", "ziprecruiter", "jobhop", "indeed", "glassdoor", "linkedin.com"];
    private static readonly string[] PromoMarkers = ["noreply@", "no-reply@", "newsletter", "marketing@", "promotions@", "deals", "offers", "edm.", "mailer@", "email@my.", "info@send."];
    private static readonly string[] PromoSubjectMarkers = ["% off", "sale", "black friday", "deal", "offer", "last chance", "don't miss", "free shipping", "🔥", "🎁"];
    private static readonly string[] CodeSubjectMarkers = ["verification code", "one-time", "is your code", "secure link", "verify your email", "password reset", "security alert"];
    private static readonly Regex Homoglyph = new(@"[^\u0000-\u007F]", RegexOptions.Compiled);
    private static readonly string[] Brands = ["google", "microsoft", "apple", "icloud", "netflix", "paypal", "amazon", "mcafee", "norton", "bank", "bnz", "anz", "asb", "nzta", "spotify", "disney"];

    public async Task<IReadOnlyList<AgentProposal>> ProposeTriageAsync(long accountId, long folderId, CancellationToken ct = default)
    {
        var messages = await _store.GetMessagesAsync(folderId, limit: 10_000, ct: ct);
        var scam = new List<long>();
        var codes = new List<long>();
        var jobs = new List<long>();
        var promos = new List<long>();

        foreach (var m in messages)
        {
            var from = m.FromAddress.ToLowerInvariant();
            var name = m.FromName.ToLowerInvariant();
            var subject = m.Subject.ToLowerInvariant();
            var domain = from.Contains('@') ? from[(from.IndexOf('@') + 1)..] : from;

            if (LooksLikeScam(name, domain, subject)) { scam.Add(m.Id); continue; }
            if (CodeSubjectMarkers.Any(subject.Contains)) { codes.Add(m.Id); continue; }
            if (JobAlertMarkers.Any(from.Contains) && !m.IsFlagged) { jobs.Add(m.Id); continue; }
            if (PromoMarkers.Any(from.Contains) && PromoSubjectMarkers.Any(subject.Contains)) { promos.Add(m.Id); continue; }
        }

        var proposals = new List<AgentProposal>();
        void Add(ProposalKind kind, string title, string why, List<long> ids, string? target)
        {
            if (ids.Count == 0) return;
            proposals.Add(new AgentProposal
            {
                Kind = kind, Title = title, Rationale = why, AccountId = accountId, SourceFolderId = folderId,
                MessageIds = ids, TargetFolderName = target,
            });
        }

        Add(ProposalKind.MoveMessages, $"Move {scam.Count} likely scam or phishing emails to Junk",
            "Sender display name imitates a known brand but the domain does not match, or the name uses lookalike characters.", scam, "Junk");
        Add(ProposalKind.MoveMessages, $"Move {codes.Count} expired one-time codes and login links to Trash",
            "Verification codes and sign-in links are useless once used.", codes, "Trash");
        Add(ProposalKind.MoveMessages, $"Move {jobs.Count} job-board alerts to Trash",
            "Automated job alerts from LinkedIn, SEEK and similar. The listings remain on those sites.", jobs, "Trash");
        Add(ProposalKind.MoveMessages, $"Move {promos.Count} promotional emails to Trash",
            "Bulk marketing from no-reply senders with sale wording in the subject.", promos, "Trash");
        return proposals;
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
