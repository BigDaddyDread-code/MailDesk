using System.ComponentModel;
using System.Windows;
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
    private readonly IMailSender _sender;
    private bool _readerReady;

    public MainWindow(MainViewModel vm, IMailSync sync, ICredentialProtector protector, IMailSender sender)
    {
        _vm = vm;
        _sync = sync;
        _protector = protector;
        _sender = sender;
        InitializeComponent();
        DataContext = vm;
        vm.RequestAccountDialog = ShowAccountDialogAsync;
        vm.RequestCompose = ShowComposeAsync;
        vm.RequestRuleEditor = ShowRuleEditorAsync;
        vm.RequestRulesWindow = ShowRulesWindowAsync;
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

    /// <summary>The window the user is looking at, so dialogs stack correctly over the rules window too.</summary>
    private Window ActiveOwner() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;

    private Task<(Account account, string password)?> ShowAccountDialogAsync()
    {
        var dialog = new AccountDialog(_sync, _protector) { Owner = this };
        var ok = dialog.ShowDialog() == true;
        return Task.FromResult(ok ? dialog.Result : null);
    }

    private Task<(MailRule rule, bool applyNow)?> ShowRuleEditorAsync(MailRule? rule, long accountId, IReadOnlyList<FolderChoice> folders)
    {
        var editor = new RuleEditorWindow(new RuleEditorViewModel(rule, accountId, folders)) { Owner = ActiveOwner() };
        var ok = editor.ShowDialog() == true && editor.Result is not null;
        return Task.FromResult(ok ? (editor.Result!, editor.ApplyNow) : ((MailRule, bool)?)null);
    }

    private Task ShowRulesWindowAsync()
    {
        new RulesWindow(_vm) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }

    private Task<string?> ShowComposeAsync(Account account, OutgoingMessage seed)
    {
        var window = new ComposeWindow(new ComposeViewModel(_sender, account, seed)) { Owner = this };
        window.ShowDialog();
        return Task.FromResult(window.Outcome);
    }
}
