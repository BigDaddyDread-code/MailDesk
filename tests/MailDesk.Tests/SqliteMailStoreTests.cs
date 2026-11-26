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
