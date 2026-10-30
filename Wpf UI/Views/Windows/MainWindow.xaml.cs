using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels;
using LLM_Wiki.ViewModels.Windows;
using LLM_Wiki.Views.Dialogs;
using LLM_Wiki.Views.Pages;

using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Windows;

public partial class MainWindow : INavigationWindow
{
    public MainWindowViewModel ViewModel { get; }

    private readonly ISnackbarService _snackbarService;
    private readonly INavigationService _navigationService;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    private bool _closeConfirmed;
    private bool _syncingSearch;
    private bool _allowNavigation;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationViewPageProvider navigationViewPageProvider,
        INavigationService navigationService,
        ISnackbarService snackbarService,
        ConfigService configService)
    {
        ViewModel = viewModel;
        _snackbarService = snackbarService;
        _navigationService = navigationService;
        DataContext = this;

        AppThemeService.Apply(configService.Theme);

        InitializeComponent();
        SetPageService(navigationViewPageProvider);
        navigationService.SetNavigationControl(RootNavigation);
        _snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        if (App.Services.GetService(typeof(ToastService)) is ToastService toast) toast.SetPresenter(SnackbarPresenter);
        ViewModel.ConfirmExit = ConfirmExitAsync;

        Closing += OnWindowClosing;

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => ViewModel.RefreshStatusBar();
        _statusTimer.Start();
        ViewModel.RefreshStatusBar();

        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(ViewModel.StartupPageType);
        };
    }

    private void OnKbPickerClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = KbPickerButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var kb in ViewModel.Kbs)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = kb,
                IsCheckable = true,
                IsChecked = kb.Equals(ViewModel.CurrentKb, StringComparison.Ordinal),
            };
            var name = kb;
            item.Click += async (_, _) => await ViewModel.SwitchKbAsync(name);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new System.Windows.Controls.Separator());
        var create = new System.Windows.Controls.MenuItem { Header = "新建知识库…" };
        create.Click += async (_, _) => await ShowCreateKbAsync();
        menu.Items.Add(create);
        var manage = new System.Windows.Controls.MenuItem { Header = "管理当前知识库" };
        manage.Click += (_, _) => ViewModel.NavigateTo("kbsettings");
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    public async Task ShowCreateKbAsync()
    {
        // D01（00§9.4）：失败留框保留输入；重名就地提示并提供“打开已有知识库”。
        var dialog = new CreateKbDialog(
            ViewModel.Kbs,
            (name, scene, purpose) => ViewModel.CreateKbAsync(name, scene, purpose))
        { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.OpenExisting)
        {
            await ViewModel.SwitchKbAsync(dialog.KbName);
        }
    }

    /// <summary>D13 退出保护确认框（00§9.4）。</summary>
    private async Task<ExitChoice> ConfirmExitAsync(bool hasDrafts)
    {
        var dlg = new UnsavedChangesDialog();
        var detail = "库：" + (string.IsNullOrEmpty(ViewModel.CurrentKb) ? "（无）" : ViewModel.CurrentKb);
        if (!hasDrafts)
        {
            dlg.ConfigureExit(
                title: "任务仍在运行",
                lead: "本机整理任务尚未结束；退出应用前请先处理。",
                detail: detail,
                secondary: "取消任务后退出",
                primary: "",
                close: "留在应用");
            var choice = await dlg.ShowChoiceAsync(null);
            return choice switch
            {
                UnsavedChoice.Discard => ExitChoice.CancelTaskAndExit,
                UnsavedChoice.SaveAndLeave => ExitChoice.SaveAndCancelTaskAndExit,
                _ => ExitChoice.Stay,
            };
        }

        dlg.ConfigureExit(
            title: "任务仍在运行，还有未保存的更改",
            lead: "退出前，请处理运行中的本机任务和未保存的文件。",
            detail: detail,
            secondary: "放弃草稿并取消任务后退出",
            primary: "保存并取消任务后退出",
            close: "留在应用");
        var combined = await dlg.ShowChoiceAsync(null);
        return combined switch
        {
            UnsavedChoice.Discard => ExitChoice.DiscardAndCancelTaskAndExit,
            UnsavedChoice.SaveAndLeave => ExitChoice.SaveAndCancelTaskAndExit,
            _ => ExitChoice.Stay,
        };
    }

    private async void OnHelpClick(object sender, RoutedEventArgs e) => await ViewModel.OpenHelpAsync("quick-start");

    private void OnFixStorageClick(object sender, RoutedEventArgs e) => ViewModel.NavigateTo("settings");

    private void OnStatusTaskClick(object sender, RoutedEventArgs e) => ViewModel.NavigateTo("organize");

    private void OnTopSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel.SearchQuery = TopSearchBox.Text;
            ViewModel.OpenSearchCommand.Execute(null);
            FocusSearchOverlay();
            e.Handled = true;
        }
    }

    private void OnTopSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSearch) return;
        if (string.IsNullOrWhiteSpace(TopSearchBox.Text)) return;
        if (!ViewModel.IsSearchOpen)
        {
            ViewModel.OpenSearchCommand.Execute(null);
            FocusSearchOverlay();
            _syncingSearch = true;
            SearchOverlayBox.Text = TopSearchBox.Text;
            _syncingSearch = false;
        }
    }

    private void FocusSearchOverlay()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchOverlayBox.Focus();
            SearchOverlayBox.CaretIndex = SearchOverlayBox.Text.Length;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnSearchOverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel.CloseSearchCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down)
        {
            if (SearchResultList.Items.Count > 0)
                SearchResultList.SelectedIndex = Math.Min(SearchResultList.Items.Count - 1, SearchResultList.SelectedIndex + 1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up)
        {
            if (SearchResultList.Items.Count > 0)
                SearchResultList.SelectedIndex = Math.Max(0, SearchResultList.SelectedIndex - 1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            OpenSelectedSearchResult();
            e.Handled = true;
        }
    }

    private void OnSearchResultDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedSearchResult();

    private void OpenSelectedSearchResult()
    {
        var result = SearchResultList.SelectedItem as FileSearchResult;
        if (result == null && SearchResultList.Items.Count > 0)
            result = SearchResultList.Items[0] as FileSearchResult;
        if (result == null) return;
        ViewModel.OpenResult(result);
        TopSearchBox.Text = "";
    }

    private void OnCloseSearchClick(object sender, RoutedEventArgs e) => ViewModel.CloseSearchCommand.Execute(null);

    /// <summary>导航保护：切页前统一处理所有未保存草稿，取消则不离开。</summary>
    private async void OnRootNavigating(NavigationView sender, NavigatingCancelEventArgs e)
    {
        if (_allowNavigation) return;
        e.Cancel = true;
        var target = e.Page as Type;
        if (target == null && e.Page is NavigationViewItem item) target = item.TargetPageType;
        if (target == null && e.Page != null) target = e.Page.GetType();
        if (target == null) return;
        if (!await ViewModel.CanCloseAsync()) return;

        _allowNavigation = true;
        try { sender.Navigate(target); }
        finally
        {
            _ = Dispatcher.BeginInvoke(new Action(() => _allowNavigation = false));
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ViewModel.OpenSearchCommand.Execute(null);
            FocusSearchOverlay();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.K && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ViewModel.NavigateTo("kbsettings");
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ViewModel.IsSearchOpen)
        {
            ViewModel.CloseSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;
        // D13：草稿与本机任务合并退出保护（00§9.4）。
        if (!await ViewModel.CanExitAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    #region INavigationWindow

    public INavigationView GetNavigation() => RootNavigation;
    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);
    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) => RootNavigation.SetPageProviderService(navigationViewPageProvider);
    public void ShowWindow() => Show();
    public void CloseWindow() => Close();

    public void SetServiceProvider(IServiceProvider serviceProvider) { }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
