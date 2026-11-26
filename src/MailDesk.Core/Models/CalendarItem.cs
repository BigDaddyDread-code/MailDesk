namespace MailDesk.Core.Models;

public enum RsvpStatus
{
    NeedsAction,
    Accepted,
    Tentative,
    Declined,
}

public sealed record CalendarAttendee(string Email, string Name, string Status, bool Optional);

/// <summary>
/// One calendar entry, typically imported from a meeting invite. Times are stored as UTC milliseconds; RawIcs keeps the
/// original VEVENT so recurrence can be expanded faithfully and replies can echo the organizer's identifiers.
/// </summary>
public sealed class CalendarItem
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    /// <summary>The iCalendar UID. Together with AccountId this identifies the meeting across updates.</summary>
    public string Uid { get; set; } = "";
    public int Sequence { get; set; }
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Location { get; set; } = "";
    public long StartUtcMs { get; set; }
    public long EndUtcMs { get; set; }
    public bool AllDay { get; set; }
    public string OrganizerEmail { get; set; } = "";
    public string OrganizerName { get; set; } = "";
    public List<CalendarAttendee> Attendees { get; set; } = [];
    /// <summary>Teams, Zoom, Meet or similar link found in the invite, if any.</summary>
    public string? JoinUrl { get; set; }
    public string? RecurrenceRule { get; set; }
    public string Status { get; set; } = "CONFIRMED";
    public RsvpStatus MyResponse { get; set; } = RsvpStatus.NeedsAction;
    /// <summary>Local message id the invite arrived in, if it came by mail.</summary>
    public long? SourceMessageId { get; set; }
    public string RawIcs { get; set; } = "";
    public long UpdatedUtcMs { get; set; }

    public DateTimeOffset Start => DateTimeOffset.FromUnixTimeMilliseconds(StartUtcMs).ToLocalTime();
    public DateTimeOffset End => DateTimeOffset.FromUnixTimeMilliseconds(EndUtcMs).ToLocalTime();
    public bool IsRecurring => !string.IsNullOrWhiteSpace(RecurrenceRule);

    public string WhenText()
    {
        if (AllDay) return Start.ToString("ddd d MMM yyyy") + " (all day)";
        var sameDay = Start.Date == End.Date;
        return sameDay
            ? $"{Start:ddd d MMM yyyy}, {Start:HH:mm} to {End:HH:mm}"
            : $"{Start:ddd d MMM yyyy HH:mm} to {End:ddd d MMM yyyy HH:mm}";
    }
}

/// <summary>One concrete occurrence of an item, after recurrence expansion. Non-recurring items yield exactly one.</summary>
public sealed record CalendarOccurrence(CalendarItem Item, DateTimeOffset Start, DateTimeOffset End);
