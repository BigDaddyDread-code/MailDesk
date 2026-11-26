namespace MailDesk.Core.Models;

public enum RuleField
{
    FromAddress,
    FromName,
    SenderDomain,
    Subject,
    ToAddresses,
}

public enum RuleOperator
{
    Contains,
    Equals,
    StartsWith,
    EndsWith,
    Regex,
}

public enum RuleAction
{
    MoveToFolder,
    MoveToTrash,
    MarkRead,
    Flag,
}

/// <summary>One test on one header field. Matching is case-insensitive.</summary>
public sealed record RuleCondition(RuleField Field, RuleOperator Operator, string Value);

/// <summary>
/// A filter the user owns. All conditions must match (AND); the first matching rule by SortOrder wins.
/// Rules run on new Inbox mail after every sync, and on demand over an existing folder.
/// </summary>
public sealed class MailRule
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
    public List<RuleCondition> Conditions { get; set; } = [];
    public RuleAction Action { get; set; } = RuleAction.MoveToFolder;
    /// <summary>Server full name of the target folder for MoveToFolder. Created on first use if missing.</summary>
    public string? TargetFolder { get; set; }
    public long CreatedUtcMs { get; set; }
    public int HitCount { get; set; }

    public string Describe()
    {
        var when = Conditions.Count == 0 ? "(no conditions)" : string.Join(" and ", Conditions.Select(c => $"{Label(c.Field)} {Label(c.Operator)} \"{c.Value}\""));
        var then = Action switch
        {
            RuleAction.MoveToFolder => $"move to {TargetFolder ?? "?"}",
            RuleAction.MoveToTrash => "move to Trash",
            RuleAction.MarkRead => "mark as read",
            RuleAction.Flag => "flag",
            _ => Action.ToString(),
        };
        return $"If {when}, then {then}.";
    }

    public static string Label(RuleField f) => f switch
    {
        RuleField.FromAddress => "sender address",
        RuleField.FromName => "sender name",
        RuleField.SenderDomain => "sender domain",
        RuleField.Subject => "subject",
        RuleField.ToAddresses => "recipients",
        _ => f.ToString(),
    };

    public static string Label(RuleOperator o) => o switch
    {
        RuleOperator.Contains => "contains",
        RuleOperator.Equals => "is",
        RuleOperator.StartsWith => "starts with",
        RuleOperator.EndsWith => "ends with",
        RuleOperator.Regex => "matches regex",
        _ => o.ToString(),
    };

    public static string Label(RuleAction a) => a switch
    {
        RuleAction.MoveToFolder => "Move to folder",
        RuleAction.MoveToTrash => "Move to Trash",
        RuleAction.MarkRead => "Mark as read",
        RuleAction.Flag => "Flag",
        _ => a.ToString(),
    };
}
