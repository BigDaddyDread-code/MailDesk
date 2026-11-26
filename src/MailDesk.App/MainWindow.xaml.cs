using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MailDesk.App.ViewModels;
using MailDesk.App.Views;
using MailDesk.Core.Models;
using MailDesk.Core.Security;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;
using Wpf.Ui.Controls;

namespace MailDesk.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly IMailSync _sync;
    private readonly ICredentialProtector _protector;
    private readonly IMailSender _sender;
    private readonly IMailStore _store;
    private readonly MailDesk.Agent.IDraftingService _drafter;
    private bool _readerReady;

    public MainWindow(MainViewModel vm, IMailSync sync, ICredentialProtector protector, IMailSender sender, IMailStore store, MailDesk.Agent.IDraftingService drafter)
    {
        _vm = vm;
        _sync = sync;
        _protector = protector;
        _sender = sender;
        _store = store;
        _drafter = drafter;
        InitializeComponent();
        DataContext = vm;
        vm.RequestAccountDialog = ShowAccountDialogAsync;
        vm.RequestCompose = ShowComposeAsync;
        vm.RequestAiSettings = () => { new AiSettingsWindow(_store, _protector) { Owner = this }.ShowDialog(); return Task.CompletedTask; };
        vm.RequestCalendar = account => { new CalendarWindow(new CalendarViewModel(_store, account, _sender)) { Owner = this }.Show(); return Task.CompletedTask; };
        vm.RequestSender = () => _sender;
        vm.RequestProtector = () => _protector;
        vm.RequestTasks = account =>
        {
            var tasksVm = new TasksViewModel(_store, account.Id) { RequestOpenMessage = id => { Activate(); return _vm.OpenMessageAsync(id); } };
            new TasksWindow(tasksVm) { Owner = this }.Show();
            return Task.CompletedTask;
        };
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

    private void QuestionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.AskCommand.CanExecute(null)) _vm.AskCommand.Execute(null);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SearchCommand.CanExecute(null))
            _vm.SearchCommand.Execute(null);
    }

    // ---------------- Drag messages onto folders ----------------

    private const string MessageDragFormat = "MailDesk.Messages";
    private Point _dragStart;
    private MessageSummary? _dragCandidate;
    private FolderItem? _dropTarget;

    private void MessageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        var row = System.Windows.Controls.ItemsControl.ContainerFromElement(MessageList, e.OriginalSource as DependencyObject) as System.Windows.Controls.ListViewItem;
        _dragCandidate = row?.DataContext as MessageSummary;
    }

    private void MessageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Drag the whole selection when the pressed row is part of it, otherwise just that row.
        var selected = MessageList.SelectedItems.Cast<MessageSummary>().ToList();
        var payload = selected.Contains(_dragCandidate) ? selected : [_dragCandidate];
        _dragCandidate = null;
        DragDrop.DoDragDrop(MessageList, new DataObject(MessageDragFormat, payload), DragDropEffects.Move);
        SetDropTarget(null);
    }

    private FolderItem? FolderUnder(DragEventArgs e) =>
        (System.Windows.Controls.ItemsControl.ContainerFromElement(FolderList, e.OriginalSource as DependencyObject) as System.Windows.Controls.ListBoxItem)?.DataContext as FolderItem;

    private void SetDropTarget(FolderItem? folder)
    {
        if (ReferenceEquals(_dropTarget, folder)) return;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
        _dropTarget = folder;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = true;
    }

    private void FolderList_DragOver(object sender, DragEventArgs e)
    {
        var folder = FolderUnder(e);
        var ok = e.Data.GetDataPresent(MessageDragFormat) && folder is not null && _vm.CanDropOn(folder);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(ok ? folder : null);
        e.Handled = true;
    }

    private void FolderList_DragLeave(object sender, DragEventArgs e)
    {
        // DragOver on the next item re-highlights; this only clears when the pointer leaves the list.
        if (FolderUnder(e) is null) SetDropTarget(null);
    }

    private async void FolderList_Drop(object sender, DragEventArgs e)
    {
        var folder = FolderUnder(e);
        SetDropTarget(null);
        e.Handled = true;
        if (folder is null || e.Data.GetData(MessageDragFormat) is not List<MessageSummary> messages) return;
        await _vm.MoveMessagesAsync(messages, folder);
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
        var window = new ComposeWindow(new ComposeViewModel(_sender, account, seed, _drafter)) { Owner = this };
        window.ShowDialog();
        return Task.FromResult(window.Outcome);
    }
}
