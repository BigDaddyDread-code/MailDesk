using System.Windows;
using System.Windows.Media;
using MailDesk.Agent;
using MailDesk.Core.Security;
using MailDesk.Core.Storage;
using Wpf.Ui.Controls;

namespace MailDesk.App.Views;

public partial class AiSettingsWindow : FluentWindow
{
    private readonly IMailStore _store;
    private readonly ICredentialProtector _protector;
    private AgentSettings _settings = new();

    public AiSettingsWindow(IMailStore store, ICredentialProtector protector)
    {
        _store = store;
        _protector = protector;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _settings = await AgentSettings.LoadAsync(_store);
        LocalRadio.IsChecked = !_settings.UseClaude;
        ClaudeRadio.IsChecked = _settings.UseClaude;
        ModelBox.Text = _settings.Model;
        PreviewsBox.IsChecked = _settings.SharePreviews;
        var env = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        KeyStatus.Text = _settings.HasApiKey ? "A key is saved." : env ? "No saved key; ANTHROPIC_API_KEY from the environment will be used." : "No key saved.";
    }

    private async Task<string?> EffectiveKeyAsync()
    {
        var typed = KeyBox.Password;
        if (!string.IsNullOrWhiteSpace(typed)) return typed.Trim();
        return await AgentSettings.LoadApiKeyAsync(_store, _protector);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        Show("Testing...", muted: true);
        try
        {
            var key = await EffectiveKeyAsync();
            if (key is null) { Show("Enter an API key first.", error: true); return; }
            await ClaudeTriageAgent.TestAsync(key, ModelBox.Text.Trim());
            Show($"Key works and model '{ModelBox.Text.Trim()}' is available.");
        }
        catch (Exception ex)
        {
            Show($"Test failed: {ex.Message}", error: true);
        }
        finally { TestButton.IsEnabled = true; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            _settings.UseClaude = ClaudeRadio.IsChecked == true;
            _settings.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? AgentSettings.DefaultModel : ModelBox.Text.Trim();
            _settings.SharePreviews = PreviewsBox.IsChecked == true;
            await AgentSettings.SaveAsync(_store, _settings);
            if (!string.IsNullOrWhiteSpace(KeyBox.Password))
                await AgentSettings.SaveApiKeyAsync(_store, _protector, KeyBox.Password);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Show($"Could not save: {ex.Message}", error: true);
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Show(string text, bool error = false, bool muted = false)
    {
        ResultText.Text = text;
        ResultText.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xE0, 0x1E, 0x5A))
            : muted ? (Brush)FindResource("MutedTextBrush") : new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0x5A));
    }
}
