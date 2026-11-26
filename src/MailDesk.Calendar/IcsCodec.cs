using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Ical.Net.Serialization.DataTypes;
using MailDesk.Core.Models;

namespace MailDesk.Calendar;

/// <summary>
/// Parses iCalendar text into CalendarItems, expands recurrences, and builds METHOD:REPLY responses.
/// Wraps Ical.Net so the rest of the app never sees its types.
/// </summary>
public static class IcsCodec
{
    private static readonly Regex JoinLink = new(
        @"https?://(?:teams\.microsoft\.com|teams\.live\.com|[a-z0-9.-]*zoom\.us|meet\.google\.com|[a-z0-9.-]*webex\.com)/[^\s<>""')\]]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every VEVENT in the text as an item. Returns an empty list for text that is not a calendar.</summary>
    public static IReadOnlyList<CalendarItem> Parse(string ics, long accountId, long? sourceMessageId = null)
    {
        if (string.IsNullOrWhiteSpace(ics) || !ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return [];
        var calendar = Ical.Net.Calendar.Load(ics);
        if (calendar is null) return [];
        var method = calendar.Method ?? "";
        var items = new List<CalendarItem>();
        foreach (var ev in calendar.Events)
        {
            if (ev.Start is null) continue;
            var startUtc = ev.Start.AsUtc;
            var endUtc = ev.End?.AsUtc ?? (ev.IsAllDay ? startUtc.AddDays(1) : startUtc.AddHours(1));
            var organizer = ev.Organizer;
            var item = new CalendarItem
            {
                AccountId = accountId,
                Uid = ev.Uid ?? Guid.NewGuid().ToString("N"),
                Sequence = ev.Sequence,
                Summary = ev.Summary ?? "(no title)",
                Description = ev.Description ?? "",
                Location = ev.Location ?? "",
                StartUtcMs = new DateTimeOffset(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                EndUtcMs = new DateTimeOffset(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                AllDay = ev.IsAllDay,
                OrganizerEmail = MailFrom(organizer?.Value?.ToString()),
                OrganizerName = organizer?.CommonName ?? "",
                Attendees = ev.Attendees.Select(a => new CalendarAttendee(
                    MailFrom(a.Value?.ToString()),
                    a.CommonName ?? "",
                    a.ParticipationStatus ?? "NEEDS-ACTION",
                    string.Equals(a.Role, "OPT-PARTICIPANT", StringComparison.OrdinalIgnoreCase))).ToList(),
                Status = string.Equals(method, "CANCEL", StringComparison.OrdinalIgnoreCase) ? "CANCELLED" : (ev.Status ?? "CONFIRMED"),
                RecurrenceRule = ev.RecurrenceRule is { } rrule ? new RecurrenceRuleSerializer().SerializeToString(rrule) : null,
                SourceMessageId = sourceMessageId,
                RawIcs = SerializeSingle(calendar, ev),
            };
            item.JoinUrl = FindJoinUrl(ev);
            items.Add(item);
        }
        return items;
    }

    /// <summary>Concrete occurrences of the items inside [from, to), recurring ones expanded.</summary>
    public static IReadOnlyList<CalendarOccurrence> Occurrences(IEnumerable<CalendarItem> items, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<CalendarOccurrence>();
        foreach (var item in items)
        {
            if (!item.IsRecurring || string.IsNullOrWhiteSpace(item.RawIcs))
            {
                if (item.End > from && item.Start < to) result.Add(new CalendarOccurrence(item, item.Start, item.End));
                continue;
            }
            try
            {
                var calendar = Ical.Net.Calendar.Load(item.RawIcs);
                var start = new CalDateTime(from.UtcDateTime, "UTC");
                var end = new CalDateTime(to.UtcDateTime, "UTC");
                foreach (var occ in calendar!.GetOccurrences(start).TakeWhileBefore(end))
                {
                    var s = occ.Period.StartTime.AsUtc;
                    var e = occ.Period.EffectiveEndTime?.AsUtc ?? s.AddMilliseconds(item.EndUtcMs - item.StartUtcMs);
                    result.Add(new CalendarOccurrence(item,
                        new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc)).ToLocalTime(),
                        new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc)).ToLocalTime()));
                }
            }
            catch
            {
                // Unparseable recurrence: fall back to the first instance so the item is at least visible.
                if (item.End > from && item.Start < to) result.Add(new CalendarOccurrence(item, item.Start, item.End));
            }
        }
        return result.OrderBy(o => o.Start).ToList();
    }

    /// <summary>Builds the iTIP REPLY the organizer's server expects: same UID and sequence, one attendee (us) with the new status.</summary>
    public static string BuildReply(CalendarItem item, string myEmail, string myName, RsvpStatus response)
    {
        var partstat = response switch
        {
            RsvpStatus.Accepted => "ACCEPTED",
            RsvpStatus.Tentative => "TENTATIVE",
            RsvpStatus.Declined => "DECLINED",
            _ => "NEEDS-ACTION",
        };
        var reply = new Ical.Net.Calendar { Method = "REPLY", ProductId = "-//MailDesk//EN" };
        var ev = new CalendarEvent
        {
            Uid = item.Uid,
            Sequence = item.Sequence,
            Summary = item.Summary,
            DtStamp = new CalDateTime(DateTime.UtcNow, "UTC"),
            Start = new CalDateTime(item.Start.UtcDateTime, "UTC"),
            End = new CalDateTime(item.End.UtcDateTime, "UTC"),
        };
        if (!string.IsNullOrWhiteSpace(item.OrganizerEmail))
            ev.Organizer = new Organizer("mailto:" + item.OrganizerEmail) { CommonName = item.OrganizerName };
        ev.Attendees.Add(new Attendee("mailto:" + myEmail) { CommonName = myName, ParticipationStatus = partstat, Rsvp = false });
        reply.Events.Add(ev);
        return new CalendarSerializer().SerializeToString(reply) ?? "";
    }

    public static string ResponseSubject(CalendarItem item, RsvpStatus response) => response switch
    {
        RsvpStatus.Accepted => $"Accepted: {item.Summary}",
        RsvpStatus.Tentative => $"Tentative: {item.Summary}",
        RsvpStatus.Declined => $"Declined: {item.Summary}",
        _ => item.Summary,
    };

    private static string SerializeSingle(Ical.Net.Calendar source, CalendarEvent ev)
    {
        // Keep time zone definitions so recurrence expansion stays correct.
        var single = new Ical.Net.Calendar { Method = source.Method, ProductId = source.ProductId };
        foreach (var tz in source.TimeZones) single.TimeZones.Add(tz);
        single.Events.Add(ev);
        return new CalendarSerializer().SerializeToString(single) ?? "";
    }

    private static string? FindJoinUrl(CalendarEvent ev)
    {
        foreach (var key in new[] { "X-MICROSOFT-SKYPETEAMSMEETINGURL", "X-GOOGLE-CONFERENCE", "X-ZOOM-URL" })
        {
            var p = ev.Properties.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
            if (p?.Value is string s && s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s.Trim();
        }
        if (ev.Url is not null && JoinLink.IsMatch(ev.Url.ToString())) return ev.Url.ToString();
        var text = (ev.Description ?? "") + "\n" + (ev.Location ?? "");
        var m = JoinLink.Match(text);
        return m.Success ? m.Value : null;
    }

    private static string MailFrom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var v = value.Trim();
        return v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? v[7..] : v;
    }
}
