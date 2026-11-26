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
| `src/MailDesk.Calendar` | `IcsCodec` over Ical.Net: invite parsing, recurrence expansion, iTIP replies. |
| `tests/MailDesk.Tests` | xUnit tests for the store, search, rules, compose, agent toolbox and calendar. Synthetic fixtures only. |
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

## The Claude agent

**Tidy with agent** runs whichever agent is selected in **AI settings** (the gear icon):

- **Local rules**: the heuristic agent, no network, no cost.
- **OpenAI**: `OpenAITriageAgent` uses the same toolbox and prompt through Chat Completions function calling
  (default model `gpt-5`; key from AI settings or `OPENAI_API_KEY`).
- **Claude**: `ClaudeTriageAgent` calls the Anthropic API (default model `claude-opus-5-5`) with a small tool set
  over the local cache: `list_folders`, `get_sender_stats`, `list_messages` (filterable by sender or subject),
  `propose_move`, `propose_rules`, `finish`. The model can only propose; every proposal lands in the same review
  pane as the local agent's, and `ProposalApplier` is still the only path to the mailbox. Message ids the model
  sends back are checked against the folder it was given.

What is sent to the API: sender, subject, date, read/flagged state and, if the preview option is on, the one-line
preview the server already produced. Bodies are never sent. The API key is stored DPAPI-protected in the local
database, or read from `ANTHROPIC_API_KEY` when none is saved. A run is capped at 30 model turns and reports token
usage in the review pane. Requests opt in to Anthropic's server-side refusal fallback so a safety-classifier decline is
re-served by a fallback model instead of failing the run.

## Calendar and meeting invites

`MailDesk.Calendar` wraps Ical.Net. When a message carries a `text/calendar` part or an `.ics` attachment, the sync
layer keeps the raw iCalendar text with the cached body, and the reader shows an invite card: title, time in your
local zone, location, organiser, and **Accept / Tentative / Decline**, which send an iTIP `METHOD:REPLY` to the
organiser (the same UID and sequence, your participation status) and record the response locally. **Add to calendar**
saves without replying; **Join meeting** opens the Teams, Zoom, Meet or Webex link found in the invite.

**Calendar** in the top bar opens the calendar with **Week**, **Month** and **Year** views and a day agenda on the right.
The week view is a time grid (all-day events in a row above it, overlapping events offset into lanes); the year view shows
twelve mini months with busy days filled in, and clicking a day or month title jumps to it. Previous/Next step by the
unit of the current view. Recurring events are expanded through the stored VEVENT, so RRULEs and Windows time-zone names
from Exchange work. **Import .ics** loads a downloaded invite directly. Updates to a meeting replace the stored copy only
when their sequence number is not older.

## Search

The search box searches subject, sender and preview text across the whole account. The toggle at its right end switches
between **All folders** and **This folder**. Folders that were never opened have nothing cached, so an all-folder search
syncs their headers first (Trash and Junk excepted). Results show a folder badge next to the date.

Folder names are searchable too: typing `finance` lists everything in a folder called Finance as well as mail that
mentions it, and `in:finance invoice` (or `folder:finance`) limits the text search to folders whose name contains
"finance". Use an underscore for a space in the folder name: `in:family_school`.

## Compose, reply, forward

**New message** in the top bar opens the compose window: To, Cc/Bcc, subject, plain-text body, file attachments.
The reader header has Reply, Reply all and Forward. Replies quote the original, set In-Reply-To and References
so threads stay intact, and Reply all copies the other recipients minus you and the sender. Forward includes the
original headers above the quoted body. Attachments from the original are not forwarded yet.

Sending goes over SMTP (STARTTLS on 587 by default, implicit TLS on 465) with the account's credentials, then a copy
is appended to the Sent folder. **Save draft** appends to the Drafts folder without sending. Messages go out as
text plus a minimal HTML alternative so other clients keep the line breaks.

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
