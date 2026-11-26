using System.IO;
using System.Windows;
using MailDesk.Agent;
using MailDesk.App.Services;
using MailDesk.App.ViewModels;
using MailDesk.Core.Security;
using MailDesk.Core.Services;
using MailDesk.Core.Storage;
using MailDesk.Mail;
using Microsoft.Extensions.DependencyInjection;

namespace MailDesk.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "MailDesk: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailDesk");
        Directory.CreateDirectory(dataDir);

        var services = new ServiceCollection();
        services.AddSingleton<IMailStore>(new SqliteMailStore(Path.Combine(dataDir, "maildesk.db")));
        services.AddSingleton<ICredentialProtector, DpapiCredentialProtector>();
        services.AddSingleton<IMailSync, ImapMailSync>();
        services.AddSingleton<IMailSender, MailKitSender>();
        services.AddSingleton<HeuristicTriageAgent>();
        services.AddSingleton<ClaudeTriageAgent>();
        services.AddSingleton<OpenAITriageAgent>();
        services.AddSingleton<IAgentService, SwitchingAgent>();
        services.AddSingleton<ProposalApplier>();
        services.AddSingleton<MailDesk.Core.Rules.RuleRunner>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();

        try
        {
            await Services.GetRequiredService<IMailStore>().InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the local database:\n{ex.Message}", "MailDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        await Services.GetRequiredService<MainViewModel>().LoadAsync();
    }
}
