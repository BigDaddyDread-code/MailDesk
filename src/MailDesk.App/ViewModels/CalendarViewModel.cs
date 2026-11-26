using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDesk.Calendar;
using MailDesk.Core.Models;
using MailDesk.Core.Storage;

namespace MailDesk.App.ViewModels;

public sealed partial class DayCell : ObservableObject
{
    public DateTime Date { get; init; }
    public bool IsCurrentMonth { get; init; }
    public bool IsToday => Date == DateTime.Today;
    public string DayNumber => Date.Day.ToString();
    public ObservableCollection<CalendarOccurrence> Occurrences { get; } = [];
    public bool HasMore => Occurrences.Count > 3;
    public string MoreText => HasMore ? $"+{Occurrences.Count - 3} more" : "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Month grid plus a day agenda. Reads items from the store and expands recurrences through IcsCodec.</summary>
public sealed partial class CalendarViewModel : ObservableObject
{
    private readonly IMailStore _store;
    private readonly long _accountId;

    public CalendarViewModel(IMailStore store, long accountId)
    {
        _store = store;
        _accountId = accountId;
        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _selectedDate = DateTime.Today;
    }

    public ObservableCollection<DayCell> Days { get; } = [];
    public ObservableCollection<CalendarOccurrence> Agenda { get; } = [];
    public string[] WeekdayNames { get; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MonthTitle))] private DateTime _month;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AgendaTitle))] private DateTime _selectedDate;
    [ObservableProperty] private CalendarOccurrence? _selectedOccurrence;
    [ObservableProperty] private string _statusText = "";

    public string MonthTitle => Month.ToString("MMMM yyyy");
    public string AgendaTitle => SelectedDate.ToString("dddd d MMMM");

    public async Task LoadAsync()
    {
        var first = Month;
        var gridStart = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)); // Monday-based grid
        var gridEnd = gridStart.AddDays(42);
        var from = new DateTimeOffset(gridStart);
        var to = new DateTimeOffset(gridEnd);
        var items = await _store.GetCalendarItemsAsync(_accountId, from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds());
        var occurrences = IcsCodec.Occurrences(items, from, to);

        Days.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            var cell = new DayCell { Date = date, IsCurrentMonth = date.Month == first.Month, IsSelected = date == SelectedDate };
            foreach (var o in occurrences.Where(o => o.Start.Date <= date && o.End.AddSeconds(-1).Date >= date))
                cell.Occurrences.Add(o);
            Days.Add(cell);
        }
        RefreshAgenda();
        StatusText = $"{items.Count} event{(items.Count == 1 ? "" : "s")} this view";
    }

    private void RefreshAgenda()
    {
        Agenda.Clear();
        var cell = Days.FirstOrDefault(d => d.Date == SelectedDate);
        if (cell is null) return;
        foreach (var o in cell.Occurrences.OrderBy(o => o.Start)) Agenda.Add(o);
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        foreach (var d in Days) d.IsSelected = d.Date == value;
        RefreshAgenda();
    }

    [RelayCommand]
    private void SelectDay(DayCell? cell)
    {
        if (cell is not null) SelectedDate = cell.Date;
    }

    [RelayCommand]
    private async Task PreviousMonthAsync() { Month = Month.AddMonths(-1); await LoadAsync(); }

    [RelayCommand]
    private async Task NextMonthAsync() { Month = Month.AddMonths(1); await LoadAsync(); }

    [RelayCommand]
    private async Task TodayAsync()
    {
        Month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
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

    /// <summary>Imports every event in an .ics file. Returns how many were saved.</summary>
    public async Task<int> ImportAsync(string path)
    {
        var ics = await File.ReadAllTextAsync(path);
        var items = IcsCodec.Parse(ics, _accountId);
        foreach (var item in items) await _store.UpsertCalendarItemAsync(item);
        if (items.Count > 0)
        {
            var firstStart = items.Min(i => i.Start);
            Month = new DateTime(firstStart.Year, firstStart.Month, 1);
            SelectedDate = firstStart.Date;
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
}
