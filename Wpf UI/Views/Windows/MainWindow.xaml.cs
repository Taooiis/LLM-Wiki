using System.Windows.Input;

using LLM_Wiki.Services;
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

        /// <summary>״̬������ˢ�£�����ʵ�������ԣ���α��״̬������ĵ� 00��2����</summary>
        private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

        public MainWindow(
            MainWindowViewModel viewModel,
            INavigationViewPageProvider navigationViewPageProvider,
            INavigationService navigationService,
            ISnackbarService snackbarService,
            LLM_Wiki.Services.ConfigService configService
        )
        {
            ViewModel = viewModel;
            _snackbarService = snackbarService;
            _navigationService = navigationService;
            DataContext = this;

            // ����Ӧ���Ѽ�ס�����⣨Ĭ�� Dark������ҳ�л���д�� config.json��
            AppThemeService.Apply(configService.Theme);

            InitializeComponent();
            SetPageService(navigationViewPageProvider);

            navigationService.SetNavigationControl(RootNavigation);

            // ���� SnackbarPresenter ��ȫ�� toast ʹ��
            _snackbarService.SetSnackbarPresenter(SnackbarPresenter);

            // ״̬�� 2 ����ѯˢ�£���ȡ CurrentKb / WikisDir / ShareService.Running ��ʵֵ��
            _statusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _statusTimer.Tick += (_, _) => ViewModel.RefreshStatusBar();
            _statusTimer.Start();
            ViewModel.RefreshStatusBar();
        }

        /// <summary>Ctrl+K �۽�ȫ������������ĵ� 07��6����</summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                GlobalSearch.Focus();
                e.Handled = true;
            }
        }

        /// <summary>�����س��������Ƶ�����ƥ��ҳ�棨������/ҳ����ڣ�����ȫ����������</summary>
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

        /// <summary>������ݰ�ť �� С��ָ�ϡ�</summary>
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
