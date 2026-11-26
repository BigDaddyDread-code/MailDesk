# MailDesk

A Windows email client in C# and WPF with an agent that proposes tidy-up actions for human review.
Slack-inspired layout: folder sidebar, message list with avatars, reader pane, and a review pane for agent proposals.

## Layout

| Project | Purpose |
|---|---|
| `src/MailDesk.Core` | Models, `IMailStore` (SQLite + FTS5 search), `IMailSync`, DPAPI credential protection. No UI, no network. |
| `src/MailDesk.Mail` | `ImapMailSync`: MailKit implementation of `IMailSync`. Folder list, header sync, body fetch, move, flags, create and delete folder. |
| `src/MailDesk.Agent` | `AgentProposal`, `IAgentService`, `HeuristicTriageAgent` (local rules, no API key), `ProposalApplier`. |
| `src/MailDesk.App` | WPF shell (WPF UI, CommunityToolkit MVVM, WebView2 reader). |
| `tests/MailDesk.Tests` | xUnit tests for the store and the heuristic agent. Synthetic fixtures only. |
| `tools/MailDesk.Smoke` | End-to-end smoke test that runs every sync feature against a real account configured in the app. |

## Run

```powershell
dotnet build
dotnet test
dotnet run --project src\MailDesk.App
```

Click **Add account**, enter IMAP details, **Test and save**. The Inbox syncs its most recent 500 headers.
Bodies download on first open and are cached. **Tidy with agent** fills the review pane with proposals;
nothing changes on the server until you approve one.

Data lives in `%LocalAppData%\MailDesk\maildesk.db`. Passwords are encrypted with Windows DPAPI (current user).

### Smoke test against a real mailbox

After adding an account in the app:

```powershell
dotnet run --project tools\MailDesk.Smoke
```

It syncs folders and the Inbox, fetches a body, searches, toggles and restores a flag on the newest message,
creates a folder named `MailDesk Smoke`, moves the newest Inbox message into it and back, deletes that folder,
and runs the triage agent in propose-only mode. The mailbox ends in the state it started in.

## Rules

A rule is one or more conditions on sender address, sender name, sender domain, subject or recipients
(contains, is, starts with, ends with, regex; all case-insensitive, all must match) plus one action:
move to a folder (created on first use), move to Trash, mark read, or flag. The first matching rule wins.

- Rules run automatically on new Inbox mail after every sync.
- **Rules** in the top bar lists them; **Run now on current folder** applies them to what is already there.
- Right-click a message and choose **Create rule from sender...** to start a rule pre-filled with that sender.
- **Tidy with agent** also proposes rules for senders that appear three or more times in a junk bucket, so
  approving one grouping keeps that sender out of the Inbox for good.

A rule with no conditions never matches, so a half-finished rule cannot move mail.

## Design rules

- The UI reads only from the local store. The sync layer is the only thing that talks to the server.
- The agent only proposes. `ProposalApplier` is the single path from proposal to mailbox, and it goes through `IMailSync`.
- Agent moves target Trash or Junk, never permanent delete. Sending mail is never an agent tool.
- `DeleteFolderAsync` refuses special folders (Inbox, Sent, Drafts, Trash, Junk, Archive).
- HTML mail renders in WebView2 with scripts off and a CSP that blocks remote images, on a white background.

## Building in a deeply nested folder

If native package files hit the Windows 260-character path limit, set `MAILDESK_ARTIFACTS` to a short
absolute path before building; `Directory.Build.props` then redirects `bin`/`obj` there.

## Next steps

1. Rules engine (sender/subject match to folder), so approved groupings become permanent filters.
2. Compose and send via MailKit SMTP; reply/forward quoting; drafts.
3. Claude-backed `IAgentService` using the Anthropic C# SDK tool runner, with the same proposal contract.
4. Connection pooling and IMAP IDLE for push updates; flag resync for already-cached messages.
5. Threading, OAuth2 for Gmail/Outlook, keyboard shortcuts, notifications.

## License

MIT. See [LICENSE](LICENSE).
