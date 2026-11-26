using System.Windows;
using System.Windows.Controls;
using MailDesk.Core.Models;
using MailDesk.Core.Security;
using MailDesk.Core.Services;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class AccountDialog : FluentWindow
{
    private readonly IMailSync _sync;
    private readonly ICredentialProtector _protector;

    public (Account account, string password)? Result { get; private set; }

    public AccountDialog(IMailSync sync, ICredentialProtector protector)
    {
        _sync = sync;
        _protector = protector;
        InitializeComponent();
    }

    private void EmailBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Convenience: guess hosts from the domain until the user types their own.
        var email = EmailBox.Text.Trim();
        var at = email.IndexOf('@');
        if (at < 0) return;
        var domain = email[(at + 1)..];
        if (string.IsNullOrWhiteSpace(UserBox.Text)) UserBox.Text = email;
        if (string.IsNullOrWhiteSpace(ImapHostBox.Text) || ImapHostBox.Text.StartsWith("imap.")) ImapHostBox.Text = $"imap.{domain}";
        if (string.IsNullOrWhiteSpace(SmtpHostBox.Text) || SmtpHostBox.Text.StartsWith("smtp.")) SmtpHostBox.Text = $"smtp.{domain}";
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = domain.Split('.')[0];
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (!int.TryParse(ImapPortBox.Text, out var imapPort) || !int.TryParse(SmtpPortBox.Text, out var smtpPort))
        {
            ShowError("Ports must be numbers.");
            return;
        }
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(EmailBox.Text) || string.IsNullOrWhiteSpace(ImapHostBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text) || string.IsNullOrEmpty(password))
        {
            ShowError("Email, username, password and IMAP host are required.");
            return;
        }

        var account = new Account
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? EmailBox.Text : NameBox.Text,
            Email = EmailBox.Text.Trim(),
            Username = UserBox.Text.Trim(),
            ImapHost = ImapHostBox.Text.Trim(),
            ImapPort = imapPort,
            ImapUseSsl = ImapSslBox.IsChecked == true,
            SmtpHost = SmtpHostBox.Text.Trim(),
            SmtpPort = smtpPort,
            SmtpUseStartTls = SmtpTlsBox.IsChecked == true,
            ProtectedPassword = _protector.Protect(password),
        };

        SaveButton.IsEnabled = false;
        SaveButton.Content = "Testing...";
        try
        {
            await _sync.TestConnectionAsync(account, password);
            Result = (account, password);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Could not sign in: {ex.Message}");
        }
        finally
        {
            SaveButton.IsEnabled = true;
            SaveButton.Content = "Test and save";
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
