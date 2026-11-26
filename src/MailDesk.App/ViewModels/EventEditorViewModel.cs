using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MailDesk.Core.Models;

namespace MailDesk.App.ViewModels;

/// <summary>Backs the event editor. Validates the form and writes it back into a CalendarItem the caller owns.</summary>
public sealed partial class EventEditorViewModel : ObservableObject
{
    private readonly CalendarItem _item;

    public EventEditorViewModel(CalendarItem item, bool isNew)
    {
        _item = item;
        IsNew = isNew;
        _title = item.Summary;
        _location = item.Location;
        _description = item.Description;
        _allDay = item.AllDay;
        _startDate = item.Start.Date;
        _startTime = item.Start.ToString("HH:mm");
        _endDate = item.AllDay ? item.End.AddSeconds(-1).Date : item.End.Date;
        _endTime = item.End.ToString("HH:mm");
        _attendees = string.Join(", ", item.Attendees.Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Email : $"{a.Name} <{a.Email}>"));
    }

    public bool IsNew { get; }
    public string WindowTitle => IsNew ? "New event" : "Edit event";

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _location;
    [ObservableProperty] private string _description;
    [ObservableProperty] private bool _allDay;
    [ObservableProperty] private DateTime _startDate;
    [ObservableProperty] private string _startTime;
    [ObservableProperty] private DateTime _endDate;
    [ObservableProperty] private string _endTime;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAttendees))] private string _attendees;
    [ObservableProperty] private string _error = "";

    public bool HasAttendees => !string.IsNullOrWhiteSpace(Attendees);

    partial void OnStartDateChanged(DateTime value)
    {
        if (EndDate < value) EndDate = value;
    }

    /// <summary>Parses "Name &lt;addr&gt;" or bare addresses separated by commas, semicolons or newlines.</summary>
    public static List<CalendarAttendee> ParseAttendees(string text)
    {
        var result = new List<CalendarAttendee>();
        foreach (var raw in text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = "";
            var email = raw;
            var lt = raw.IndexOf('<');
            var gt = raw.IndexOf('>');
            if (lt >= 0 && gt > lt)
            {
                name = raw[..lt].Trim().Trim('"');
                email = raw[(lt + 1)..gt].Trim();
            }
            if (!email.Contains('@') || email.Contains(' ')) throw new FormatException($"'{raw}' is not an email address.");
            if (result.Any(a => a.Email.Equals(email, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new CalendarAttendee(email, name, "NEEDS-ACTION", false));
        }
        return result;
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var t = text.Trim();
        return TimeSpan.TryParseExact(t, @"h\:mm", CultureInfo.InvariantCulture, out time)
            || TimeSpan.TryParseExact(t, @"hh\:mm", CultureInfo.InvariantCulture, out time)
            || (DateTime.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var dt) && (time = dt.TimeOfDay) >= TimeSpan.Zero);
    }

    /// <summary>Validates and copies the form into the item. Returns false with Error set when something is wrong.</summary>
    public bool TryApply()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Title)) { Error = "Give the event a title."; return false; }
        DateTimeOffset start, end;
        if (AllDay)
        {
            start = new DateTimeOffset(StartDate.Date, TimeZoneInfo.Local.GetUtcOffset(StartDate.Date));
            var endDay = EndDate.Date.AddDays(1);
            end = new DateTimeOffset(endDay, TimeZoneInfo.Local.GetUtcOffset(endDay));
            if (end <= start) { Error = "The end date is before the start date."; return false; }
        }
        else
        {
            if (!TryParseTime(StartTime, out var st)) { Error = "Start time should look like 09:00."; return false; }
            if (!TryParseTime(EndTime, out var et)) { Error = "End time should look like 10:00."; return false; }
            var s = StartDate.Date + st;
            var e = EndDate.Date + et;
            start = new DateTimeOffset(s, TimeZoneInfo.Local.GetUtcOffset(s));
            end = new DateTimeOffset(e, TimeZoneInfo.Local.GetUtcOffset(e));
            if (end <= start) { Error = "The event ends before it starts."; return false; }
        }
        List<CalendarAttendee> attendees;
        try { attendees = ParseAttendees(Attendees); }
        catch (FormatException ex) { Error = ex.Message; return false; }

        // Keep the status of anyone already invited, so an edit does not reset their reply.
        var previous = _item.Attendees.ToDictionary(a => a.Email, a => a, StringComparer.OrdinalIgnoreCase);
        _item.Attendees = attendees.Select(a => previous.TryGetValue(a.Email, out var p) ? p with { Name = string.IsNullOrWhiteSpace(a.Name) ? p.Name : a.Name } : a).ToList();
        _item.Summary = Title.Trim();
        _item.Location = Location.Trim();
        _item.Description = Description.Trim();
        _item.AllDay = AllDay;
        _item.StartUtcMs = start.ToUnixTimeMilliseconds();
        _item.EndUtcMs = end.ToUnixTimeMilliseconds();
        return true;
    }
}
