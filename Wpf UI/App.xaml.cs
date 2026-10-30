using System.IO;
using System.Windows.Threading;

using LLM_Wiki.Services;
using LLM_Wiki.Share;
using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.ViewModels.Windows;
using LLM_Wiki.Views.Pages;
using LLM_Wiki.Views.Windows;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace LLM_Wiki;

public partial class App
{
    private static readonly IHost _host = Host
        .CreateDefaultBuilder()
        .ConfigureAppConfiguration(c => { c.SetBasePath(Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory); })
        .ConfigureServices((context, services) =>
        {
            services.AddNavigationViewPageProvider();
            services.AddHostedService<ApplicationHostService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<ITaskBarService, TaskBarService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<INavigationWindow, MainWindow>();
            services.AddSingleton<MainWindowViewModel>();

            services.AddSingleton<ConfigService>(_ => new ConfigService(AppContext.BaseDirectory));
            services.AddSingleton<ChangeService>();
            services.AddSingleton<OperationStore>();
            services.AddSingleton<TrashService>();
            services.AddSingleton<ReviewService>();
            services.AddSingleton<ScanService>();
            services.AddSingleton<SourceService>();
            services.AddSingleton<RuleService>();
            services.AddSingleton<KnowledgeService>();
            services.AddSingleton<KbService>();
            services.AddSingleton<FileBridge>();
            services.AddSingleton<PromptComposer>();
            services.AddSingleton<TaskService>();
            services.AddSingleton<TaskExportService>();
            services.AddSingleton<UnsavedChangesCoordinator>();

            services.AddSingleton<ShareService>();
            services.AddSingleton<McpAccessPolicy>();
            services.AddSingleton<McpServerContext>();
            services.AddSingleton<McpService>();

            services.AddSingleton<ISnackbarService, SnackbarService>();
            services.AddSingleton<ToastService>();
            services.AddSingleton<UpdateService>();

            services.AddSingleton<KnowledgePage>();
            services.AddSingleton<KnowledgeViewModel>();
            services.AddSingleton<SourcePage>();
            services.AddSingleton<SourceViewModel>();
            services.AddSingleton<OrganizePage>();
            services.AddSingleton<OrganizeViewModel>();
            services.AddSingleton<ConnectionsPage>();
            services.AddSingleton<ConnectionsViewModel>();
            services.AddSingleton<KbSettingsPage>();
            services.AddSingleton<KbSettingsViewModel>();
            services.AddSingleton<AppSettingsPage>();
            services.AddSingleton<AppSettingsViewModel>();
            services.AddSingleton<HelpPage>();
        }).Build();

    public static IServiceProvider Services => _host.Services;

    private async void OnStartup(object sender, StartupEventArgs e) => await _host.StartAsync();

    private async void OnExit(object sender, ExitEventArgs e)
    {
        try { Services.GetService<ShareService>()?.Stop(); } catch { }
        try { Services.GetService<McpService>()?.Stop(); } catch { }
        await _host.StopAsync();
        _host.Dispose();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;
    }
}
