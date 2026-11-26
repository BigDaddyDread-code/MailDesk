using System.Windows;
using MailDesk.App.ViewModels;
using MailDesk.Core.Models;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class RuleEditorWindow : FluentWindow
{
    private readonly RuleEditorViewModel _vm;

    public MailRule? Result { get; private set; }
    public bool ApplyNow => _vm.ApplyNow;

    public RuleEditorWindow(RuleEditorViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var rule = _vm.Validate();
        if (rule is null) return;
        Result = rule;
        DialogResult = true;
        Close();
    }
}
