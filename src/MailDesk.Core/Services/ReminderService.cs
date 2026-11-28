using System.Globalization;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.Core.Services;

public enum ReminderKind
{
    /// <summary>Mail you sent that has had no reply for longer than the threshold.</summary>
    NoReply,
    /// <summary>Something someone else promised you, past its date.</summary>
    CommitmentOverdue,
    /// <summary>Something you promised, past its date.</summary>
    PromiseOverdue,
    /// <summary>A task past its due date.</summary>
    TaskOverdue,
}

/// <summary>One thing worth a nudge or a look. Computed from the local cache; nothing is stored except snoozes.</summary>
public sealed class Reminder
{
    public ReminderKind Kind { get; init; }
    /// <summary>Stable id used for snoozing: "noreply:{messageId}", "fact:{factId}", "task:{taskId}".</summary>
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>When the wait started (sent date) or when the item was due, Unix milliseconds UTC.</summary>
    public long AnchorUtcMs { get; init; }
    /// <summary>The message to nudge about or open: the sent message, or a commitment's source message.</summary>
    public MessageSummary? Message { get; init; }
    public ThreadFact? Fact { get; init; }
    public TaskItem? Task { get; init; }

    public bool CanNudge => Kind is ReminderKind.NoReply or ReminderKind.CommitmentOverdue && Message is not null;
    public int Days => Math.Max(0, (int)Math.Floor((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(AnchorUtcMs)).TotalDays));
    public string AgeText => Kind == ReminderKind.NoReply
        ? (Days == 1 ? "1 day without a reply" : $"{Days} days without a reply")
        : (Days == 0 ? "due today" : Days == 1 ? "1 day overdue" : $"{Days} days overdue");
}

/// <summary>
/// Rule-based reminders over the cache: unanswered sent mail older than a threshold, commitments and tasks past their date.
/// Snoozes and dismissals live in the settings table so they survive restarts and never touch the server.
/// </summary>
public sealed class ReminderService
{
    public const int DefaultWaitingDays = 5;
    private const string WaitingDaysKey = "reminders.waiting_days";
    private const string SnoozePrefix = "reminders.snooze:";
    private const int WaitingWindowDays = 90;

    private readonly IMailStore _store;

    public ReminderService(IMailStore store) => _store = store;

    public async Task<int> GetWaitingDaysAsync(CancellationToken ct = default)
    {
        var raw = await _store.GetSettingAsync(WaitingDaysKey, ct);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : DefaultWaitingDays;
    }

    public Task SetWaitingDaysAsync(int days, CancellationToken ct = default) =>
        _store.SetSettingAsync(WaitingDaysKey, Math.Clamp(days, 1, 60).ToString(CultureInfo.InvariantCulture), ct);

    /// <summary>Hide a reminder for a while; null hides it for good (until the item itself goes away).</summary>
    public Task SnoozeAsync(Reminder reminder, TimeSpan? duration, CancellationToken ct = default)
    {
        var until = duration is { } d ? DateTimeOffset.UtcNow.Add(d).ToUnixTimeMilliseconds() : long.MaxValue;
        return _store.SetSettingAsync(SnoozePrefix + reminder.Key, until.ToString(CultureInfo.InvariantCulture), ct);
    }

    public Task UnsnoozeAsync(Reminder reminder, CancellationToken ct = default) => _store.SetSettingAsync(SnoozePrefix + reminder.Key, null, ct);

    public async Task<IReadOnlyList<Reminder>> ComputeAsync(Account account, DateTimeOffset now, bool includeSnoozed = false, CancellationToken ct = default)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var waitingDays = await GetWaitingDaysAsync(ct);
        var snoozed = new Dictionary<string, long>();
        foreach (var (key, value) in await _store.GetSettingsByPrefixAsync(SnoozePrefix, ct))
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var until)) snoozed[key[SnoozePrefix.Length..]] = until;
        bool Visible(string key) => includeSnoozed || !snoozed.TryGetValue(key, out var until) || until <= nowMs;

        var list = new List<Reminder>();

        foreach (var m in await _store.GetSmartViewAsync(account.Id, SmartView.Waiting, limit: 500, waitingDays: WaitingWindowDays, ct))
        {
            var days = (nowMs - m.DateUtcMs) / 86_400_000.0;
            if (days < waitingDays) continue;
            var key = $"noreply:{m.Id}";
            if (!Visible(key)) continue;
            list.Add(new Reminder
            {
                Kind = ReminderKind.NoReply, Key = key, Title = $"No reply from {Recipients(m.ToAddresses)}", Detail = m.Subject,
                AnchorUtcMs = m.DateUtcMs, Message = m,
            });
        }

        var startOfToday = new DateTimeOffset(now.ToLocalTime().Date, TimeZoneInfo.Local.GetUtcOffset(now.ToLocalTime().Date)).ToUnixTimeMilliseconds();
        foreach (var f in await _store.GetThreadFactsAsync(account.Id, includeDone: false, ct))
        {
            if (f.Kind != "commitment" || f.DueUtcMs is not { } due || due >= startOfToday) continue;
            var key = $"fact:{f.Id}";
            if (!Visible(key)) continue;
            var source = f.MessageId is { } id ? await _store.GetMessageAsync(id, ct) : null;
            list.Add(new Reminder
            {
                Kind = f.IsYours ? ReminderKind.PromiseOverdue : ReminderKind.CommitmentOverdue, Key = key,
                Title = f.IsYours ? $"You promised: {f.Text}" : $"{f.Who} promised: {f.Text}",
                Detail = f.Subject, AnchorUtcMs = due, Fact = f, Message = source,
            });
        }

        foreach (var t in await _store.GetTasksAsync(account.Id, includeDone: false, ct))
        {
            if (t.DueUtcMs is not { } due || due >= startOfToday) continue;
            var key = $"task:{t.Id}";
            if (!Visible(key)) continue;
            list.Add(new Reminder { Kind = ReminderKind.TaskOverdue, Key = key, Title = $"Task overdue: {t.Title}", Detail = t.SourceSubject, AnchorUtcMs = due, Task = t });
        }

        return list.OrderBy(r => r.AnchorUtcMs).ToList();
    }

    /// <summary>"pat@x" → "pat@x"; two or more → "pat@x and 1 other".</summary>
    private static string Recipients(string toAddresses)
    {
        var all = toAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (all.Length == 0) return "recipients";
        return all.Length == 1 ? all[0] : $"{all[0]} and {all.Length - 1} other{(all.Length == 2 ? "" : "s")}";
    }
}
