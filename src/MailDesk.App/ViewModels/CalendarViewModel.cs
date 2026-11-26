using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Calendar;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.App.ViewModels;

public enum CalendarView { Week, Month, Year }

/// <summary>One day of the month grid.</summary>
public sealed partial class DayCell : ObservableObject
{
    public DateTime Date { get; init; }
    public bool IsCurrentMonth { get; init; }
    public bool IsToday => Date == DateTime.Today;
    public string DayNumber => Date.Day.ToString();
    public ObservableCollection<CalendarOccurrence> Occurrences { get; } = [];
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A timed event positioned on the week view's time grid. Top and Height are pixels; Lane offsets overlapping events.</summary>
public sealed class TimedEvent
{
    public required CalendarOccurrence Occurrence { get; init; }
    public double Top { get; init; }
    public double Height { get; init; }
    public int Lane { get; init; }
    public Thickness Margin => new(2 + Lane * 14, Top, 2, 0);
    public string TimeText => $"{Occurrence.Start:HH:mm} to {Occurrence.End:HH:mm}";
    public string Summary => Occurrence.Item.Summary;
}

/// <summary>One column of the week view.</summary>
public sealed class WeekColumn
{
    public DateTime Date { get; init; }
    public string Header => Date.ToString("ddd d", CultureInfo.CurrentCulture);
    public bool IsToday => Date == DateTime.Today;
    public List<CalendarOccurrence> AllDay { get; } = [];
    public List<TimedEvent> Timed { get; } = [];
}

/// <summary>One day in a year-view mini month.</summary>
public sealed class MiniDay
{
    public DateTime Date { get; init; }
    public bool IsCurrentMonth { get; init; }
    public bool IsToday => Date == DateTime.Today;
    public bool HasEvents { get; init; }
    public string DayNumber => IsCurrentMonth ? Date.Day.ToString() : "";
}

/// <summary>One of the twelve mini months in the year view.</summary>
public sealed class MiniMonth
{
    public DateTime Month { get; init; }
    public string Title => Month.ToString("MMMM", CultureInfo.CurrentCulture);
    public List<MiniDay> Days { get; } = [];
    public int EventCount { get; init; }
}

/// <summary>
/// Week, month and year views over the stored calendar items, plus a day agenda. Reads items from the store and
/// expands recurrences through IcsCodec. SelectedDate is the anchor every view navigates from.
/// </summary>
public sealed partial class CalendarViewModel : ObservableObject
{
    public const double HourHeight = 48;
    public const double TimelineHeight = HourHeight * 24;

    private readonly IMailStore _store;
    private readonly long _accountId;
    private IReadOnlyList<CalendarOccurrence> _occurrences = [];

    public CalendarViewModel(IMailStore store, long accountId)
    {
        _store = store;
        _accountId = accountId;
        _selectedDate = DateTime.Today;
    }

    public ObservableCollection<DayCell> Days { get; } = [];
    public ObservableCollection<WeekColumn> WeekColumns { get; } = [];
    public ObservableCollection<MiniMonth> YearMonths { get; } = [];
    public ObservableCollection<CalendarOccurrence> Agenda { get; } = [];
    public string[] WeekdayNames { get; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    public string[] WeekdayInitials { get; } = ["M", "T", "W", "T", "F", "S", "S"];
    public string[] Hours { get; } = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeekView), nameof(IsMonthView), nameof(IsYearView), nameof(Title))]
    private CalendarView _view = CalendarView.Month;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgendaTitle), nameof(Title))]
    private DateTime _selectedDate;

    [ObservableProperty] private CalendarOccurrence? _selectedOccurrence;
    [ObservableProperty] private string _statusText = "";

    public bool IsWeekView => View == CalendarView.Week;
    public bool IsMonthView => View == CalendarView.Month;
    public bool IsYearView => View == CalendarView.Year;

    /// <summary>First day of the month that holds the selected date.</summary>
    public DateTime Month => new(SelectedDate.Year, SelectedDate.Month, 1);

    /// <summary>Monday of the week that holds the selected date.</summary>
    public DateTime WeekStart => SelectedDate.AddDays(-(((int)SelectedDate.DayOfWeek + 6) % 7));

    public string Title => View switch
    {
        CalendarView.Week => WeekTitle(),
        CalendarView.Year => SelectedDate.Year.ToString(),
        _ => Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
    };

    public string AgendaTitle => SelectedDate.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

    private string WeekTitle()
    {
        var start = WeekStart;
        var end = start.AddDays(6);
        if (start.Month == end.Month) return $"{start:d} to {end:d MMMM yyyy}";
        if (start.Year == end.Year) return $"{start:d MMM} to {end:d MMM yyyy}";
        return $"{start:d MMM yyyy} to {end:d MMM yyyy}";
    }

    partial void OnViewChanged(CalendarView value) => _ = LoadAsync();

    partial void OnSelectedDateChanged(DateTime value)
    {
        foreach (var d in Days) d.IsSelected = d.Date == value;
        RefreshAgenda();
    }

    /// <summary>Loads items for the range the current view shows and rebuilds that view.</summary>
    public async Task LoadAsync()
    {
        var (from, to) = VisibleRange();
        var items = await _store.GetCalendarItemsAsync(_accountId, new DateTimeOffset(from).ToUnixTimeMilliseconds(), new DateTimeOffset(to).ToUnixTimeMilliseconds());
        _occurrences = IcsCodec.Occurrences(items, new DateTimeOffset(from), new DateTimeOffset(to));

        switch (View)
        {
            case CalendarView.Week: BuildWeek(); break;
            case CalendarView.Year: BuildYear(); break;
            default: BuildMonth(); break;
        }
        RefreshAgenda();
        var noun = View == CalendarView.Year ? "this year" : View == CalendarView.Week ? "this week" : "this month";
        var count = _occurrences.Count;
        StatusText = $"{count} event{(count == 1 ? "" : "s")} {noun}";
    }

    private (DateTime from, DateTime to) VisibleRange() => View switch
    {
        CalendarView.Week => (WeekStart, WeekStart.AddDays(7)),
        CalendarView.Year => (new DateTime(SelectedDate.Year, 1, 1), new DateTime(SelectedDate.Year + 1, 1, 1)),
        _ => (MonthGridStart(Month), MonthGridStart(Month).AddDays(42)),
    };

    private static DateTime MonthGridStart(DateTime month) => month.AddDays(-(((int)month.DayOfWeek + 6) % 7)); // Monday-based grid

    private static bool CoversDay(CalendarOccurrence o, DateTime date) => o.Start.Date <= date && o.End.AddSeconds(-1).Date >= date;

    private void BuildMonth()
    {
        Days.Clear();
        var gridStart = MonthGridStart(Month);
        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            var cell = new DayCell { Date = date, IsCurrentMonth = date.Month == Month.Month, IsSelected = date == SelectedDate };
            foreach (var o in _occurrences.Where(o => CoversDay(o, date)).OrderBy(o => o.Start)) cell.Occurrences.Add(o);
            Days.Add(cell);
        }
    }

    private void BuildWeek()
    {
        WeekColumns.Clear();
        for (var i = 0; i < 7; i++)
        {
            var date = WeekStart.AddDays(i);
            var column = new WeekColumn { Date = date };
            var dayStart = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
            var timed = new List<CalendarOccurrence>();
            foreach (var o in _occurrences.Where(o => CoversDay(o, date)).OrderBy(o => o.Start))
            {
                if (o.Item.AllDay || o.End - o.Start >= TimeSpan.FromHours(24)) column.AllDay.Add(o);
                else timed.Add(o);
            }
            // Lanes: an event takes the first lane whose previous event ended before this one starts.
            var laneEnds = new List<DateTimeOffset>();
            foreach (var o in timed)
            {
                var lane = laneEnds.FindIndex(end => end <= o.Start);
                if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(o.End); } else laneEnds[lane] = o.End;
                var startMinutes = Math.Clamp((o.Start - dayStart).TotalMinutes, 0, 24 * 60);
                var endMinutes = Math.Clamp((o.End - dayStart).TotalMinutes, startMinutes, 24 * 60);
                var top = startMinutes / 60 * HourHeight;
                var height = Math.Max(20, (endMinutes - startMinutes) / 60 * HourHeight - 2);
                column.Timed.Add(new TimedEvent { Occurrence = o, Top = top, Height = Math.Min(height, TimelineHeight - top), Lane = lane });
            }
            WeekColumns.Add(column);
        }
    }

    private void BuildYear()
    {
        YearMonths.Clear();
        var busyDays = _occurrences
            .SelectMany(o => Enumerable.Range(0, Math.Max(1, (o.End.AddSeconds(-1).Date - o.Start.Date).Days + 1)).Select(d => o.Start.Date.AddDays(d)))
            .ToHashSet();
        for (var m = 1; m <= 12; m++)
        {
            var month = new DateTime(SelectedDate.Year, m, 1);
            var gridStart = MonthGridStart(month);
            var mini = new MiniMonth { Month = month, EventCount = _occurrences.Count(o => o.Start.Month == m && o.Start.Year == month.Year) };
            for (var i = 0; i < 42; i++)
            {
                var date = gridStart.AddDays(i);
                var inMonth = date.Month == m;
                mini.Days.Add(new MiniDay { Date = date, IsCurrentMonth = inMonth, HasEvents = inMonth && busyDays.Contains(date) });
            }
            YearMonths.Add(mini);
        }
    }

    private void RefreshAgenda()
    {
        Agenda.Clear();
        foreach (var o in _occurrences.Where(o => CoversDay(o, SelectedDate)).OrderBy(o => o.Start)) Agenda.Add(o);
    }

    // ---------------- Navigation ----------------

    [RelayCommand]
    private void SetView(string? name)
    {
        if (Enum.TryParse<CalendarView>(name, ignoreCase: true, out var view) && view != View) View = view;
    }

    [RelayCommand]
    private void SelectDay(DayCell? cell)
    {
        if (cell is not null) SelectedDate = cell.Date;
    }

    /// <summary>Year view: a day click jumps to that day in the month view.</summary>
    [RelayCommand]
    private void GoToDay(MiniDay? day)
    {
        if (day is null || !day.IsCurrentMonth) return;
        SelectedDate = day.Date;
        View = CalendarView.Month;
    }

    /// <summary>Year view: a month title click opens that month.</summary>
    [RelayCommand]
    private void GoToMonth(MiniMonth? month)
    {
        if (month is null) return;
        SelectedDate = month.Month == Month ? SelectedDate : month.Month;
        View = CalendarView.Month;
    }

    /// <summary>Week view: a column header click selects that day.</summary>
    [RelayCommand]
    private void SelectColumn(WeekColumn? column)
    {
        if (column is not null) SelectedDate = column.Date;
    }

    [RelayCommand]
    private async Task PreviousAsync() => await StepAsync(-1);

    [RelayCommand]
    private async Task NextAsync() => await StepAsync(1);

    private async Task StepAsync(int direction)
    {
        SelectedDate = View switch
        {
            CalendarView.Week => SelectedDate.AddDays(7 * direction),
            CalendarView.Year => SelectedDate.AddYears(direction),
            _ => Month.AddMonths(direction).AddDays(Math.Min(SelectedDate.Day, DateTime.DaysInMonth(Month.AddMonths(direction).Year, Month.AddMonths(direction).Month)) - 1),
        };
        await LoadAsync();
    }

    [RelayCommand]
    private async Task TodayAsync()
    {
        SelectedDate = DateTime.Today;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteOccurrenceAsync(CalendarOccurrence? occ)
    {
        if (occ is null) return;
        await _store.DeleteCalendarItemAsync(occ.Item.Id);
        await LoadAsync();
        StatusText = $"Deleted: {occ.Item.Summary}";
    }

    /// <summary>Imports every event in an .ics file, then shows the month of the first one. Returns how many were saved.</summary>
    public async Task<int> ImportAsync(string path)
    {
        var ics = await File.ReadAllTextAsync(path);
        var items = IcsCodec.Parse(ics, _accountId);
        foreach (var item in items) await _store.UpsertCalendarItemAsync(item);
        if (items.Count > 0)
        {
            SelectedDate = items.Min(i => i.Start).Date;
            if (View == CalendarView.Year) View = CalendarView.Month;
        }
        await LoadAsync();
        StatusText = items.Count == 0 ? "No events found in that file." : $"Imported {items.Count} event{(items.Count == 1 ? "" : "s")}.";
        return items.Count;
    }

    public static void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void Join(CalendarOccurrence? occ) => OpenLink(occ?.Item.JoinUrl);

    [RelayCommand]
    private void JoinTimed(TimedEvent? ev) => OpenLink(ev?.Occurrence.Item.JoinUrl);
}
