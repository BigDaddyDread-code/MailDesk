using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.Tests;

public sealed class SqliteMailStoreTests : IAsyncLifetime
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

    private async Task<(Account account, MailFolder folder)> SeedAsync()
    {
        var account = new Account { Name = "Test", Email = "t@example.com", ImapHost = "imap.example.com", Username = "t", ProtectedPassword = "x" };
        await _store.UpsertAccountAsync(account);
        var folder = new MailFolder { AccountId = account.Id, FullName = "INBOX", DisplayName = "Inbox", Kind = FolderKind.Inbox };
        await _store.UpsertFolderAsync(folder);
        return (account, folder);
    }

    [Fact]
    public async Task Upsert_and_list_messages_updates_folder_counts()
    {
        var (_, folder) = await SeedAsync();
        await _store.UpsertMessagesAsync(
        [
            new MessageSummary { FolderId = folder.Id, Uid = 1, Subject = "Hello", FromAddress = "a@x.com", DateUtcMs = 1000, IsRead = false },
            new MessageSummary { FolderId = folder.Id, Uid = 2, Subject = "World", FromAddress = "b@y.com", DateUtcMs = 2000, IsRead = true },
        ]);

        var list = await _store.GetMessagesAsync(folder.Id);
        Assert.Equal(2, list.Count);
        Assert.Equal("World", list[0].Subject); // newest first

        var f = await _store.GetFolderAsync(folder.Id);
        Assert.NotNull(f);
        Assert.Equal(2, f!.TotalCount);
        Assert.Equal(1, f.UnreadCount);
        Assert.Equal(2, f.HighestUid);
    }

    [Fact]
    public async Task Upsert_same_uid_twice_is_idempotent()
    {
        var (_, folder) = await SeedAsync();
        var m = new MessageSummary { FolderId = folder.Id, Uid = 7, Subject = "Once", FromAddress = "a@x.com" };
        await _store.UpsertMessagesAsync([m]);
        await _store.UpsertMessagesAsync([m]);
        Assert.Single(await _store.GetMessagesAsync(folder.Id));
    }

    [Fact]
    public async Task Full_text_search_matches_subject_and_sender_prefixes()
    {
        var (account, folder) = await SeedAsync();
        await _store.UpsertMessagesAsync(
        [
            new MessageSummary { FolderId = folder.Id, Uid = 1, Subject = "Loan variation letter", FromName = "Example Bank", FromAddress = "service@examplebank.test" },
            new MessageSummary { FolderId = folder.Id, Uid = 2, Subject = "Black Friday deals", FromName = "Shop", FromAddress = "promo@shop.example" },
        ]);

        var bySubject = await _store.SearchAsync("loan variat", account.Id);
        Assert.Single(bySubject);
        Assert.Equal(1, bySubject[0].Uid);

        var bySender = await _store.SearchAsync("examplebank");
        Assert.Single(bySender);

        var none = await _store.SearchAsync("zzzz");
        Assert.Empty(none);
    }

    [Fact]
    public async Task Tasks_round_trip_order_and_completion()
    {
        var (account, _) = await SeedAsync();
        var tomorrow = DateTimeOffset.Now.Date.AddDays(1);
        var noDue = await _store.UpsertTaskAsync(new TaskItem { AccountId = account.Id, Title = "No deadline" });
        var due = await _store.UpsertTaskAsync(new TaskItem { AccountId = account.Id, Title = "Pay rates", DueUtcMs = new DateTimeOffset(tomorrow).ToUnixTimeMilliseconds(), SourceSubject = "Rates notice" });

        var open = await _store.GetTasksAsync(account.Id, includeDone: false);
        Assert.Equal(2, open.Count);
        Assert.Equal("Pay rates", open[0].Title); // dated tasks come before undated ones
        Assert.Equal("Tomorrow", open[0].DueText);
        Assert.False(open[0].IsOverdue);

        await _store.SetTaskDoneAsync(due, true);
        Assert.Single(await _store.GetTasksAsync(account.Id, includeDone: false));
        var all = await _store.GetTasksAsync(account.Id, includeDone: true);
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(t => t.Id == due).IsDone);
        Assert.NotNull(all.Single(t => t.Id == due).CompletedUtcMs);

        var edit = all.Single(t => t.Id == noDue);
        edit.Title = "Renamed";
        await _store.UpsertTaskAsync(edit);
        Assert.Contains(await _store.GetTasksAsync(account.Id, true), t => t.Title == "Renamed");

        await _store.DeleteTaskAsync(noDue);
        Assert.Single(await _store.GetTasksAsync(account.Id, includeDone: true));
    }

    [Fact]
    public async Task Search_matches_folder_names_and_scopes_with_in_prefix()
    {
        var (account, inbox) = await SeedAsync();
        var finance = new MailFolder { AccountId = account.Id, FullName = "Finance", DisplayName = "Finance & Bills", Kind = FolderKind.Other };
        await _store.UpsertFolderAsync(finance);
        await _store.UpsertMessagesAsync(
        [
            new MessageSummary { FolderId = inbox.Id, Uid = 1, Subject = "Invoice 100", FromAddress = "a@example.test", DateUtcMs = 3 },
            new MessageSummary { FolderId = finance.Id, Uid = 1, Subject = "Invoice 200", FromAddress = "b@example.test", DateUtcMs = 2 },
            new MessageSummary { FolderId = finance.Id, Uid = 2, Subject = "Rates notice", FromAddress = "c@example.test", DateUtcMs = 1 },
        ]);

        // A plain word that is also a folder name returns everything in that folder.
        var byFolderName = await _store.SearchAsync("finance", account.Id);
        Assert.Equal(2, byFolderName.Count);
        Assert.All(byFolderName, m => Assert.Equal("Finance & Bills", m.FolderName));

        // in: narrows the text search to matching folders.
        var scoped = await _store.SearchAsync("in:finance invoice", account.Id);
        var hit = Assert.Single(scoped);
        Assert.Equal("Invoice 200", hit.Subject);

        // in: alone lists the folder.
        Assert.Equal(2, (await _store.SearchAsync("folder:bills", account.Id)).Count);

        // folderId restricts to one folder regardless of where else the text appears.
        var inboxOnly = await _store.SearchAsync("invoice", account.Id, folderId: inbox.Id);
        Assert.Equal("Invoice 100", Assert.Single(inboxOnly).Subject);
        Assert.Equal("Inbox", inboxOnly[0].FolderName);

        // Results from a normal folder listing have no folder name attached.
        Assert.All(await _store.GetMessagesAsync(inbox.Id), m => Assert.Equal("", m.FolderName));
    }

    [Fact]
    public async Task Search_operators_filter_by_sender_recipient_date_attachments_and_boolean_text()
    {
        var (account, inbox) = await SeedAsync();
        long Day(int d) => new DateTimeOffset(2026, 3, d, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await _store.UpsertMessagesAsync(
        [
            new MessageSummary { FolderId = inbox.Id, Uid = 1, Subject = "Invoice 100", FromAddress = "accounts@supplier.test", FromName = "Supplier", ToAddresses = "me@example.test", DateUtcMs = Day(1), HasAttachments = true, Size = 3_000_000 },
            new MessageSummary { FolderId = inbox.Id, Uid = 2, Subject = "Invoice 200", FromAddress = "pat@friend.test", ToAddresses = "me@example.test, other@example.test", DateUtcMs = Day(10), IsRead = true },
            new MessageSummary { FolderId = inbox.Id, Uid = 3, Subject = "Car loan rates", FromAddress = "bank@bank.test", ToAddresses = "me@example.test", DateUtcMs = Day(20) },
            new MessageSummary { FolderId = inbox.Id, Uid = 4, Subject = "Home loan rates", FromAddress = "bank@bank.test", ToAddresses = "me@example.test", DateUtcMs = Day(25) },
        ]);

        Assert.Equal("Invoice 100", Assert.Single(await _store.SearchAsync("from:supplier", account.Id)).Subject);
        Assert.Equal("Invoice 100", Assert.Single(await _store.SearchAsync("invoice has:attachment", account.Id)).Subject);
        Assert.Equal("Invoice 100", Assert.Single(await _store.SearchAsync("larger:2mb", account.Id)).Subject);
        Assert.Equal("Invoice 200", Assert.Single(await _store.SearchAsync("to:other@example.test", account.Id)).Subject);
        Assert.Equal("Invoice 200", Assert.Single(await _store.SearchAsync("invoice is:read", account.Id)).Subject);
        Assert.Equal(2, (await _store.SearchAsync("subject:invoice", account.Id)).Count);

        // Date operators: both bank mails are after the 15th; before cuts the later one off.
        var after = await _store.SearchAsync("after:2026-03-15", account.Id);
        Assert.Equal(2, after.Count);
        Assert.Equal("Home loan rates", Assert.Single(await _store.SearchAsync("after:2026-03-15 -car", account.Id)).Subject);
        Assert.Equal("Car loan rates", Assert.Single(await _store.SearchAsync("loan before:2026-03-22", account.Id)).Subject);

        // Boolean text.
        Assert.Equal(4, (await _store.SearchAsync("invoice OR loan", account.Id)).Count);
        Assert.Equal(2, (await _store.SearchAsync("invoice OR loan -rates", account.Id)).Count);
        Assert.Equal("Home loan rates", Assert.Single(await _store.SearchAsync("loan NOT car", account.Id)).Subject);
    }

    [Fact]
    public async Task Smart_views_separate_priority_newsletters_notifications_and_waiting()
    {
        var (account, inbox) = await SeedAsync();
        var sent = new MailFolder { AccountId = account.Id, FullName = "Sent", DisplayName = "Sent", Kind = FolderKind.Sent };
        await _store.UpsertFolderAsync(sent);
        var now = DateTimeOffset.UtcNow;
        long Ago(int days) => now.AddDays(-days).ToUnixTimeMilliseconds();

        await _store.UpsertMessagesAsync(
        [
            // Inbox: one person, one newsletter, one notification.
            new MessageSummary { FolderId = inbox.Id, Uid = 1, Subject = "Lunch?", FromAddress = "pat@friend.test", DateUtcMs = Ago(1), Category = MessageCategory.Primary },
            new MessageSummary { FolderId = inbox.Id, Uid = 2, Subject = "Spring sale", FromAddress = "news@shop.test", DateUtcMs = Ago(1), Category = MessageCategory.Newsletter },
            new MessageSummary { FolderId = inbox.Id, Uid = 3, Subject = "Your code is 123456", FromAddress = "noreply@bank.test", DateUtcMs = Ago(1), Category = MessageCategory.Notification },
            // Sent: answered by In-Reply-To, answered by subject from a recipient, unanswered, and a note to self.
            new MessageSummary { FolderId = sent.Id, Uid = 1, MessageId = "<s1@me>", Subject = "Quote for the deck", FromAddress = "t@example.com", ToAddresses = "builder@trade.test", DateUtcMs = Ago(10) },
            new MessageSummary { FolderId = sent.Id, Uid = 2, MessageId = "<s2@me>", Subject = "Rates query", FromAddress = "t@example.com", ToAddresses = "rates@council.test", DateUtcMs = Ago(8) },
            new MessageSummary { FolderId = sent.Id, Uid = 3, MessageId = "<s3@me>", Subject = "Can you send the contract?", FromAddress = "t@example.com", ToAddresses = "jo@client.test", DateUtcMs = Ago(5) },
            new MessageSummary { FolderId = sent.Id, Uid = 4, MessageId = "<s4@me>", Subject = "Note to self", FromAddress = "t@example.com", ToAddresses = "t@example.com", DateUtcMs = Ago(2) },
            new MessageSummary { FolderId = sent.Id, Uid = 5, MessageId = "<s5@me>", Subject = "Old request", FromAddress = "t@example.com", ToAddresses = "someone@far.test", DateUtcMs = Ago(90) },
            // Replies in the Inbox.
            new MessageSummary { FolderId = inbox.Id, Uid = 4, Subject = "Re: Quote for the deck", FromAddress = "other@trade.test", InReplyTo = "<s1@me>", DateUtcMs = Ago(9) },
            new MessageSummary { FolderId = inbox.Id, Uid = 5, Subject = "RE: Rates query", FromAddress = "Rates@council.test", DateUtcMs = Ago(7) },
            // A same-subject message from someone who was not a recipient does not count as a reply.
            new MessageSummary { FolderId = inbox.Id, Uid = 6, Subject = "Re: Can you send the contract?", FromAddress = "stranger@elsewhere.test", DateUtcMs = Ago(4) },
        ]);

        var priority = await _store.GetSmartViewAsync(account.Id, SmartView.Priority);
        Assert.Contains(priority, m => m.Subject == "Lunch?");
        Assert.DoesNotContain(priority, m => m.Subject == "Spring sale" || m.Subject.StartsWith("Your code"));
        Assert.All(priority, m => Assert.Equal("Inbox", m.FolderName));

        Assert.Equal("Spring sale", Assert.Single(await _store.GetSmartViewAsync(account.Id, SmartView.Newsletters)).Subject);
        Assert.Equal("Your code is 123456", Assert.Single(await _store.GetSmartViewAsync(account.Id, SmartView.Notifications)).Subject);

        var waiting = await _store.GetSmartViewAsync(account.Id, SmartView.Waiting);
        var only = Assert.Single(waiting);
        Assert.Equal("Can you send the contract?", only.Subject);
        Assert.True(only.ShowRecipients);
        Assert.Equal("Waiting 5 days", only.Badge);
        Assert.Equal("To: jo@client.test", only.Correspondent);

        // A wider window brings the old request back.
        Assert.Equal(2, (await _store.GetSmartViewAsync(account.Id, SmartView.Waiting, waitingDays: 120)).Count);
    }

    [Fact]
    public async Task Thread_keys_group_replies_and_conversation_rows_collapse_them()
    {
        var (account, inbox) = await SeedAsync();
        var sent = new MailFolder { AccountId = account.Id, FullName = "Sent", DisplayName = "Sent", Kind = FolderKind.Sent };
        await _store.UpsertFolderAsync(sent);

        await _store.UpsertMessagesAsync(
        [
            // A: original from Pat; B: my reply (References A); C: Pat's reply with a truncated References (only B);
            // D: a "Re:" from Pat with no thread headers at all; E: an unrelated message; F: a lone message with no Message-ID.
            new MessageSummary { FolderId = inbox.Id, Uid = 1, MessageId = "<a@pat>", Subject = "Deck quote", FromAddress = "pat@trade.test", DateUtcMs = 1000 },
            new MessageSummary { FolderId = sent.Id, Uid = 1, MessageId = "<b@me>", Subject = "Re: Deck quote", FromAddress = "t@example.com", ToAddresses = "pat@trade.test", InReplyTo = "<a@pat>", ReferencesHeader = "<a@pat>", DateUtcMs = 2000 },
            new MessageSummary { FolderId = inbox.Id, Uid = 2, MessageId = "<c@pat>", Subject = "Re: Deck quote", FromAddress = "pat@trade.test", InReplyTo = "<b@me>", ReferencesHeader = "<b@me>", DateUtcMs = 3000 },
            new MessageSummary { FolderId = inbox.Id, Uid = 3, MessageId = "<d@pat>", Subject = "RE: Deck quote", FromAddress = "pat@trade.test", DateUtcMs = 4000 },
            new MessageSummary { FolderId = inbox.Id, Uid = 4, MessageId = "<e@x>", Subject = "Rates notice", FromAddress = "rates@council.test", DateUtcMs = 2500 },
            new MessageSummary { FolderId = inbox.Id, Uid = 5, MessageId = "", Subject = "(no subject)", FromAddress = "odd@x.test", DateUtcMs = 500 },
        ]);

        var inboxRows = await _store.GetMessagesAsync(inbox.Id);
        var a = inboxRows.Single(m => m.MessageId == "<a@pat>");
        Assert.Equal("<a@pat>", a.ThreadKey);
        Assert.Equal("<a@pat>", inboxRows.Single(m => m.MessageId == "<c@pat>").ThreadKey); // chased through B
        Assert.Equal("<a@pat>", inboxRows.Single(m => m.MessageId == "<d@pat>").ThreadKey); // joined by subject
        Assert.Equal("<e@x>", inboxRows.Single(m => m.MessageId == "<e@x>").ThreadKey);
        Assert.StartsWith("msg:", inboxRows.Single(m => m.MessageId == "").ThreadKey);
        Assert.Equal(4, a.ThreadCount); // A, B, C, D across Inbox and Sent
        Assert.Equal(1, inboxRows.Single(m => m.MessageId == "<e@x>").ThreadCount);
        Assert.Equal("", inboxRows.Single(m => m.MessageId == "<e@x>").ThreadBadge);
        Assert.Equal("4", a.ThreadBadge);

        // Conversation rows: the deck thread collapses to its newest Inbox message, D.
        var threads = await _store.GetThreadsAsync(inbox.Id);
        Assert.Equal(3, threads.Count);
        var deck = threads.Single(t => t.ThreadKey == "<a@pat>");
        Assert.Equal("<d@pat>", deck.MessageId);
        Assert.Equal(4, deck.ThreadCount);
        Assert.Equal(4, deck.ThreadUnread);

        // The timeline spans folders, oldest first.
        var timeline = await _store.GetThreadAsync(account.Id, "<a@pat>");
        Assert.Equal(["<a@pat>", "<b@me>", "<c@pat>", "<d@pat>"], timeline.Select(m => m.MessageId).ToArray());
        Assert.Equal("Sent", timeline[1].FolderName);

        // Waiting uses the thread too: my reply B was answered by C, so nothing is owed.
        Assert.Empty(await _store.GetSmartViewAsync(account.Id, SmartView.Waiting, waitingDays: 36500));
    }

    [Fact]
    public async Task Thread_facts_replace_per_thread_keep_done_and_order_by_due()
    {
        var (account, _) = await SeedAsync();
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        var lastWeek = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
        await _store.ReplaceThreadFactsAsync(account.Id, "<t1>",
        [
            new ThreadFact { Kind = "commitment", Text = "Send the contract", Who = "Pat", Due = "2026-12-05", DueUtcMs = tomorrow, Subject = "Deck quote" },
            new ThreadFact { Kind = "commitment", Text = "Pay the deposit", Who = "you", IsYours = true, DueUtcMs = lastWeek, Subject = "Deck quote" },
            new ThreadFact { Kind = "decision", Text = "Build the deck", Who = "you", Subject = "Deck quote" },
        ]);
        await _store.ReplaceThreadFactsAsync(account.Id, "<t2>",
        [
            new ThreadFact { Kind = "question", Text = "Which timber?", Who = "Pat", Subject = "Timber" },
        ]);

        var all = await _store.GetThreadFactsAsync(account.Id, includeDone: true);
        Assert.Equal(4, all.Count);
        Assert.Equal("Pay the deposit", all[0].Text); // dated items first, soonest (here: overdue) first
        Assert.True(all[0].IsOverdue);
        Assert.Equal("Send the contract", all[1].Text);
        Assert.False(all[1].IsOverdue);

        // Mark one done; re-analysing the thread keeps that flag for the unchanged item and drops items that vanished.
        await _store.SetThreadFactDoneAsync(all[0].Id, true);
        await _store.ReplaceThreadFactsAsync(account.Id, "<t1>",
        [
            new ThreadFact { Kind = "commitment", Text = "Pay the deposit", Who = "you", IsYours = true, Subject = "Deck quote" },
            new ThreadFact { Kind = "commitment", Text = "Send the signed contract", Who = "Pat", Subject = "Deck quote" },
        ]);
        var open = await _store.GetThreadFactsAsync(account.Id, includeDone: false);
        Assert.Equal(["Send the signed contract", "Which timber?"], open.Select(f => f.Text).OrderBy(t => t).ToArray());
        var withDone = await _store.GetThreadFactsAsync(account.Id, includeDone: true);
        Assert.True(withDone.Single(f => f.Text == "Pay the deposit").IsDone);
        Assert.DoesNotContain(withDone, f => f.Text == "Build the deck"); // gone from the new analysis

        // Settings by prefix, used to backfill facts from cached analyses.
        await _store.SetSettingAsync("thread.insight:<t1>", "{}");
        await _store.SetSettingAsync("thread.insight:<t2>", "{}");
        await _store.SetSettingAsync("other", "x");
        Assert.Equal(2, (await _store.GetSettingsByPrefixAsync("thread.insight:")).Count);
    }

    [Fact]
    public async Task Update_flags_and_sender_stats()
    {
        var (_, folder) = await SeedAsync();
        await _store.UpsertMessagesAsync(
        [
            new MessageSummary { FolderId = folder.Id, Uid = 1, Subject = "A", FromAddress = "x@a.com" },
            new MessageSummary { FolderId = folder.Id, Uid = 2, Subject = "B", FromAddress = "x@a.com" },
            new MessageSummary { FolderId = folder.Id, Uid = 3, Subject = "C", FromAddress = "y@b.com", IsRead = true },
        ]);
        var first = (await _store.GetMessagesAsync(folder.Id)).First(m => m.Uid == 1);
        await _store.UpdateFlagsAsync(first.Id, isRead: true, isFlagged: true);

        var reloaded = await _store.GetMessageAsync(first.Id);
        Assert.True(reloaded!.IsRead);
        Assert.True(reloaded.IsFlagged);

        var stats = await _store.GetSenderStatsAsync(folder.Id);
        Assert.Equal("x@a.com", stats[0].FromAddress);
        Assert.Equal(2L, stats[0].Count);
        Assert.Equal(1L, stats[0].UnreadCount);
    }

    [Fact]
    public void Fts_query_builder_quotes_terms_and_adds_prefix()
    {
        Assert.Equal("\"loan\"* \"variat\"*", SqliteMailStore.ToFtsQuery("loan variat"));
        Assert.Equal("\"smith\"*", SqliteMailStore.ToFtsQuery("\"smith\""));
        Assert.Equal("", SqliteMailStore.ToFtsQuery("   "));
    }
}
