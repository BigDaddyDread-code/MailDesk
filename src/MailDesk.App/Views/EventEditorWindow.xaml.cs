using System.Windows;
using MailDesk.App.ViewModels;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class EventEditorWindow : FluentWindow
{
    private readonly EventEditorViewModel _vm;

    public EventEditorWindow(EventEditorViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => TitleBox.Focus();
    }

    /// <summary>True when the user asked for invitations to go out as well as saving.</summary>
    public bool SendInvitations { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e) => Finish(send: false);

    private void SaveSend_Click(object sender, RoutedEventArgs e) => Finish(send: true);

    private void Finish(bool send)
    {
        if (!_vm.TryApply()) return;
        SendInvitations = send;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
