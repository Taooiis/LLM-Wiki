using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

using Markdig;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly KbService _kbService;
        private readonly FileBridge _fileBridge;
        private readonly ToastService _toastService;
        private readonly MainWindowViewModel _main;

        public DashboardViewModel(KbService kbService, FileBridge fileBridge, ToastService toastService, MainWindowViewModel mainViewModel)
        {
            _kbService = kbService;
            _fileBridge = fileBridge;
            _toastService = toastService;
            _main = mainViewModel;
            LoadKbs();
            // 启动即选中首个库（对齐 Demo 默认选中行为），各页面随 CurrentKb 联动刷新
            if (Kbs.Count > 0 && string.IsNullOrEmpty(SelectedKb))
            {
                SelectedKb = Kbs[0];
            }
        }

        [ObservableProperty]
        private ObservableCollection<string> _kbs = new();

        [ObservableProperty]
        private ObservableCollection<string> _files = new();

        [ObservableProperty]
        private string _selectedKb = "";

        [ObservableProperty]
        private string _selectedFile = "";

        [ObservableProperty]
        private string _markdownText = "";

        [ObservableProperty]
        private string _kbSearchText = "";

        [ObservableProperty]
        private string _fileSearchText = "";

        /// <summary>过滤后的视图集合：搜索即时本地过滤，不回读磁盘。</summary>
        public ObservableCollection<string> KbsView { get; } = new();

        public ObservableCollection<string> FilesView { get; } = new();

        partial void OnKbSearchTextChanged(string value) => ApplyKbFilter();

        partial void OnFileSearchTextChanged(string value) => ApplyFileFilter();

        private void ApplyKbFilter()
        {
            KbsView.Clear();
            foreach (var k in Kbs)
            {
                if (string.IsNullOrWhiteSpace(KbSearchText) || k.Contains(KbSearchText, StringComparison.OrdinalIgnoreCase))
                {
                    KbsView.Add(k);
                }
            }
        }

        private void ApplyFileFilter()
        {
            FilesView.Clear();
            foreach (var f in Files)
            {
                if (string.IsNullOrWhiteSpace(FileSearchText) || f.Contains(FileSearchText, StringComparison.OrdinalIgnoreCase))
                {
                    FilesView.Add(f);
                }
            }
        }

        /// <summary>当前库根目录完整路径（显示用，对齐 Demo 顶部路径行）。</summary>
        [ObservableProperty]
        private string _currentKbPath = "";

        // 统计卡数据（Admin Panel 风格顶栏）
        [ObservableProperty]
        private int _kbCount;

        [ObservableProperty]
        private int _fileCount;

        [ObservableProperty]
        private int _pendingChangeCount;

        /// <summary>选中库变化：写入全局 CurrentKb（其他页面据此刷新）并更新路径显示。</summary>
        partial void OnSelectedKbChanged(string value)
        {
            _main.CurrentKb = value ?? "";
            CurrentKbPath = string.IsNullOrWhiteSpace(value) ? "" : _fileBridge.KbRoot(value);
        }

        public void LoadKbs()
        {
            // Clear 会让 KbList 的 TwoWay 绑定把 SelectedKb 写成 null，进而清零 CurrentKb：
            // 先记住原选中，重填后显式恢复（否则改存储路径/删库后全页掉回未选态）
            var prev = SelectedKb;

            Kbs.Clear();
            foreach (var kb in _kbService.ListKbs())
            {
                Kbs.Add(kb);
            }
            ApplyKbFilter();

            var target = !string.IsNullOrEmpty(prev) && Kbs.Contains(prev)
                ? prev
                : Kbs.Count > 0 ? Kbs[0] : "";
            SelectedKb = target;
            KbCount = Kbs.Count;
        }

        public void LoadFiles(string kb)
        {
            Files.Clear();
            if (string.IsNullOrWhiteSpace(kb))
            {
                ApplyFileFilter();
                return;
            }

            foreach (var item in _fileBridge.Tree(kb))
            {
                Files.Add(item.Name);
            }
            ApplyFileFilter();
        }

        public void LoadFileContent(string kb, string file)
        {
            if (string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(file))
            {
                MarkdownText = string.Empty;
                return;
            }

            var error = "";
            var content = _fileBridge.ReadText(kb, file, out error);
            MarkdownText = content ?? string.Empty;
            SelectedFile = file;
        }

        [RelayCommand]
        private void SaveFile(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(SelectedFile))
            {
                _toastService.ShowWarning("请选择文件后再保存");
                return;
            }

            var error = "";
            if (!_fileBridge.WriteText(SelectedKb, SelectedFile, content, out error))
            {
                _toastService.ShowError("保存失败：" + error);
                return;
            }
            _toastService.ShowSuccess("已保存");
        }

        [RelayCommand]
        private void CreateKb(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            try
            {
                _kbService.CreateKb(name);
            }
            catch (ArgumentException)
            {
                _toastService.ShowError("知识库名只允许中文、英文、数字、下划线、中划线");
                return;
            }
            catch (Exception ex)
            {
                _toastService.ShowError("建库失败：" + ex.Message);
                return;
            }

            var existed = _kbService.ListKbs().Contains(name);
            LoadKbs();
            // 建库成功后自动选中新库（对齐 Demo），并随 OnSelectedKbChanged 联动 CurrentKb
            SelectedKb = name;
            // CreateKb 幂等（重名=补骨架，数据无损）：如实区分"新建"与"选中已有"，避免用户把内容误写进旧库
            _toastService.ShowSuccess(existed ? "库已存在，已为你选中：" + name : "已创建知识库：" + name);
        }

        [RelayCommand]
        private void DeleteKb(string name)
        {
            // 校验选中项
            if (string.IsNullOrWhiteSpace(name))
            {
                _toastService.ShowWarning("请先选择要删除的知识库");
                return;
            }

            // 校验库名合法性
            if (!PathGuard.ValidKb(name))
            {
                _toastService.ShowError("知识库名非法：仅允许中文、英文、数字、下划线、中划线");
                return;
            }

            // 校验库是否存在
            var (ok, error) = _kbService.ValidateDelete(name);
            if (!ok)
            {
                _toastService.ShowError(error);
                return;
            }

            _kbService.DeleteKb(name);
            LoadKbs();
            _toastService.ShowSuccess($"已删除知识库：{name}");
        }
    }
}