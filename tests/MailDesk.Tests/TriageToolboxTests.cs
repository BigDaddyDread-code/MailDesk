using System.Text.Json;
using MailDesk.Agent;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.Tests;

public sealed class TriageToolboxTests : IAsyncLifetime
{
    private string _path = "";
    private SqliteMailStore _store = null!;
    private Account _account = null!;
    private MailFolder _inbox = null!;
    private MailFolder _other = null!;

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"maildesk-test-{Guid.NewGuid():N}.db");
        _store = new SqliteMailStore(_path);
        await _store.InitializeAsync();
        _account = new Account { Name = "T", Email = "t@x", ImapHost = "h", Username = "u", ProtectedPassword = "p" };
        await _store.UpsertAccountAsync(_account);
        _inbox = new MailFolder { AccountId = _account.Id, FullName = "INBOX", DisplayName = "Inbox", Kind = FolderKind.Inbox };
        _other = new MailFolder { AccountId = _account.Id, FullName = "Other", DisplayName = "Other", Kind = FolderKind.Other };
        await _store.UpsertFolderAsync(_inbox);
        await _store.UpsertFolderAsync(_other);
        await _store.UpsertMessagesAsync(
        [
            new MessageSummary { FolderId = _inbox.Id, Uid = 1, FromName = "Jobs", FromAddress = "alerts@jobs.test", Subject = "New jobs for you", Snippet = "secret preview" },
            new MessageSummary { FolderId = _inbox.Id, Uid = 2, FromName = "Jobs", FromAddress = "alerts@jobs.test", Subject = "More jobs" },
            new MessageSummary { FolderId = _inbox.Id, Uid = 3, FromName = "Pat", FromAddress = "pat@friend.test", Subject = "Lunch" },
            new MessageSummary { FolderId = _other.Id, Uid = 1, FromName = "Elsewhere", FromAddress = "x@other.test", Subject = "Not in inbox" },
        ]);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { }
        return Task.CompletedTask;
    }

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task Sender_stats_and_filtered_listing_work_and_previews_respect_setting()
    {
        var box = new TriageToolbox(_store, _account.Id, _inbox.Id, sharePreviews: false);
        var stats = JsonSerializer.Deserialize<JsonElement>(await box.ExecuteAsync("get_sender_stats", J(new { top = 10 })));
        Assert.Equal("alerts@jobs.test", stats[0].GetProperty("address").GetString());
        Assert.Equal(2, stats[0].GetProperty("count").GetInt32());

        var list = JsonSerializer.Deserialize<JsonElement>(await box.ExecuteAsync("list_messages", J(new { from_filter = "jobs.test" })));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        Assert.False(list.GetProperty("messages")[0].TryGetProperty("preview", out _));

        var withPreview = new TriageToolbox(_store, _account.Id, _inbox.Id, sharePreviews: true);
        var list2 = JsonSerializer.Deserialize<JsonElement>(await withPreview.ExecuteAsync("list_messages", J(new { subject_filter = "new jobs" })));
        Assert.Equal("secret preview", list2.GetProperty("messages")[0].GetProperty("preview").GetString());
    }

    [Fact]
    public async Task Propose_move_keeps_only_ids_from_this_folder()
    {
        var box = new TriageToolbox(_store, _account.Id, _inbox.Id, sharePreviews: true);
        var inboxIds = (await _store.GetMessagesAsync(_inbox.Id)).Select(m => m.Id).ToList();
        var foreignId = (await _store.GetMessagesAsync(_other.Id)).Single().Id;

        var result = JsonSerializer.Deserialize<JsonElement>(await box.ExecuteAsync("propose_move",
            J(new { message_ids = new[] { inboxIds[0], inboxIds[1], foreignId, 999999L }, target_folder = "Trash", title = "Move 2 job alerts to Trash", rationale = "Automated." })));

        Assert.True(result.GetProperty("recorded").GetBoolean());
        Assert.Equal(2, result.GetProperty("messages").GetInt32());
        Assert.Equal(2, result.GetProperty("ignored_unknown_ids").GetInt32());
        var p = Assert.Single(box.Proposals);
        Assert.Equal(ProposalKind.MoveMessages, p.Kind);
        Assert.Equal("Trash", p.TargetFolderName);
        Assert.DoesNotContain(foreignId, p.MessageIds);

        var bad = JsonSerializer.Deserialize<JsonElement>(await box.ExecuteAsync("propose_move", J(new { message_ids = new[] { foreignId }, target_folder = "Trash", title = "x", rationale = "y" })));
        Assert.True(bad.TryGetProperty("error", out _));
        Assert.Single(box.Proposals);
    }

    [Fact]
    public async Task Propose_rules_validates_each_rule_and_finish_ends_the_run()
    {
        var box = new TriageToolbox(_store, _account.Id, _inbox.Id, sharePreviews: true);
        var result = JsonSerializer.Deserialize<JsonElement>(await box.ExecuteAsync("propose_rules", J(new
        {
            title = "Create rules", rationale = "Repeat senders.",
            rules = new object[]
            {
                new { name = "Trash jobs", field = "FromAddress", @operator = "Equals", value = "alerts@jobs.test", action = "MoveToTrash" },
                new { field = "Subject", @operator = "Regex", value = ".*", action = "MoveToTrash" },
                new { field = "FromAddress", @operator = "Equals", value = "x@y.test", action = "MoveToFolder" },
                new { field = "SenderDomain", @operator = "EndsWith", value = "ab", action = "Flag" },
            },
        })));
        Assert.Equal(1, result.GetProperty("rules").GetInt32());
        Assert.Equal(3, result.GetProperty("rejected").GetArrayLength());
        var p = Assert.Single(box.Proposals);
        Assert.Equal(ProposalKind.CreateRule, p.Kind);
        Assert.Equal("Trash jobs", p.Rules[0].Name);
        Assert.Equal(RuleAction.MoveToTrash, p.Rules[0].Action);

        Assert.False(box.Finished);
        await box.ExecuteAsync("finish", J(new { summary = "Done." }));
        Assert.True(box.Finished);
        Assert.Equal("Done.", box.FinishSummary);

        var unknown = JsonSerializer.Deserialize<JsonElement>(await box.ExecuteAsync("nope", J(new { })));
        Assert.True(unknown.TryGetProperty("error", out _));
    }

    [Fact]
    public void Tool_schemas_are_valid_json_objects()
    {
        foreach (var t in TriageToolbox.Tools)
        {
            var el = JsonSerializer.SerializeToElement(t.Schema);
            Assert.Equal("object", el.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        }
    }
}
