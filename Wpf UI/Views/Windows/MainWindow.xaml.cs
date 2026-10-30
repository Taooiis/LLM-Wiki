using System.Windows.Input;

using LLM_Wiki.ViewModels.Windows;

using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Windows
{
    public partial class MainWindow : INavigationWindow
    {
        public MainWindowViewModel ViewModel { get; }
        private readonly ISnackbarService _snackbarService;
        private readonly INavigationService _navigationService;

        /// <summary>状态栏轻量刷新：读真实服务属性，不伪造状态（设计文档 00§2）。</summary>
        private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

        public MainWindow(
            MainWindowViewModel viewModel,
            INavigationViewPageProvider navigationViewPageProvider,
            INavigationService navigationService,
            ISnackbarService snackbarService
        )
        {
            ViewModel = viewModel;
            _snackbarService = snackbarService;
            _navigationService = navigationService;
            DataContext = this;

            // 启动固定深色主题（设计文档 06§5：默认 Dark，设置页可切换）
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);

            InitializeComponent();
            SetPageService(navigationViewPageProvider);

            navigationService.SetNavigationControl(RootNavigation);

            // 设置 SnackbarPresenter 供全局 toast 使用
            _snackbarService.SetSnackbarPresenter(SnackbarPresenter);

            // 状态栏 2 秒轮询刷新（读取 CurrentKb / WikisDir / ShareService.Running 真实值）
            _statusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _statusTimer.Tick += (_, _) => ViewModel.RefreshStatusBar();
            _statusTimer.Start();
            ViewModel.RefreshStatusBar();
        }

        /// <summary>Ctrl+K 聚焦全局搜索（设计文档 07§6）。</summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                GlobalSearch.Focus();
                e.Handled = true;
            }
        }

        /// <summary>搜索回车：按名称导航到匹配页面（仅功能/页面入口，不做全文搜索）。</summary>
        private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var query = args.QueryText?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                return;
            }

            ViewModel.NavigateToNamedPage(query, _navigationService);
            GlobalSearch.Text = string.Empty;
        }

        /// <summary>顶栏快捷按钮 → 小白指南。</summary>
        private void OnGuideClick(object sender, RoutedEventArgs e)
        {
            _navigationService.Navigate(typeof(Views.Pages.GuidePage));
        }

        #region INavigationWindow methods

        public INavigationView GetNavigation() => RootNavigation;

        public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

        public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) => RootNavigation.SetPageProviderService(navigationViewPageProvider);

        public void ShowWindow() => Show();

        public void CloseWindow() => Close();

        private IServiceProvider? _serviceProvider;

        public void SetServiceProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        #endregion INavigationWindow methods

        /// <summary>
        /// Raises the closed event.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            _statusTimer.Stop();

            // Make sure that closing this window will begin the process of closing the application.
            Application.Current.Shutdown();
        }
    }
}
