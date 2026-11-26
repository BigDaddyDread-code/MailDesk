using System.ComponentModel;
using System.Windows.Input;
using MailDesk.App.ViewModels;
using MailDesk.App.Views;
using MailDesk.Core.Models;
using MailDesk.Core.Security;
using MailDesk.Core.Services;
using Wpf.Ui.Controls;

namespace MailDesk.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly IMailSync _sync;
    private readonly ICredentialProtector _protector;
    private bool _readerReady;

    public MainWindow(MainViewModel vm, IMailSync sync, ICredentialProtector protector)
    {
        _vm = vm;
        _sync = sync;
        _protector = protector;
        InitializeComponent();
        DataContext = vm;
        vm.RequestAccountDialog = ShowAccountDialogAsync;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += async (_, _) => await InitReaderAsync();
    }

    private async Task InitReaderAsync()
    {
        try
        {
            await Reader.EnsureCoreWebView2Async();
            var s = Reader.CoreWebView2.Settings;
            s.IsScriptEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            // Links in mail open in the system browser, never inside the reader.
            Reader.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            Reader.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenExternal(e.Uri); }
            };
            _readerReady = true;
            Reader.NavigateToString(_vm.ReaderHtml);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Reader unavailable (WebView2 runtime missing?): {ex.Message}";
        }
    }

    private static void OpenExternal(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ReaderHtml) && _readerReady)
            Reader.NavigateToString(_vm.ReaderHtml);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SearchCommand.CanExecute(null))
            _vm.SearchCommand.Execute(null);
    }

    private Task<(Account account, string password)?> ShowAccountDialogAsync()
    {
        var dialog = new AccountDialog(_sync, _protector) { Owner = this };
        var ok = dialog.ShowDialog() == true;
        return Task.FromResult(ok ? dialog.Result : null);
    }
}
