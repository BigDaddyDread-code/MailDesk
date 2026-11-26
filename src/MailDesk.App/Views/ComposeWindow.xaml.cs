using System.Windows;
using MailDesk.App.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class ComposeWindow : FluentWindow
{
    private readonly ComposeViewModel _vm;
    private bool _done;

    public string? Outcome => _vm.Outcome;

    public ComposeWindow(ComposeViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => { if (string.IsNullOrWhiteSpace(vm.To)) ToBox.Focus(); else BodyBox.Focus(); };
        Closing += (_, e) =>
        {
            if (_done || !_vm.HasContent || _vm.IsBusy) return;
            var r = System.Windows.MessageBox.Show(this, "Discard this message?", "Discard", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (r != System.Windows.MessageBoxResult.Yes) e.Cancel = true;
        };
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (await _vm.SendAsync()) { _done = true; Close(); }
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (await _vm.SaveDraftAsync()) { _done = true; Close(); }
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Attach files" };
        if (dlg.ShowDialog(this) == true) _vm.AddAttachments(dlg.FileNames);
    }

    private void Discard_Click(object sender, RoutedEventArgs e) => Close();
}
