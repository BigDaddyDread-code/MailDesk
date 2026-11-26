// End-to-end smoke test against a real account already configured in the MailDesk app.
// Reads the account from the app's local database (password stays DPAPI-protected; this process
// runs as the same Windows user so it can use it without ever printing it).
//
// What it does, in order, and what it touches on the server:
//   1. Folder list sync                        (read only)
//   2. Inbox header sync                       (read only)
//   3. Body fetch for the newest Inbox message (read only)
//   4. Local full-text search                  (local only)
//   5. Flag the newest message, then unflag it (server flag toggled and restored)
//   6. Create folder "MailDesk Smoke", move the newest Inbox message into it,
//      sync it, move it back to Inbox, delete the folder (message ends where it started)
//   7. Run the heuristic triage agent          (proposes only, applies nothing)
//
// Usage: dotnet run --project tools/MailDesk.Smoke [path-to-maildesk.db]

using MailDesk.Agent;
using MailDesk.Core.Models;
using MailDesk.Core.Security;
using MailDesk.Core.Storage;
using MailDesk.Mail;

var dbPath = args.Length > 0 ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailDesk", "maildesk.db");
if (!File.Exists(dbPath)) return Fail($"Database not found at {dbPath}. Add an account in the app first.");

var store = new SqliteMailStore(dbPath);
await store.InitializeAsync();
var sync = new ImapMailSync(store, new DpapiCredentialProtector());

var account = (await store.GetAccountsAsync()).FirstOrDefault();
if (account is null) return Fail("No account configured. Add one in the app first.");
Console.WriteLine($"Account: {account.Name} ({account.ImapHost}:{account.ImapPort})");

var failures = 0;

await Step("1. Sync folder list", async () =>
{
    await sync.SyncFoldersAsync(account);
    var folders = await store.GetFoldersAsync(account.Id);
    Console.WriteLine($"   {folders.Count} folders: " + string.Join(", ", folders.Select(f => $"{f.DisplayName}[{f.Kind}]")));
    Require(folders.Any(f => f.Kind == FolderKind.Inbox), "no Inbox found");
});

var inbox = (await store.GetFoldersAsync(account.Id)).First(f => f.Kind == FolderKind.Inbox);

await Step("2. Sync Inbox headers", async () =>
{
    var progress = new Progress<string>(s => Console.WriteLine("   " + s));
    await sync.SyncFolderAsync(account, inbox, progress: progress);
    var msgs = await store.GetMessagesAsync(inbox.Id);
    Console.WriteLine($"   {msgs.Count} cached, {inbox.UnreadCount} unread on server, highest UID {inbox.HighestUid}");
    Require(msgs.Count > 0, "Inbox is empty; put at least one message in it to test");
});

var newest = (await store.GetMessagesAsync(inbox.Id, limit: 1)).First();
Console.WriteLine($"   Test message: \"{newest.Subject}\" from {newest.FromAddress} (uid {newest.Uid})");

await Step("3. Fetch body", async () =>
{
    var body = await sync.FetchBodyAsync(account, newest);
    var len = (body.HtmlBody?.Length ?? 0) + (body.TextBody?.Length ?? 0);
    Console.WriteLine($"   html={body.HtmlBody?.Length ?? 0} chars, text={body.TextBody?.Length ?? 0} chars");
    Require(len > 0, "body came back empty");
    var cached = await store.GetBodyAsync(newest.Id);
    Require(cached is not null, "body was not cached");
});

await Step("4. Full-text search", async () =>
{
    var word = newest.Subject.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => w.Length > 3) ?? newest.FromAddress.Split('@')[0];
    var hits = await store.SearchAsync(word, account.Id);
    Console.WriteLine($"   search \"{word}\": {hits.Count} hits");
    Require(hits.Any(h => h.Id == newest.Id), "newest message not found by search");
});

await Step("5. Flag and unflag", async () =>
{
    var wasFlagged = newest.IsFlagged;
    await sync.SetFlagsAsync(account, newest, isFlagged: !wasFlagged);
    var after = await store.GetMessageAsync(newest.Id);
    Require(after!.IsFlagged == !wasFlagged, "flag did not change locally");
    await sync.SetFlagsAsync(account, newest, isFlagged: wasFlagged);
    var restored = await store.GetMessageAsync(newest.Id);
    Require(restored!.IsFlagged == wasFlagged, "flag was not restored");
    Console.WriteLine("   flag toggled and restored");
});

await Step("6. Create folder, move message there and back, delete folder", async () =>
{
    var messageIdHeader = newest.MessageId;
    var folder = await sync.CreateFolderAsync(account, "MailDesk Smoke");
    Console.WriteLine($"   created '{folder.FullName}'");
    try
    {
        await sync.MoveAsync(account, [newest], folder);
        await sync.SyncFolderAsync(account, folder);
        var inTest = (await store.GetMessagesAsync(folder.Id)).FirstOrDefault(m => m.MessageId == messageIdHeader);
        Require(inTest is not null, "message did not arrive in the test folder");
        Console.WriteLine($"   moved to test folder as uid {inTest!.Uid}");

        await sync.MoveAsync(account, [inTest], inbox);
        await sync.SyncFolderAsync(account, inbox);
        var back = (await store.GetMessagesAsync(inbox.Id)).FirstOrDefault(m => m.MessageId == messageIdHeader);
        Require(back is not null, "message did not come back to Inbox");
        Console.WriteLine($"   moved back to Inbox as uid {back!.Uid}");
    }
    finally
    {
        await sync.DeleteFolderAsync(account, folder);
        Console.WriteLine("   test folder deleted");
    }
});

await Step("7. Heuristic triage (proposes only)", async () =>
{
    var agent = new HeuristicTriageAgent(store);
    var proposals = await agent.ProposeTriageAsync(account.Id, inbox.Id);
    Console.WriteLine($"   {proposals.Count} proposals");
    foreach (var p in proposals) Console.WriteLine($"   - {p.Title}");
});

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL STEPS PASSED" : $"{failures} STEP(S) FAILED");
return failures == 0 ? 0 : 1;

async Task Step(string name, Func<Task> body)
{
    Console.WriteLine(name);
    try { await body(); Console.WriteLine("   OK"); }
    catch (Exception ex) { failures++; Console.WriteLine($"   FAIL: {ex.Message}"); }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
