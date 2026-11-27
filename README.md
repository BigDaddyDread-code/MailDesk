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

**New event** opens the editor: title, all-day or timed start and end, location, notes, and people to invite. **Save** keeps
a private entry; **Save and send invitations** also emails each person a `METHOD:REQUEST` invitation they can accept or
decline in their own client. Events you organise have an **Edit** button in the agenda; saving an edit bumps the sequence
number and can resend the updated invitation. Invitations from other people are responded to from the message they
arrived in, not edited here.

## Conversations and the timeline

Every message gets a thread key when it is cached: the first Message-ID in `References`, else `In-Reply-To`, else its own
Message-ID. Two fix-ups make real mail hang together: a key that points at a cached message which already belongs to another
thread is chased to that thread (clients that truncate `References`), and a `Re:` with no thread headers at all joins the
earliest message in the account with the same normalised subject. Keys are plain indexed columns, so threading costs
nothing at read time.

- **Conversations**, the toggle at the top of the message list, shows one row per thread: the newest message, with a count
  badge for how many messages the conversation holds across the account (Trash and Junk excluded) and bold when any of them
  is unread. Off, the list is one row per message and the badge still shows on messages that belong to a thread. The
  setting is remembered.
- **The timeline** appears above the open message whenever its conversation has more than one message: every message in
  it across folders (your replies from Sent included), oldest first, each with who, which folder, a one-line preview, when,
  and an unread dot. The open message is highlighted; click another to read it. Clicking one that is not in the current list
  (your own reply in Sent, say) shows it in the reader without changing the list or its selection.
- **Waiting** uses thread keys as its first test for "has been answered", ahead of the header and subject matching.
- **Ask your mailbox** gets `get_conversation`: the whole exchange behind a message with each message's own text (quoted
  replies stripped), so "what did we agree with the builder?" reads one timeline instead of five messages.

Operations on a conversation row act on that one message, not the whole thread. Deleting or moving a thread at once is
not built yet.

## Views: Priority, Waiting, Newsletters, Notifications

The sidebar starts with four **Views** built from the local cache, one set per account, above the server folders:

- **Priority**: Inbox mail from people. Every message is classified at sync time from its headers, locally and without a
  model: `List-Unsubscribe`, `List-Id` or `Precedence: bulk` marks a newsletter; `Auto-Submitted`, bounce and no-reply
  style senders (and system senders with transactional subjects such as receipts, codes and reminders) mark a notification;
  everything else is correspondence. The badge is the unread count.
- **Waiting**: mail you sent in the last 45 days that nobody has answered. A reply is any later message from one of the
  recipients that names the sent message in `In-Reply-To` or `References`, or carries the same subject once Re:/Fwd:
  prefixes are stripped. Each row leads with who it went to and how long it has waited; the badge is the count. Mail to
  yourself is skipped. **Sync** on this view refreshes Sent and Inbox first.
- **Newsletters** and **Notifications**: that category across every folder except Sent, Drafts, Trash and Junk, so the
  Inbox can be read without them and they can still be cleared in one sweep (multi-select, Delete).

Classification is stored on the message, so views are plain indexed queries. Messages cached before this existed are
classified from sender and subject alone on first start.

## Tasks

**Tasks** in the top bar is a local to-do list: add a task with an optional due date, tick it off, delete it, and show or
hide done ones. Overdue tasks are marked. A task made from a message keeps a link back to it. Three ways in:

- Right-click a message and choose **Create task from message**.
- **Tidy with agent** proposes tasks for mail that asks for something (a bill, a form, a reply owed, a deadline); the local
  agent uses subject keywords on unread or flagged mail, the model agents use the `propose_tasks` tool. Approving the
  proposal adds the tasks and nothing else.
- Type one into the Tasks window.

## Selecting, moving and deleting

Multi-select messages with Ctrl or Shift. Drag the selection onto a folder in the sidebar to move it; the folder under the
pointer highlights green when it can take the drop, and Drafts and other accounts' folders refuse it. **Delete**, the bin
button in the reader header, or right-click **Move to Trash** sends every selected message to Trash (never a permanent
delete) and selects the next message so you can keep going. Right-clicking a row outside the selection acts on that row alone.

## Summarise and ask

The sparkle button in the reader header sends the open message to the model named in AI settings and shows a card above
the body: a short summary, the things the mail asks you to do (each with **Add task**), any dates it proposes or confirms
(each with **Add to calendar**), and up to three quick replies that open the compose window with that text above the
quote. Scams are called out and get no replies.

**Ask your mailbox** sits at the top of the agent pane (the robot icon). Type a question such as "what did the council say
about rates?" or "what's on next week?" and the model answers by searching and reading your mail, calendar and to-do list
through read-only tools (`search_messages`, `list_messages`, `get_message`, `list_folders`, `list_events`, `list_tasks`).
Each answer lists the messages it looked at; click one to open it. When you ask to put something in the calendar or on
the list, or mail clearly holds an appointment or deadline, the model calls `suggest_event` or `suggest_task`, which only
shows you an **Add** button: nothing is saved until you click it. The conversation is kept for follow-up questions until
you clear it, and nothing in it can move, send or delete mail.

Both send message text to the provider, and both only run when you click.

## Writing with AI

The compose window has a bar above the body: type what the email should say (or leave it empty when replying) and click
**Draft with AI**. The model named in AI settings writes the body; the text lands above the quoted original for you to
edit, and nothing is sent until you click Send. What goes to the model: your instructions, the subject, the recipients,
and the quoted message with anything already typed. With the local agent selected, the bar asks you to choose Claude or
OpenAI first.

## Search

The search box searches subject, sender and preview text across the whole account. The toggle at its right end switches
between **All folders** and **This folder**. Folders that were never opened have nothing cached, so an all-folder search
syncs their headers first (Trash and Junk excepted). Results show a folder badge next to the date.

Folder names are searchable too: typing `finance` lists everything in a folder called Finance as well as mail that
mentions it, and `in:finance invoice` (or `folder:finance`) limits the text search to folders whose name contains
"finance". Use an underscore for a space in the folder name: `in:family_school`.

Operators narrow the search further and can be combined with free text or used on their own:

| Operator | Matches |
|---|---|
| `from:name` / `from:address` | Sender name or address contains the value |
| `to:address` (or `cc:`) | To or Cc contains the value |
| `subject:word` | Subject contains the value (underscore for a space) |
| `has:attachment` | Messages with attachments |
| `is:unread`, `is:read`, `is:flagged` | Read and flag state |
| `after:2026-01`, `before:2026-03-15`, `on:2026-02-01` | Date bounds: `yyyy-mm-dd`, `yyyy-mm`, `yyyy`, `today`, `yesterday`, `7d` |
| `larger:10mb`, `smaller:500k` | Size bounds |
| `rates OR levy`, `loan NOT car`, `loan -car` | Boolean free text (upper-case operators; a leading minus excludes a word) |

The Ask-your-mailbox assistant knows the same operators, so "what did the council send me in March with an attachment?"
becomes one search rather than a scan.

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
- Agent moves target Trash or Junk, never permanent delete. Sending mail is never an agent tool; the drafting assistant
  only returns text into the compose window.
- `DeleteFolderAsync` refuses special folders (Inbox, Sent, Drafts, Trash, Junk, Archive).
- HTML mail renders in WebView2 with scripts off and a CSP that blocks remote images, on a white background.
  A bar above such a message offers **Show images** (this message only) or **Always show from this sender**
  (the address is kept in the local settings table). Links always open in the system browser, never in the reader.

## Building in a deeply nested folder

If native package files hit the Windows 260-character path limit, set `MAILDESK_ARTIFACTS` to a short
absolute path before building; `Directory.Build.props` then redirects `bin`/`obj` there.

## Where this sits, and what is next

The aim is not another client with a summary button but one that turns correspondence into things you can find, trace and
act on while staying in control. Measured against five workspaces, this is the state of play:

| Workspace | Question | Have | Missing |
|---|---|---|---|
| Inbox | What arrived and needs attention? | Priority view, local classification, conversation view, rules, drag and drop, multi-select delete, remote-image control | Unified inbox across accounts, snooze, keyboard command palette, thread-wide move and delete |
| Actions | What do I need to do? | Tasks, agent task proposals, Summarise with Add task / Add to calendar, calendar with invitations | Deadlines extracted automatically from new mail, templates and quick steps |
| Waiting | Who owes me a response? | Waiting view with reply matching by thread headers and subject, `list_waiting` for the assistant | Reminders when a wait passes a threshold, "nudge" drafts |
| Knowledge | What was discussed, agreed or promised? | Search operators, folder search, conversation timeline, `get_conversation` for the assistant, Ask-your-mailbox with sources, Summarise | Semantic search, decisions and commitments extracted per thread with links to the source message, saved searches |
| Automation | What can the system handle safely? | Rules engine, agent proposals with review, approval-only path to the mailbox | Multi-step rules (save attachment, read invoice number, raise approval), run logs per rule |

Everything the AI does is proposal-only: it never sends, deletes or changes a record on its own, and the review pane shows
what it wants to do before anything happens. That rule holds for every item in the Missing column too.

Engineering next steps, in order:

1. Unified inbox and per-account sync diagnostics; IMAP IDLE for push updates; flag resync for already-cached messages.
2. Decisions, commitments and open questions extracted per conversation, each linked to its source message.
3. Saved searches as sidebar views, built on the search operators above.
4. Keyboard shortcuts and a command palette.
5. OAuth2 for Gmail and Outlook; phishing and spoofing warnings on the reader; attachment library.

## License

MIT. See [LICENSE](LICENSE).
