using MailDesk.Core.Models;
using MailDesk.Core.Rules;
using MailDesk.Core.Storage;

namespace MailDesk.Tests;

public sealed class RuleEngineTests
{
    private static MessageSummary Msg(string from = "news@shop.example", string name = "Shop", string subject = "Big sale", string to = "me@example.test")
        => new() { FromAddress = from, FromName = name, Subject = subject, ToAddresses = to };

    private static MailRule Rule(RuleField f, RuleOperator op, string value, RuleAction action = RuleAction.MoveToTrash, bool enabled = true, int order = 0, long id = 1)
        => new() { Id = id, Name = "r", Enabled = enabled, SortOrder = order, Conditions = [new RuleCondition(f, op, value)], Action = action };

    [Theory]
    [InlineData(RuleField.FromAddress, RuleOperator.Contains, "SHOP.EXAMPLE", true)]
    [InlineData(RuleField.FromAddress, RuleOperator.Equals, "news@shop.example", true)]
    [InlineData(RuleField.FromAddress, RuleOperator.Equals, "news@shop", false)]
    [InlineData(RuleField.FromAddress, RuleOperator.StartsWith, "news@", true)]
    [InlineData(RuleField.FromAddress, RuleOperator.EndsWith, ".example", true)]
    [InlineData(RuleField.SenderDomain, RuleOperator.Equals, "shop.example", true)]
    [InlineData(RuleField.SenderDomain, RuleOperator.Equals, "example", false)]
    [InlineData(RuleField.FromName, RuleOperator.Contains, "sho", true)]
    [InlineData(RuleField.Subject, RuleOperator.Regex, @"^big\s+sale$", true)]
    [InlineData(RuleField.Subject, RuleOperator.Regex, @"[invalid", false)]
    [InlineData(RuleField.ToAddresses, RuleOperator.Contains, "me@", true)]
    public void Condition_operators_behave(RuleField field, RuleOperator op, string value, bool expected)
    {
        Assert.Equal(expected, RuleEngine.ConditionMatches(new RuleCondition(field, op, value), Msg()));
    }

    [Fact]
    public void All_conditions_must_match_and_empty_rule_never_matches()
    {
        var both = new MailRule { Conditions = [new(RuleField.FromName, RuleOperator.Equals, "Shop"), new(RuleField.Subject, RuleOperator.Contains, "sale")] };
        Assert.True(RuleEngine.Matches(both, Msg()));
        Assert.False(RuleEngine.Matches(both, Msg(subject: "Receipt")));
        Assert.False(RuleEngine.Matches(new MailRule(), Msg()));
        Assert.False(RuleEngine.ConditionMatches(new RuleCondition(RuleField.Subject, RuleOperator.Contains, ""), Msg()));
    }

    [Fact]
    public void First_match_respects_order_and_enabled()
    {
        var later = Rule(RuleField.Subject, RuleOperator.Contains, "sale", RuleAction.Flag, order: 2, id: 1);
        var earlier = Rule(RuleField.FromName, RuleOperator.Equals, "Shop", RuleAction.MoveToTrash, order: 1, id: 2);
        var disabled = Rule(RuleField.FromName, RuleOperator.Equals, "Shop", RuleAction.MarkRead, enabled: false, order: 0, id: 3);

        var hit = RuleEngine.FirstMatch([later, earlier, disabled], Msg());
        Assert.Same(earlier, hit);
        Assert.Null(RuleEngine.FirstMatch([later, earlier], Msg(name: "Other", subject: "Receipt")));
    }

    [Fact]
    public void Describe_reads_like_a_sentence()
    {
        var r = new MailRule { Conditions = [new(RuleField.SenderDomain, RuleOperator.Equals, "shop.example")], Action = RuleAction.MoveToFolder, TargetFolder = "Promos" };
        Assert.Equal("If sender domain is \"shop.example\", then move to Promos.", r.Describe());
    }
}

public sealed class RuleStoreTests : IAsyncLifetime
{
    private string _path = "";
    private SqliteMailStore _store = null!;

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"maildesk-test-{Guid.NewGuid():N}.db");
        _store = new SqliteMailStore(_path);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Rules_round_trip_with_conditions_order_and_hits()
    {
        var account = new Account { Name = "T", Email = "t@x", ImapHost = "h", Username = "u", ProtectedPassword = "p" };
        await _store.UpsertAccountAsync(account);

        var a = new MailRule { AccountId = account.Id, Name = "A", Conditions = [new(RuleField.FromAddress, RuleOperator.Equals, "a@x.test")], Action = RuleAction.MoveToTrash };
        var b = new MailRule { AccountId = account.Id, Name = "B", Conditions = [new(RuleField.Subject, RuleOperator.Regex, "^inv"), new(RuleField.SenderDomain, RuleOperator.EndsWith, ".test")], Action = RuleAction.MoveToFolder, TargetFolder = "Invoices" };
        await _store.UpsertRuleAsync(a);
        await _store.UpsertRuleAsync(b);

        var rules = await _store.GetRulesAsync(account.Id);
        Assert.Equal(["A", "B"], rules.Select(r => r.Name).ToArray());
        Assert.True(rules[0].SortOrder < rules[1].SortOrder);
        Assert.Equal(2, rules[1].Conditions.Count);
        Assert.Equal(RuleOperator.Regex, rules[1].Conditions[0].Operator);
        Assert.Equal("Invoices", rules[1].TargetFolder);
        Assert.True(rules[0].CreatedUtcMs > 0);

        await _store.IncrementRuleHitsAsync(a.Id, 5);
        a.Enabled = false;
        a.Name = "A2";
        await _store.UpsertRuleAsync(a);
        var reloaded = (await _store.GetRulesAsync(account.Id)).First(r => r.Id == a.Id);
        Assert.False(reloaded.Enabled);
        Assert.Equal("A2", reloaded.Name);
        Assert.Equal(5, reloaded.HitCount);

        await _store.DeleteRuleAsync(a.Id);
        Assert.Single(await _store.GetRulesAsync(account.Id));
    }
}
