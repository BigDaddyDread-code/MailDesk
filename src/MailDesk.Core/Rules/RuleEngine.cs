using System.Text.RegularExpressions;
using MailDesk.Core.Models;

namespace MailDesk.Core.Rules;

/// <summary>Pure matching logic. No I/O, so it is trivially testable.</summary>
public static class RuleEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The first enabled rule whose conditions all match, in SortOrder then Id order; null if none.</summary>
    public static MailRule? FirstMatch(IEnumerable<MailRule> rules, MessageSummary message)
        => rules.Where(r => r.Enabled).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).FirstOrDefault(r => Matches(r, message));

    /// <summary>True when every condition matches. A rule with no conditions never matches, so a half-built rule cannot move mail.</summary>
    public static bool Matches(MailRule rule, MessageSummary message)
        => rule.Conditions.Count > 0 && rule.Conditions.All(c => ConditionMatches(c, message));

    public static bool ConditionMatches(RuleCondition condition, MessageSummary message)
    {
        var input = FieldValue(condition.Field, message);
        var value = condition.Value ?? "";
        if (value.Length == 0) return false;
        var cmp = StringComparison.OrdinalIgnoreCase;
        return condition.Operator switch
        {
            RuleOperator.Contains => input.Contains(value, cmp),
            RuleOperator.Equals => input.Equals(value, cmp),
            RuleOperator.StartsWith => input.StartsWith(value, cmp),
            RuleOperator.EndsWith => input.EndsWith(value, cmp),
            RuleOperator.Regex => SafeRegex(input, value),
            _ => false,
        };
    }

    public static string FieldValue(RuleField field, MessageSummary m) => field switch
    {
        RuleField.FromAddress => m.FromAddress,
        RuleField.FromName => m.FromName,
        RuleField.SenderDomain => Domain(m.FromAddress),
        RuleField.Subject => m.Subject,
        RuleField.ToAddresses => m.ToAddresses,
        _ => "",
    };

    public static string Domain(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? "" : address[(at + 1)..];
    }

    private static bool SafeRegex(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Returns null when the regex compiles, otherwise the parser's message. For the editor.</summary>
    public static string? ValidateRegex(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }
}
