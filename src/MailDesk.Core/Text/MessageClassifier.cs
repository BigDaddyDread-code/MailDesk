using System.Text.RegularExpressions;
using MailDesk.Core.Models;

namespace MailDesk.Core.Text;

/// <summary>
/// Local, deterministic classification of a message from its headers: no network, no model. Decides whether a message
/// is correspondence from a person (Primary), a mailing-list or marketing send (Newsletter) or machine-generated
/// notification (Notification), and normalises subjects so replies can be matched to what they answer.
/// </summary>
public static partial class MessageClassifier
{
    /// <summary>Headers that matter for classification. Any may be null when the server did not return it.</summary>
    public sealed record Headers(string? ListId, string? ListUnsubscribe, string? Precedence, string? AutoSubmitted);

    public static readonly Headers NoHeaders = new(null, null, null, null);

    [GeneratedRegex(@"^(?:no-?reply|do-?not-?reply|donotreply|noreply[-_.]?\w*|notifications?|notify|alerts?|alert[-_.]\w+|mailer-daemon|postmaster|bounces?|bounce[-_.]\w+|auto(?:mated|mailer|-?reply)?|system|robot|updates?|digest|news(?:letter)?|marketing|promotions?|offers?|hello|info|support|billing|invoices?|receipts?|orders?|accounts?|security|team|service)(?:[-_.+].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachineLocalPart();

    [GeneratedRegex(@"^(?:no-?reply|do-?not-?reply|donotreply|noreply[-_.]?\w*|notifications?|notify|alerts?|alert[-_.]\w+|mailer-daemon|postmaster|bounces?|bounce[-_.]\w+|auto(?:mated|mailer|-?reply)?|system|robot|security|verification|verify|otp|2fa)(?:[-_.+].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotificationLocalPart();

    [GeneratedRegex(@"^\s*(?:(?:re|fw|fwd|aw|wg|tr|sv|vs|antw)\s*(?:\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPrefixes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static MessageCategory Classify(string fromAddress, string subject, Headers headers)
    {
        var local = LocalPart(fromAddress);

        // Explicitly automatic mail is a notification whatever else it carries.
        if (headers.AutoSubmitted is { } auto && !auto.Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase)) return MessageCategory.Notification;
        if (local is "mailer-daemon" or "postmaster") return MessageCategory.Notification;

        // List headers mark bulk sends. A system sender with them is still a notification (build alerts, sign-in codes);
        // anything else with them is a newsletter or marketing.
        var bulk = !string.IsNullOrWhiteSpace(headers.ListUnsubscribe) || !string.IsNullOrWhiteSpace(headers.ListId)
                   || (headers.Precedence?.Trim().ToLowerInvariant() is "bulk" or "list" or "junk");
        if (bulk) return NotificationLocalPart().IsMatch(local) ? MessageCategory.Notification : MessageCategory.Newsletter;

        if (NotificationLocalPart().IsMatch(local)) return MessageCategory.Notification;
        if (MachineLocalPart().IsMatch(local) && LooksTransactional(subject)) return MessageCategory.Notification;
        return MessageCategory.Primary;
    }

    /// <summary>Subject with every Re:/Fwd: style prefix removed, whitespace collapsed, lower-cased: the key a reply shares with its original.</summary>
    public static string SubjectKey(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";
        var s = ReplyPrefixes().Replace(subject, "");
        return Whitespace().Replace(s, " ").Trim().ToLowerInvariant();
    }

    /// <summary>True when the subject is a Re:/Fwd: of something else.</summary>
    public static bool IsReplyOrForward(string? subject) => !string.IsNullOrWhiteSpace(subject) && ReplyPrefixes().IsMatch(subject);

    private static bool LooksTransactional(string subject)
    {
        var s = subject.ToLowerInvariant();
        return s.Contains("your order") || s.Contains("receipt") || s.Contains("invoice") || s.Contains("statement") || s.Contains("confirm")
               || s.Contains("verification") || s.Contains("verify") || s.Contains("password") || s.Contains("sign-in") || s.Contains("sign in")
               || s.Contains("login") || s.Contains("your code") || s.Contains("verification code") || s.Contains("security code") || s.Contains("one-time") || s.Contains("shipped") || s.Contains("delivery") || s.Contains("payment")
               || s.Contains("reminder") || s.Contains("notification") || s.Contains("alert") || s.Contains("booking") || s.Contains("ticket");
    }

    private static string LocalPart(string address)
    {
        var at = address.IndexOf('@');
        return (at > 0 ? address[..at] : address).Trim().ToLowerInvariant();
    }
}
