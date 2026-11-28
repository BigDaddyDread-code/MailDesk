using System.Windows;
using MailDesk.App.ViewModels;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class CommitmentsWindow : FluentWindow
{
    public CommitmentsWindow(CommitmentsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
