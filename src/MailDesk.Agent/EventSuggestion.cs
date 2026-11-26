using System.Globalization;
using System.Text.Json.Serialization;
using MailDesk.Core.Models;

namespace MailDesk.Agent;

/// <summary>A date the model found in mail or proposed in chat. Stays a suggestion until the user clicks Add to calendar.</summary>
public sealed class EventSuggestion
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>ISO 8601 local time, e.g. 2026-12-03T14:00, or a date for all-day events.</summary>
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("end")] public string End { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("all_day")] public bool AllDay { get; set; }

    [JsonIgnore] public DateTimeOffset? StartAt => ParseLocal(Start);
    [JsonIgnore] public DateTimeOffset? EndAt => ParseLocal(End);
    [JsonIgnore] public bool IsValid => !string.IsNullOrWhiteSpace(Title) && StartAt is not null;

    [JsonIgnore]
    public string WhenText
    {
        get
        {
            if (StartAt is not { } s) return Start;
            var e = EndAt;
            if (AllDay || Start.Length <= 10) return s.ToString("ddd d MMM yyyy") + (e is { } ed && ed.Date > s.Date ? $" to {ed:ddd d MMM yyyy}" : "") + " (all day)";
            return e is { } en && en.Date == s.Date ? $"{s:ddd d MMM yyyy}, {s:HH:mm} to {en:HH:mm}" : $"{s:ddd d MMM yyyy HH:mm}";
        }
    }

    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];

    public static DateTimeOffset? ParseLocal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (DateTime.TryParseExact(t, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        return DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var any) ? any.ToLocalTime() : null;
    }

    /// <summary>Builds the private calendar entry this would become. RawIcs is left for the caller that owns the calendar codec.</summary>
    public CalendarItem ToCalendarItem(long accountId, string uid, string organizerEmail, string organizerName, long? sourceMessageId)
    {
        var start = StartAt ?? throw new InvalidOperationException("The suggestion has no start time.");
        var allDay = AllDay || Start.Trim().Length <= 10;
        var end = EndAt is { } e && e > start ? e : allDay ? start.AddDays(1) : start.AddHours(1);
        if (allDay) { start = new DateTimeOffset(start.Date, TimeZoneInfo.Local.GetUtcOffset(start.Date)); end = new DateTimeOffset(end.Date == start.Date ? end.Date.AddDays(1) : end.Date, TimeZoneInfo.Local.GetUtcOffset(end.Date)); }
        return new CalendarItem
        {
            AccountId = accountId, Uid = uid, Summary = Title.Trim(), Location = Location.Trim(), Description = Notes.Trim(), AllDay = allDay,
            StartUtcMs = start.ToUnixTimeMilliseconds(), EndUtcMs = end.ToUnixTimeMilliseconds(),
            OrganizerEmail = organizerEmail, OrganizerName = organizerName, MyResponse = RsvpStatus.Accepted, SourceMessageId = sourceMessageId,
        };
    }
}
