using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Wpf.Ui.Controls;

namespace LLM_Wiki.ViewModels.Windows
{
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _applicationTitle = "LLM_Wiki";

        [ObservableProperty]
        private ObservableCollection<object> _menuItems = new()
        {
            new NavigationViewItem()
            {
                Content = "Home",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(Views.Pages.DashboardPage)
            },
            //new NavigationViewItem()
            //{
            //    Content = "Data",
            //    Icon = new SymbolIcon { Symbol = SymbolRegular.DataHistogram24 },
            //    TargetPageType = typeof(Views.Pages.DataPage)
            //},
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

        [ObservableProperty]
        private ObservableCollection<object> _footerMenuItems = new()
        {
            new NavigationViewItem()
            {
                Content = "Settings",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.Pages.SettingsPage)
            },
            new NavigationViewItem()
            {
                Content = "配置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.Pages.ConfigPage)
            },
            new NavigationViewItem()
            {
                Content = "小白指南",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Info24 },
                TargetPageType = typeof(Views.Pages.GuidePage)
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

        /// <summary>从源文档页“待处理变更”点击“生成 Ingest”后，传递给工作流页的文件列表。</summary>
        [ObservableProperty]
        private ObservableCollection<string> _pendingIngestFiles = new();

        /// <summary>对应的知识库名。</summary>
        [ObservableProperty]
        private string _pendingIngestKb = "";
    }
}
