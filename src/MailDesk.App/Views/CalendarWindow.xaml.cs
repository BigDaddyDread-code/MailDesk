using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MailDesk.App.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class CalendarWindow : FluentWindow
{
    private readonly CalendarViewModel _vm;

    public CalendarWindow(CalendarViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => { await vm.LoadAsync(); ScrollWeekToMorning(); };
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.RequestEventEditor = (item, isNew) =>
        {
            var editor = new EventEditorWindow(new EventEditorViewModel(item, isNew)) { Owner = this };
            var ok = editor.ShowDialog() == true;
            return Task.FromResult(ok ? editor.SendInvitations : (bool?)null);
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.View) && _vm.IsWeekView) ScrollWeekToMorning();
    }

    /// <summary>Opens the week view at 07:00 rather than midnight.</summary>
    private void ScrollWeekToMorning() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => WeekScroll.ScrollToVerticalOffset(7 * CalendarViewModel.HourHeight));

    /// <summary>Clicking an event block in the week view selects its day so the agenda shows the details.</summary>
    private void TimedEvent_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimedEvent ev })
        {
            _vm.SelectedDate = ev.Occurrence.Start.Date;
            _vm.SelectedOccurrence = ev.Occurrence;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Calendar files (*.ics)|*.ics|All files|*.*", Title = "Import calendar file" };
        if (dlg.ShowDialog(this) != true) return;
        try { await _vm.ImportAsync(dlg.FileName); }
        catch (Exception ex) { _vm.StatusText = $"Import failed: {ex.Message}"; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
