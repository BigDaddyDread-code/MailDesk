using System.Windows;
using System.Windows.Input;
using MailDesk.App.ViewModels;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class TasksWindow : FluentWindow
{
    private readonly TasksViewModel _vm;

    public TasksWindow(TasksViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => { await vm.LoadAsync(); NewBox.Focus(); };
    }

    private void NewBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.AddCommand.CanExecute(null)) _vm.AddCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
