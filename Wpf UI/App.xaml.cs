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

namespace LLM_Wiki
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        // The.NET Generic Host provides dependency injection, configuration, logging, and other services.
        // https://docs.microsoft.com/dotnet/core/extensions/generic-host
        // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
        // https://docs.microsoft.com/dotnet/core/extensions/configuration
        // https://docs.microsoft.com/dotnet/core/extensions/logging
        private static readonly IHost _host = Host
            .CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => { c.SetBasePath(Path.GetDirectoryName(AppContext.BaseDirectory)); })
            .ConfigureServices((context, services) =>
            {
                services.AddNavigationViewPageProvider();

                services.AddHostedService<ApplicationHostService>();

                // Theme manipulation
                services.AddSingleton<IThemeService, ThemeService>();

                // TaskBar manipulation
                services.AddSingleton<ITaskBarService, TaskBarService>();

                // Service containing navigation, same as INavigationWindow... but without window
                services.AddSingleton<INavigationService, NavigationService>();

                // Main window with navigation
                services.AddSingleton<INavigationWindow, MainWindow>();
                services.AddSingleton<MainWindowViewModel>();

                services.AddSingleton<DashboardPage>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<DataPage>();
                services.AddSingleton<DataViewModel>();
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<SourcePage>();
                services.AddSingleton<SourceViewModel>();
                services.AddSingleton<ConfigPage>();
                services.AddSingleton<ConfigViewModel>();
                services.AddSingleton<TreeBrowserPage>();
                services.AddSingleton<TreeBrowserViewModel>();
                services.AddSingleton<WorkflowPage>();
                services.AddSingleton<WorkflowViewModel>();
                services.AddSingleton<SharePage>();
                services.AddSingleton<ShareViewModel>();
                services.AddSingleton<ShareService>();
                services.AddSingleton<GuidePage>();
                services.AddSingleton<GuideViewModel>();

                // 功能迁移：本地服务（无 WebView2，Wpf UI 项目独立）
                services.AddSingleton<ConfigService>();
                services.AddSingleton<FileBridge>(sp => new FileBridge(Path.Combine(AppContext.BaseDirectory, "wikis")));
                services.AddSingleton<KbService>(sp => new KbService(Path.Combine(AppContext.BaseDirectory, "wikis")));
            }).Build();

        /// <summary>
        /// Gets services.
        /// </summary>
        public static IServiceProvider Services
        {
            get { return _host.Services; }
        }

        /// <summary>
        /// Occurs when the application is loading.
        /// </summary>
        private async void OnStartup(object sender, StartupEventArgs e)
        {
            await _host.StartAsync();
        }

        /// <summary>
        /// Occurs when the application is closing.
        /// </summary>
        private async void OnExit(object sender, ExitEventArgs e)
        {
            await _host.StopAsync();

            _host.Dispose();
        }

        /// <summary>
        /// Occurs when an exception is thrown by an application but not handled.
        /// </summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // For more info see https://docs.microsoft.com/en-us/dotnet/api/system.windows.application.dispatcherunhandledexception?view=windowsdesktop-6.0
        }
    }
}
