using MailDesk.Core.Models;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;
using MailDesk.Core.Text;

namespace MailDesk.Tests;

public sealed class ReminderTests : IAsyncLifetime
{
    private string _path = "";
    private SqliteMailStore _store = null!;
    private Account _account = null!;
    private MailFolder _inbox = null!;
    private MailFolder _sent = null!;

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"maildesk-test-{Guid.NewGuid():N}.db");
        _store = new SqliteMailStore(_path);
        await _store.InitializeAsync();
        _account = new Account { Name = "Tess Example", Email = "t@example.com", ImapHost = "imap.example.com", Username = "t", ProtectedPassword = "x" };
        await _store.UpsertAccountAsync(_account);
        _inbox = new MailFolder { AccountId = _account.Id, FullName = "INBOX", DisplayName = "Inbox", Kind = FolderKind.Inbox };
        _sent = new MailFolder { AccountId = _account.Id, FullName = "Sent", DisplayName = "Sent", Kind = FolderKind.Sent };
        await _store.UpsertFolderAsync(_inbox);
        await _store.UpsertFolderAsync(_sent);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reminders_cover_unanswered_mail_overdue_commitments_and_tasks_and_honour_snoozes()
    {
        var now = DateTimeOffset.UtcNow;
        long Ago(int days) => now.AddDays(-days).ToUnixTimeMilliseconds();
        await _store.UpsertMessagesAsync(
        [
            new MessageSummary { FolderId = _sent.Id, Uid = 1, MessageId = "<old@me>", Subject = "Quote please", FromAddress = "t@example.com", ToAddresses = "pat@trade.test", DateUtcMs = Ago(8) },
            new MessageSummary { FolderId = _sent.Id, Uid = 2, MessageId = "<new@me>", Subject = "Quick one", FromAddress = "t@example.com", ToAddresses = "sam@shop.test", DateUtcMs = Ago(2) },
            new MessageSummary { FolderId = _sent.Id, Uid = 3, MessageId = "<ans@me>", Subject = "Answered", FromAddress = "t@example.com", ToAddresses = "jo@client.test", DateUtcMs = Ago(9) },
            new MessageSummary { FolderId = _inbox.Id, Uid = 1, MessageId = "<r@jo>", Subject = "Re: Answered", FromAddress = "jo@client.test", InReplyTo = "<ans@me>", DateUtcMs = Ago(8) },
            new MessageSummary { FolderId = _inbox.Id, Uid = 2, MessageId = "<c@pat>", Subject = "Deck quote", FromAddress = "pat@trade.test", FromName = "Pat Jones", DateUtcMs = Ago(20) },
        ]);
        var source = (await _store.GetMessagesAsync(_inbox.Id)).Single(m => m.MessageId == "<c@pat>");
        await _store.ReplaceThreadFactsAsync(_account.Id, "<c@pat>",
        [
            new ThreadFact { Kind = "commitment", Text = "Send the contract", Who = "Pat", DueUtcMs = Ago(3), Subject = "Deck quote", MessageId = source.Id },
            new ThreadFact { Kind = "commitment", Text = "Pay the deposit", Who = "you", IsYours = true, DueUtcMs = Ago(1), Subject = "Deck quote" },
            new ThreadFact { Kind = "commitment", Text = "Not yet due", Who = "Pat", DueUtcMs = now.AddDays(3).ToUnixTimeMilliseconds(), Subject = "Deck quote" },
            new ThreadFact { Kind = "decision", Text = "Build it", Who = "you", Subject = "Deck quote" },
        ]);
        await _store.UpsertTaskAsync(new TaskItem { AccountId = _account.Id, Title = "Book the inspector", DueUtcMs = Ago(2) });
        await _store.UpsertTaskAsync(new TaskItem { AccountId = _account.Id, Title = "Later", DueUtcMs = now.AddDays(5).ToUnixTimeMilliseconds() });

        var service = new ReminderService(_store);
        Assert.Equal(ReminderService.DefaultWaitingDays, await service.GetWaitingDaysAsync());
        var items = await service.ComputeAsync(_account, now);

        // One unanswered sent message past 5 days (the 2-day-old one is too fresh, the answered one is answered),
        // two overdue commitments (theirs and yours), one overdue task; the undated decision and future items are left out.
        Assert.Equal(4, items.Count);
        var noReply = Assert.Single(items, r => r.Kind == ReminderKind.NoReply);
        Assert.Equal("Quote please", noReply.Detail);
        Assert.Contains("pat@trade.test", noReply.Title);
        Assert.True(noReply.CanNudge);
        Assert.Equal(8, noReply.Days);

        var theirs = Assert.Single(items, r => r.Kind == ReminderKind.CommitmentOverdue);
        Assert.StartsWith("Pat promised", theirs.Title);
        Assert.True(theirs.CanNudge);
        Assert.Equal(source.Id, theirs.Message!.Id);

        var yours = Assert.Single(items, r => r.Kind == ReminderKind.PromiseOverdue);
        Assert.False(yours.CanNudge);
        Assert.Single(items, r => r.Kind == ReminderKind.TaskOverdue);

        // Oldest anchor first.
        Assert.Equal(ReminderKind.NoReply, items[0].Kind);

        // A shorter threshold brings the fresh message in; a snooze hides one; dismiss hides for good; both show with includeSnoozed.
        await service.SetWaitingDaysAsync(1);
        Assert.Equal(5, (await service.ComputeAsync(_account, now)).Count);
        await service.SnoozeAsync(noReply, TimeSpan.FromDays(3));
        await service.SnoozeAsync(yours, null);
        var visible = await service.ComputeAsync(_account, now);
        Assert.Equal(3, visible.Count);
        Assert.DoesNotContain(visible, r => r.Key == noReply.Key || r.Key == yours.Key);
        // Four days on: the 3-day snooze has lapsed, the dismiss has not, and the commitment due in 3 days is now overdue too.
        var later = await service.ComputeAsync(_account, now.AddDays(4));
        Assert.Equal(5, later.Count);
        Assert.Contains(later, r => r.Key == noReply.Key);
        Assert.DoesNotContain(later, r => r.Key == yours.Key);
        Assert.Contains(later, r => r.Title == "Pat promised: Not yet due");
        Assert.Equal(5, (await service.ComputeAsync(_account, now, includeSnoozed: true)).Count);
        await service.UnsnoozeAsync(yours);
        Assert.Contains(await service.ComputeAsync(_account, now), r => r.Key == yours.Key);
    }

    [Fact]
    public void Nudge_for_your_own_message_goes_back_to_its_recipients_and_quotes_it()
    {
        var sent = new MessageSummary
        {
            Id = 5, MessageId = "<old@me>", Subject = "Quote please", FromAddress = "t@example.com", ToAddresses = "pat.jones@trade.test, t@example.com",
            CcAddresses = "office@trade.test", DateUtcMs = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        };
        var body = new MessageBody { MessageId = 5, TextBody = "Could you quote the deck?" };

        var nudge = ComposeBuilder.Nudge(sent, body, "t@example.com", "Tess Example");

        Assert.Equal("pat.jones@trade.test", nudge.To); // self removed
        Assert.Equal("office@trade.test", nudge.Cc);
        Assert.Equal("Re: Quote please", nudge.Subject);
        Assert.StartsWith("Hi Pat,", nudge.TextBody);
        Assert.Contains("following up on my message below from", nudge.TextBody);
        Assert.Contains("\"Quote please\"", nudge.TextBody);
        Assert.Contains("Thanks,\nTess", nudge.TextBody);
        Assert.Contains("> Could you quote the deck?", nudge.TextBody);
        Assert.Equal("<old@me>", nudge.InReplyTo);
        Assert.Equal(["<old@me>"], nudge.References);
    }

    [Fact]
    public void Nudge_for_their_message_replies_to_them_about_the_commitment()
    {
        var theirs = new MessageSummary { Id = 6, MessageId = "<c@pat>", Subject = "Deck quote", FromName = "Pat Jones", FromAddress = "pat@trade.test", ToAddresses = "t@example.com", DateUtcMs = 1_700_000_000_000 };

        var nudge = ComposeBuilder.Nudge(theirs, null, "t@example.com", "Tess", about: "what you mentioned about \"Send the contract\"");

        Assert.Equal("\"Pat Jones\" <pat@trade.test>", nudge.To);
        Assert.Equal("", nudge.Cc);
        Assert.StartsWith("Hi Pat,", nudge.TextBody);
        Assert.Contains("following up on what you mentioned about \"Send the contract\"", nudge.TextBody);
        Assert.Contains("how that is going", nudge.TextBody);
    }

    [Theory]
    [InlineData("Pat Jones", "pat@trade.test", "Pat")]
    [InlineData("", "pat.jones@trade.test", "Pat")]
    [InlineData("", "noreply@shop.test", "")]
    [InlineData("", "x@shop.test", "")]
    [InlineData("\"Jones, Pat\"", "pj@trade.test", "Jones")]
    public void First_names_come_from_display_name_then_address(string name, string address, string expected)
    {
        Assert.Equal(expected, ComposeBuilder.FirstName(name, address));
    }

    [Fact]
    public void Greeting_handles_one_two_many_and_none()
    {
        Assert.Equal("Hi,", ComposeBuilder.Greeting([]));
        Assert.Equal("Hi Pat,", ComposeBuilder.Greeting(["Pat", ""]));
        Assert.Equal("Hi Pat and Sam,", ComposeBuilder.Greeting(["Pat", "Sam"]));
        Assert.Equal("Hi all,", ComposeBuilder.Greeting(["Pat", "Sam", "Jo"]));
    }
}
