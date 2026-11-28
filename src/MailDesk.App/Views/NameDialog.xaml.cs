using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

/// <summary>Asks for one short piece of text, such as the name of a saved search. Enter accepts, Escape cancels.</summary>
public partial class NameDialog : FluentWindow
{
    public string? Result { get; private set; }

    public NameDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        Bar.Title = title;
        Prompt.Text = prompt;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel_Click(this, e); };
    }

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Box.Text)) return;
        Result = Box.Text.Trim();
        DialogResult = true;
        Close();
    }
}
