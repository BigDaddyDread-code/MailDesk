using System.Windows;
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
        Loaded += async (_, _) => await vm.LoadAsync();
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
