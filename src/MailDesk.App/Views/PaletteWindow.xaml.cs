using System.Windows;
using System.Windows.Input;
using MailDesk.App.ViewModels;
using MailDesk.Core.Commands;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

/// <summary>Ctrl+K. Lists what can be done and where to go; Enter runs the highlighted entry after the window closes.</summary>
public partial class PaletteWindow : FluentWindow
{
    private readonly PaletteViewModel _vm;

    /// <summary>What the user picked, or null when dismissed.</summary>
    public PaletteCommand? Chosen { get; private set; }

    public PaletteWindow(PaletteViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => Box.Focus();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PaletteViewModel.Selected) && vm.Selected is not null) List.ScrollIntoView(vm.Selected); };
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _vm.Move(1); e.Handled = true; break;
            case Key.Up: _vm.Move(-1); e.Handled = true; break;
            case Key.PageDown: _vm.Move(8); e.Handled = true; break;
            case Key.PageUp: _vm.Move(-8); e.Handled = true; break;
            case Key.Enter: Choose(_vm.Selected); e.Handled = true; break;
            case Key.Escape: Choose(null); e.Handled = true; break;
        }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Choose(_vm.Selected);

    private void Choose(PaletteCommand? command)
    {
        Chosen = command;
        DialogResult = command is not null;
        Close();
    }
}
