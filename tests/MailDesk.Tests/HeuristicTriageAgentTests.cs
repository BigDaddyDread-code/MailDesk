using MailDesk.Agent;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.Tests;

public sealed class HeuristicTriageAgentTests : IAsyncLifetime
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
    public async Task Buckets_scam_codes_jobs_and_promos_and_leaves_real_mail_alone()
    {
        var account = new Account { Name = "T", Email = "t@x", ImapHost = "h", Username = "u", ProtectedPassword = "p" };
        await _store.UpsertAccountAsync(account);
        var inbox = new MailFolder { AccountId = account.Id, FullName = "INBOX", DisplayName = "Inbox", Kind = FolderKind.Inbox };
        await _store.UpsertFolderAsync(inbox);

        await _store.UpsertMessagesAsync(
        [
            new MessageSummary { FolderId = inbox.Id, Uid = 1, FromName = "Google One", FromAddress = "news@plasma-source.de", Subject = "Urgent: your storage is full" },
            new MessageSummary { FolderId = inbox.Id, Uid = 2, FromName = "Google", FromAddress = "no-reply@accounts.google.com", Subject = "Google Verification Code" },
            new MessageSummary { FolderId = inbox.Id, Uid = 3, FromName = "LinkedIn Job Alerts", FromAddress = "jobalerts-noreply@linkedin.com", Subject = "Senior Engineer at Example Corp" },
            new MessageSummary { FolderId = inbox.Id, Uid = 4, FromName = "Shop", FromAddress = "email@my.shop.co.nz", Subject = "Black Friday deals are here" },
            new MessageSummary { FolderId = inbox.Id, Uid = 5, FromName = "Pat Lawyer", FromAddress = "pat@example-law.test", Subject = "RE: your letter of 17 November" },
        ]);

        var agent = new HeuristicTriageAgent(_store);
        var proposals = await agent.ProposeTriageAsync(account.Id, inbox.Id);

        Assert.Equal(4, proposals.Count);
        Assert.All(proposals, p => Assert.Equal(ProposalStatus.Pending, p.Status));
        var allIds = proposals.SelectMany(p => p.MessageIds).ToHashSet();
        var real = (await _store.GetMessagesAsync(inbox.Id)).Single(m => m.Uid == 5);
        Assert.DoesNotContain(real.Id, allIds);
        Assert.Contains(proposals, p => p.TargetFolderName == "Junk" && p.Count == 1);
    }
}
