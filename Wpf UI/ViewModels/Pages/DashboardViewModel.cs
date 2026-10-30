using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

using Wpf.Ui;

namespace LLM_Wiki.ViewModels.Pages
{
    /// <summary>首页（知识库总览）：统计卡 + 我的知识库 + 待处理变更 + 快捷入口 + 存储状态（设计文档 01）。</summary>
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly KbService _kbService;
        private readonly FileBridge _fileBridge;
        private readonly ConfigService _configService;
        private readonly ToastService _toastService;
        private readonly MainWindowViewModel _main;
        private readonly INavigationService _navigationService;

        public DashboardViewModel(
            KbService kbService,
            FileBridge fileBridge,
            ConfigService configService,
            ToastService toastService,
            MainWindowViewModel mainViewModel,
            INavigationService navigationService)
        {
            _kbService = kbService;
            _fileBridge = fileBridge;
            _configService = configService;
            _toastService = toastService;
            _main = mainViewModel;
            _navigationService = navigationService;

            LoadOverview();
            // 启动即选中首个库（对齐 Demo 默认选中行为），各页面随 CurrentKb 联动刷新
            if (KbItems.Count > 0 && string.IsNullOrEmpty(SelectedKb))
            {
                SelectedKb = KbItems[0].Name;
            }
        }

        /// <summary>知识库列表项。系统无"最近访问"记录，展示真实创建时间（目录创建时间），不伪造访问时间。</summary>
        public record KbItem(string Name, int SourceCount, string CreatedText);

        /// <summary>待处理变更展示行（最多最近 5 条，设计文档 01§5）。</summary>
        public record PendingChangeRow(
            string FileName, string ActionLabel, string TimeText,
            bool IsAdd, bool IsModify, bool IsDelete);

        #region 知识库列表与选中

        [ObservableProperty]
        private ObservableCollection<KbItem> _kbItems = new();

        /// <summary>搜索过滤后的视图集合：即时本地过滤，不回读磁盘。</summary>
        public ObservableCollection<KbItem> KbsView { get; } = new();

        [ObservableProperty]
        private string _selectedKb = "";

        [ObservableProperty]
        private string _kbSearchText = "";

        /// <summary>当前库根目录完整路径（显示用）。</summary>
        [ObservableProperty]
        private string _currentKbPath = "";

        partial void OnKbSearchTextChanged(string value) => ApplyKbFilter();

        private void ApplyKbFilter()
        {
            KbsView.Clear();
            foreach (var k in KbItems)
            {
                if (string.IsNullOrWhiteSpace(KbSearchText) || k.Name.Contains(KbSearchText, StringComparison.OrdinalIgnoreCase))
                {
                    KbsView.Add(k);
                }
            }
        }

        /// <summary>选中库变化：写入全局 CurrentKb（其他页面据此刷新）、更新路径并加载该库统计。</summary>
        partial void OnSelectedKbChanged(string value)
        {
            _main.CurrentKb = value ?? "";
            CurrentKbPath = string.IsNullOrWhiteSpace(value) ? "" : _fileBridge.KbRoot(value);
            LoadSelectedStats();
        }

        #endregion

        #region 统计卡（设计文档 01§3）

        [ObservableProperty]
        private int _kbCount;

        [ObservableProperty]
        private int _sourceDocCount;

        [ObservableProperty]
        private int _pendingChangeCount;

        /// <summary>存储状态：来自真实目录校验（存在=正常），无法验证时如实显示。</summary>
        [ObservableProperty]
        private string _storageStatusText = "未验证";

        /// <summary>存储路径显示（超长时 UI 省略中段，Tooltip 悬停显示完整）。</summary>
        [ObservableProperty]
        private string _storagePathDisplay = "";

        public bool StorageIsOk => StorageStatusText == "正常";

        partial void OnStorageStatusTextChanged(string value)
            => OnPropertyChanged(nameof(StorageIsOk));

        #endregion

        #region 待处理变更（设计文档 01§5）

        [ObservableProperty]
        private ObservableCollection<PendingChangeRow> _recentChanges = new();

        #endregion

        #region 内联新建知识库（设计文档 01§7：优先内联创建区）

        [ObservableProperty]
        private bool _isCreatingKb;

        [ObservableProperty]
        private string _newKbName = "";

        [ObservableProperty]
        private string _newKbError = "";

        /// <summary>输入时即时校验库名：非法字符直接显示错误，不关闭创建区。</summary>
        partial void OnNewKbNameChanged(string value)
        {
            NewKbError = string.IsNullOrWhiteSpace(value) || PathGuard.ValidKb(value.Trim())
                ? ""
                : "仅允许中文、英文、数字、下划线、中划线";
        }

        #endregion

        #region 数据加载

        /// <summary>加载知识库总览：列表 + 每库统计 + 存储状态。本地场景库数量为个位数，逐库取数可接受。</summary>
        public void LoadOverview()
        {
            var prev = SelectedKb;

            KbItems.Clear();
            foreach (var kb in _kbService.ListKbs())
            {
                var root = _fileBridge.KbRoot(kb);
                var created = Directory.Exists(root)
                    ? Directory.GetCreationTime(root).ToString("yyyy-MM-dd")
                    : "";
                KbItems.Add(new KbItem(kb, _fileBridge.Sources(kb).Length, created));
            }
            ApplyKbFilter();

            // Clear 会触发 TwoWay 绑定清空选中：重填后显式恢复原选中，避免全页掉回未选态
            var target = !string.IsNullOrEmpty(prev) && KbItems.Any(k => k.Name == prev)
                ? prev
                : KbItems.Count > 0 ? KbItems[0].Name : "";
            SelectedKb = target;
            KbCount = KbItems.Count;

            StoragePathDisplay = _configService.WikisDir;
            StorageStatusText = string.IsNullOrWhiteSpace(_configService.WikisDir)
                ? "未配置"
                : Directory.Exists(_configService.WikisDir) ? "正常" : "目录不存在";
        }

        /// <summary>加载选中库的统计与最近待处理变更（最多 5 条）。</summary>
        public void LoadSelectedStats()
        {
            SourceDocCount = 0;
            PendingChangeCount = 0;
            RecentChanges.Clear();

            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                return;
            }

            SourceDocCount = _fileBridge.Sources(SelectedKb).Length;

            var changes = _fileBridge.Changes(SelectedKb);
            PendingChangeCount = changes.Count;
            foreach (var c in changes.Take(5))
            {
                var (label, isAdd, isModify, isDelete) = MapAction(c.Action);
                RecentChanges.Add(new PendingChangeRow(
                    Path.GetFileName(c.File), label, c.Time, isAdd, isModify, isDelete));
            }
        }

        /// <summary>变更动作映射：现有数据只有"编辑/删除"两种，如实映射为徽标文案，不虚构"新增"。</summary>
        private static (string Label, bool IsAdd, bool IsModify, bool IsDelete) MapAction(string action) => action switch
        {
            "编辑" => ("修改", false, true, false),
            "删除" => ("删除", false, false, true),
            "新增" or "add" => ("新增", true, false, false),
            _ => (string.IsNullOrWhiteSpace(action) ? "变更" : action, false, false, false),
        };

        #endregion

        #region 命令

        [RelayCommand]
        private void StartCreateKb()
        {
            IsCreatingKb = true;
            NewKbName = "";
            NewKbError = "";
        }

        [RelayCommand]
        private void CancelCreateKb() => IsCreatingKb = false;

        [RelayCommand]
        private void CreateKb()
        {
            var name = NewKbName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                NewKbError = "请输入知识库名称";
                return;
            }
            if (!PathGuard.ValidKb(name))
            {
                NewKbError = "仅允许中文、英文、数字、下划线、中划线";
                return;
            }

            // CreateKb 幂等（重名=补骨架，数据无损）：创建前先查是否已存在，如实区分提示
            var existed = _kbService.ListKbs().Contains(name);
            try
            {
                _kbService.CreateKb(name);
            }
            catch (ArgumentException)
            {
                NewKbError = "知识库名不合法";
                return;
            }
            catch (Exception ex)
            {
                NewKbError = "建库失败：" + ex.Message;
                return;
            }

            LoadOverview();
            // 建库成功后自动选中新库，全页随 CurrentKb 联动刷新
            SelectedKb = name;
            IsCreatingKb = false;
            _toastService.ShowSuccess(existed ? "库已存在，已为你选中：" + name : "已创建知识库：" + name);
        }

        /// <summary>删除知识库（确认弹窗在 code-behind，设计文档 07§3）。</summary>
        [RelayCommand]
        private void DeleteKb(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _toastService.ShowWarning("请先选择要删除的知识库");
                return;
            }
            if (!PathGuard.ValidKb(name))
            {
                _toastService.ShowError("知识库名非法：仅允许中文、英文、数字、下划线、中划线");
                return;
            }

            var (ok, error) = _kbService.ValidateDelete(name);
            if (!ok)
            {
                _toastService.ShowError(error);
                return;
            }

            _kbService.DeleteKb(name);
            LoadOverview();
            _toastService.ShowSuccess($"已删除知识库：{name}");
        }

        /// <summary>双击知识库卡片或点击"打开" → 进入源文档页（设计文档 01§4）。</summary>
        [RelayCommand]
        private void OpenSelectedKb() => NavigateTo(typeof(Views.Pages.SourcePage));

        // 快捷入口与统计卡跳转（设计文档 01§3、§6：直接进入对应页面，不再弹"请选择操作"）
        [RelayCommand]
        private void GoSource() => NavigateTo(typeof(Views.Pages.SourcePage));

        [RelayCommand]
        private void GoTree() => NavigateTo(typeof(Views.Pages.TreeBrowserPage));

        [RelayCommand]
        private void GoWorkflow() => NavigateTo(typeof(Views.Pages.WorkflowPage));

        [RelayCommand]
        private void GoConfig() => NavigateTo(typeof(Views.Pages.ConfigPage));

        [RelayCommand]
        private void GoPending() => NavigateTo(typeof(Views.Pages.WorkflowPage));

        [RelayCommand]
        private void GoIngest() => NavigateTo(typeof(Views.Pages.WorkflowPage));

        [RelayCommand]
        private void GoLint() => NavigateTo(typeof(Views.Pages.WorkflowPage));

        /// <summary>在资源管理器中打开存储目录（仅目录真实存在时执行）。</summary>
        [RelayCommand]
        private void OpenStorageFolder()
        {
            var dir = _configService.WikisDir;
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }

        /// <summary>刷新总览（保留当前选中，各页数据继续联动）。</summary>
        [RelayCommand]
        private void Refresh() => LoadOverview();

        private void NavigateTo(Type pageType) => _navigationService.Navigate(pageType);

        #endregion
    }
}
