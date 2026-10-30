using System.Collections.ObjectModel;

using LLM_Wiki.Services;
using LLM_Wiki.Share;

using CommunityToolkit.Mvvm.ComponentModel;

using Wpf.Ui;
using Wpf.Ui.Controls;

namespace LLM_Wiki.ViewModels.Windows
{
    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly ConfigService _config;
        private readonly ShareService _share;

        public MainWindowViewModel(ConfigService config, ShareService share)
        {
            _config = config;
            _share = share;

            // 状态栏版本号来自程序集，不硬编码
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            AppVersion = version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        [ObservableProperty]
        private string _applicationTitle = "LLM Wiki";

        [ObservableProperty]
        private ObservableCollection<object> _menuItems = new()
        {
            new NavigationViewItem()
            {
                Content = "首页",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(Views.Pages.DashboardPage)
            },
            new NavigationViewItem()
            {
                Content = "源文档",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Document24 },
                TargetPageType = typeof(Views.Pages.SourcePage)
            },
            new NavigationViewItem()
            {
                Content = "目录浏览",
                Icon = new SymbolIcon { Symbol = SymbolRegular.List24 },
                TargetPageType = typeof(Views.Pages.TreeBrowserPage)
            },
            new NavigationViewItem()
            {
                Content = "工作流",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Flow24 },
                TargetPageType = typeof(Views.Pages.WorkflowPage)
            },
            new NavigationViewItem()
            {
                Content = "分享",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Share24 },
                TargetPageType = typeof(Views.Pages.SharePage)
            }
        };

        // 底部导航固定顺序：小白指南、配置、设置（设计文档 00§3）
        [ObservableProperty]
        private ObservableCollection<object> _footerMenuItems = new()
        {
            new NavigationViewItem()
            {
                Content = "小白指南",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Info24 },
                TargetPageType = typeof(Views.Pages.GuidePage)
            },
            new NavigationViewItem()
            {
                Content = "配置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.Pages.ConfigPage)
            },
            new NavigationViewItem()
            {
                Content = "设置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.Pages.SettingsPage)
            }
        };

        [ObservableProperty]
        private ObservableCollection<MenuItem> _trayMenuItems = new()
        {
            new MenuItem { Header = "Home", Tag = "tray_home" }
        };

        /// <summary>当前选中的知识库名（供源文档页等页面读取）。</summary>
        [ObservableProperty]
        private string _currentKb = "";

        /// <summary>从源文档页“待处理变更”点击“生成 Ingest”后，传递给工作流页的文件清单（逗号分隔，含 raw/sources/ 前缀）。</summary>
        [ObservableProperty]
        private string _pendingIngestFiles = "";

        /// <summary>对应的知识库名。</summary>
        [ObservableProperty]
        private string _pendingIngestKb = "";

        #region 状态栏（设计文档 00§2：当前库 / 存储 / 分享服务 / 版本）

        [ObservableProperty]
        private string _statusKb = "未选择";

        [ObservableProperty]
        private string _statusStorage = "";

        [ObservableProperty]
        private string _statusShare = "已停止";

        [ObservableProperty]
        private string _appVersion = "";

        /// <summary>从真实服务读取状态，不缓存不伪造；由主窗口定时调用。</summary>
        public void RefreshStatusBar()
        {
            StatusKb = string.IsNullOrWhiteSpace(CurrentKb) ? "未选择" : CurrentKb;
            StatusStorage = _config.WikisDir;
            StatusShare = _share.Running ? $"运行中 · 端口 {_share.Port}" : "已停止";
        }

        /// <summary>当前库变化时状态栏即时跟随。</summary>
        partial void OnCurrentKbChanged(string value) => RefreshStatusBar();

        #endregion

        /// <summary>
        /// 全局搜索回车后按名称导航（仅功能/页面入口，不做全文搜索——设计文档 00§4）。
        /// </summary>
        public void NavigateToNamedPage(string query, INavigationService navigationService)
        {
            var map = new (string[] Keys, Type Page)[]
            {
                (new[] { "首页", "主页", "home" }, typeof(Views.Pages.DashboardPage)),
                (new[] { "源文档", "文档", "上传" }, typeof(Views.Pages.SourcePage)),
                (new[] { "目录浏览", "目录", "wiki" }, typeof(Views.Pages.TreeBrowserPage)),
                (new[] { "工作流", "提示词", "ingest" }, typeof(Views.Pages.WorkflowPage)),
                (new[] { "分享", "share" }, typeof(Views.Pages.SharePage)),
                (new[] { "配置", "存储", "路径" }, typeof(Views.Pages.ConfigPage)),
                (new[] { "设置", "主题", "版本" }, typeof(Views.Pages.SettingsPage)),
                (new[] { "小白指南", "指南", "帮助", "guide" }, typeof(Views.Pages.GuidePage)),
            };

            foreach (var (keys, page) in map)
            {
                foreach (var key in keys)
                {
                    if (query.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        navigationService.Navigate(page);
                        return;
                    }
                }
            }
        }
    }
}
