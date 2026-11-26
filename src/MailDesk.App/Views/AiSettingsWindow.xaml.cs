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
        LocalRadio.IsChecked = _settings.Provider == AgentProvider.Heuristic;
        ClaudeRadio.IsChecked = _settings.Provider == AgentProvider.Claude;
        OpenAIRadio.IsChecked = _settings.Provider == AgentProvider.OpenAI;
        ClaudeModelBox.Text = _settings.ClaudeModel;
        OpenAIModelBox.Text = _settings.OpenAIModel;
        PreviewsBox.IsChecked = _settings.SharePreviews;
        ClaudeKeyStatus.Text = KeyStatus(_settings.HasAnthropicKey, "ANTHROPIC_API_KEY");
        OpenAIKeyStatus.Text = KeyStatus(_settings.HasOpenAIKey, "OPENAI_API_KEY");
    }

    private static string KeyStatus(bool saved, string envVar)
    {
        if (saved) return "A key is saved.";
        var env = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar));
        return env ? $"No saved key; {envVar} from the environment will be used." : "No key saved.";
    }

    private async void ClaudeTest_Click(object sender, RoutedEventArgs e)
        => await TestAsync(ClaudeTestButton, ClaudeKeyBox.Password, AgentSettings.KeyAnthropicApiKey, "ANTHROPIC_API_KEY", ClaudeModelBox.Text, ClaudeTriageAgent.TestAsync);

    private async void OpenAITest_Click(object sender, RoutedEventArgs e)
        => await TestAsync(OpenAITestButton, OpenAIKeyBox.Password, AgentSettings.KeyOpenAIApiKey, "OPENAI_API_KEY", OpenAIModelBox.Text, OpenAITriageAgent.TestAsync);

    private async Task TestAsync(System.Windows.Controls.Button button, string typedKey, string settingKey, string envVar, string model, Func<string, string, CancellationToken, Task> test)
    {
        button.IsEnabled = false;
        Show("Testing...", muted: true);
        try
        {
            var key = !string.IsNullOrWhiteSpace(typedKey) ? typedKey.Trim() : await AgentSettings.LoadApiKeyAsync(_store, _protector, settingKey, envVar);
            if (key is null) { Show("Enter an API key first.", error: true); return; }
            await test(key, model.Trim(), CancellationToken.None);
            Show($"Key works and model '{model.Trim()}' is available.");
        }
        catch (Exception ex)
        {
            Show($"Test failed: {ex.Message}", error: true);
        }
        finally { button.IsEnabled = true; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            _settings.Provider = ClaudeRadio.IsChecked == true ? AgentProvider.Claude
                : OpenAIRadio.IsChecked == true ? AgentProvider.OpenAI : AgentProvider.Heuristic;
            _settings.ClaudeModel = ClaudeModelBox.Text;
            _settings.OpenAIModel = OpenAIModelBox.Text;
            _settings.SharePreviews = PreviewsBox.IsChecked == true;
            await AgentSettings.SaveAsync(_store, _settings);
            if (!string.IsNullOrWhiteSpace(ClaudeKeyBox.Password))
                await AgentSettings.SaveApiKeyAsync(_store, _protector, AgentSettings.KeyAnthropicApiKey, ClaudeKeyBox.Password);
            if (!string.IsNullOrWhiteSpace(OpenAIKeyBox.Password))
                await AgentSettings.SaveApiKeyAsync(_store, _protector, AgentSettings.KeyOpenAIApiKey, OpenAIKeyBox.Password);
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
