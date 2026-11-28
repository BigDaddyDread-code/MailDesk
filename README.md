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

### Decisions, commitments and open questions

**Decisions** on the timeline header sends the conversation (each message's own words, quoted replies stripped, labelled
with its local id) to the model named in AI settings and shows a card with four lists:

- **Decided**: what was agreed, and by whom.
- **Promised**: who owes what, with a due date when one was given and a *done* mark when a later message shows it was carried
  out. A promise of yours has **Add task**, which creates a task with the due date and a link to the message it was made in.
- **Still open**: questions nobody has answered.
- **Unclear**: what the thread leaves ambiguous, or what the model could not tell.

Every item carries the message it came from (**Open** shows it in the reader without changing the list) and a basis: items
said in so many words are plain, items the model read from context are tagged *inferred*, and the model is told to return
only message ids it was given, so a link can never point outside the conversation. The result is kept in the local settings
table under the thread key and shown again next time until the conversation grows; **Hide** only hides it.

Like Summarise and Ask, this runs only on your click and sends message text to the provider you chose.

### Commitments across conversations

Every analysis also writes its items to a local `thread_facts` table, and **Commitments** in the top bar lists them across
the account in four sections: **You owe**, **Owed to you**, **Open questions** and **Decided**. Rows show the conversation
they came from, the *inferred* tag where it applies, and a due badge (red when overdue). Tick a commitment or question off
when it is settled; the tick is yours and survives re-analysing the conversation as long as the item's text is unchanged,
while items the new analysis no longer finds are dropped. **Open** shows the source message; **Add task** turns a promise of
yours into a task. The assistant reads the same table through `list_commitments`, so "what have I promised people?" works.
Only analysed conversations are covered: this is a ledger of what you chose to extract, not a scan of the mailbox.

## Reminders and nudges

The bell in the top bar counts what needs a nudge or a look, worked out locally from the cache with no model involved:

- **Waiting on a reply**: mail you sent that has had no answer for longer than the threshold (default 5 days, set in the
  window: 2 to 21). Uses the same reply matching as the Waiting view.
- **Owed to you** and **You owe**: commitments from analysed conversations past their date.
- **Task**: tasks past their due date.

The window lists them oldest first. **Nudge** opens a follow-up in the compose window, addressed to the original recipients
(or to the person who made the promise), with a short note ("Just following up on my message below from Monday 3 November
about …") and the original quoted underneath, threaded with In-Reply-To so it lands in the same conversation. Nothing is
sent until you click Send; once you do, the reminder is snoozed for the threshold again. **Open** shows the message or the
task list. **3 days** and **1 week** snooze; the cross dismisses for good. Snoozes live in the local settings table and are
listed again with **Show snoozed**. On start the status bar says how many reminders there are.

## Views: Priority, Waiting, Newsletters, Notifications, Clean up

The sidebar starts with **Views** built from the local cache, above the server folders. **All inboxes** comes first and is
account-wide; the other five come one set per account:

- **All inboxes**: every account's Inbox in one list, newest first. With two or more accounts each row carries the account
  name, the folder list grows a header per account, and a search started from this view covers every account. Every action
  on a row (open, reply, flag, trash, drag to a folder, summarise, tasks) resolves the row's own account from its folder, so a
  reply goes out from the address the mail came to. Dragging onto a folder moves only the rows that belong to that folder's
  account and says so; Trash sends each row to its own account's Trash. **Sync** on this view refreshes every Inbox.
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
- **Clean up**: Inbox mail from senders you nearly always get rid of. See the next section.

### Learning what you get rid of

Every time you move mail to Trash yourself (Delete, the trash button, the context menu, or a drag onto Trash), the client
notes the sender. Rules and the agent never teach it: only your own hand does. From that log and the cache it works out,
per sender, how many messages you binned and how many you read and kept. A sender becomes a habit once you have binned at
least 3 of their messages and at least 75% of the ones you decided on; unread mail still in the Inbox counts as undecided.

- The **Clean up** view lists that mail, newest first, each row badged with the history ("Binned all 6", "Binned 5, kept 1").
  Its badge is the count. Select all and press Delete to clear it, or right-click a row to make a rule for the sender.
- **Habits** (the button on the Clean up view, or the palette) shows every sender you have binned mail from, with the
  counts, the share binned, the last time, how many are in the Inbox now, and why each is or is not suggested. Per sender:
  **Bin now** moves their Inbox mail to Trash, **Make rule...** opens the rule editor already set to From equals that address
  with Move to Trash (nothing happens until you save), and **Ignore sender** stops the suggestion without forgetting the
  history. The thresholds are written on the window so the list is never a mystery.
- The assistant sees the same list through `list_waiting` with `view = cleanup`, badge included.

Binning by hand through this view teaches the same way, so the suggestions firm up the more you use it. Nothing is ever
removed on its own: the view shows, you decide.

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

### Saved searches

Once there is something in the search box, a save icon appears at its right end. It asks for a name and adds the search to
a **Saved searches** list in the sidebar, between the views and the folders. Opening one re-runs the query against the
cache, so it is a live view: `from:council has:attachment after:2026-01` stays current as mail arrives. The query is put
back in the search box when the entry is opened, so it can be read and refined; pressing Enter searches with the refined
text, and **Replace with current search** on the entry's right-click menu makes that the saved query.

A search keeps the scope it was saved with: one folder when the toggle said **This folder**, otherwise every folder of the
account, or every account when it was started from **All inboxes**. **Sync** on a saved search refreshes the folder it is
scoped to, else Inbox and Sent of the accounts it spans. Rename and remove are on the same menu; removing an entry never
touches mail. The assistant can see them through `list_saved_searches` and runs one by handing its query to
`search_messages`.

## Keyboard shortcuts and the command palette

**Ctrl+K** (or the prompt icon in the top bar) opens the command palette: one list of everything you can do and everywhere
you can go. Type to filter; words match as runs or as initials, so `ra` finds Reply all and `inb` finds every Inbox. The
list holds actions for the open message (reply, forward, flag, trash, summarise, analyse, task, rule), the windows (tasks,
calendar, commitments, reminders, rules, AI settings), every view, saved search and folder with its account, and, when
nothing else matches, **Search mail for "..."** so typing a few words is also the fastest way to search. Up/Down move,
Enter runs after the palette closes, Escape dismisses. The palette never runs anything on its own, and sending still ends
in the compose window's Send button.

| Shortcut | Does |
|---|---|
| `Ctrl+K` | Command palette |
| `Ctrl+N` | New message |
| `Ctrl+R`, `Ctrl+Shift+R` | Reply, Reply all |
| `Ctrl+Shift+F` | Forward |
| `Ctrl+F` | Jump to the search box |
| `Ctrl+Shift+L` | Flag or unflag the open message |
| `Del` | Move the selected messages to Trash |
| `F5` | Sync the selected folder or view |
| `Ctrl+Shift+V` | Conversations or single messages |
| `Ctrl+T`, `Ctrl+Shift+C` | Tasks, Calendar |
| `Ctrl+Shift+A` | Agent pane |

Every shortcut needs Ctrl or a function key, so typing in the search or question box is never hijacked. The palette shows
each command's shortcut beside it.

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
| Inbox | What arrived and needs attention? | All inboxes across accounts, Priority view, local classification, conversation view, rules, drag and drop, multi-select delete, remote-image control, command palette and keyboard shortcuts, Clean up view learned from what you bin | Snooze, thread-wide move and delete |
| Actions | What do I need to do? | Tasks, agent task proposals, Summarise with Add task / Add to calendar, calendar with invitations | Deadlines extracted automatically from new mail, templates and quick steps |
| Waiting | Who owes me a response? | Waiting view with reply matching by thread headers and subject, `list_waiting` for the assistant, reminders after a threshold with one-click nudge drafts, snooze and dismiss | Reminders as Windows notifications when the app is in the background |
| Knowledge | What was discussed, agreed or promised? | Search operators, folder search, saved searches as live sidebar views, conversation timeline, decisions / commitments / open questions per thread linked to their source message, Commitments ledger across conversations, `get_conversation`, `list_commitments` and `list_saved_searches` for the assistant, Ask-your-mailbox with sources, Summarise | Semantic search, automatic analysis of new replies in already-analysed threads |
| Automation | What can the system handle safely? | Rules engine, agent proposals with review, approval-only path to the mailbox, sender habits with one-click rule from what you already bin | Multi-step rules (save attachment, read invoice number, raise approval), run logs per rule |

Everything the AI does is proposal-only: it never sends, deletes or changes a record on its own, and the review pane shows
what it wants to do before anything happens. That rule holds for every item in the Missing column too.

Engineering next steps, in order:

1. Per-account sync diagnostics; IMAP IDLE for push updates; flag resync for already-cached messages.
2. Windows notifications for new priority mail and reminders while the app is in the background.
3. OAuth2 for Gmail and Outlook; phishing and spoofing warnings on the reader; attachment library.
4. Snooze; thread-wide move and delete.

## License

MIT. See [LICENSE](LICENSE).
