using System.Windows;
using System.Windows.Input;
using MailDesk.App.ViewModels;
using MailDesk.Core.Models;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class RulesWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public RulesWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    private MailRule? Selected => RulesList.SelectedItem as MailRule;

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } rule) await _vm.EditRuleCommand.ExecuteAsync(rule);
    }

    private async void Rule_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { } rule) await _vm.EditRuleCommand.ExecuteAsync(rule);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } rule) return;
        var ok = System.Windows.MessageBox.Show(this, $"Delete rule \"{rule.Name}\"?", "Delete rule",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
        if (ok) await _vm.DeleteRuleCommand.ExecuteAsync(rule);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
