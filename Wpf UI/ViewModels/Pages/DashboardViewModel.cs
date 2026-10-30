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
        private string _searchText = "";

        /// <summary>当前库根目录完整路径（显示用，对齐 Demo 顶部路径行）。</summary>
        [ObservableProperty]
        private string _currentKbPath = "";

        /// <summary>选中库变化：写入全局 CurrentKb（其他页面据此刷新）并更新路径显示。</summary>
        partial void OnSelectedKbChanged(string value)
        {
            _main.CurrentKb = value ?? "";
            CurrentKbPath = string.IsNullOrWhiteSpace(value) ? "" : _fileBridge.KbRoot(value);
        }

        public void LoadKbs()
        {
            Kbs.Clear();
            foreach (var kb in _kbService.ListKbs())
            {
                Kbs.Add(kb);
            }
        }

        public void LoadFiles(string kb)
        {
            Files.Clear();
            if (string.IsNullOrWhiteSpace(kb))
            {
                return;
            }

            foreach (var item in _fileBridge.Tree(kb))
            {
                Files.Add(item.Name);
            }
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

        public void SearchFiles(string kb, string keyword)
        {
            Files.Clear();
            if (string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(keyword))
            {
                return;
            }

            foreach (var name in FlattenFiles(_fileBridge.Tree(kb), keyword))
            {
                Files.Add(name);
            }
        }

        private static IEnumerable<string> FlattenFiles(TreeItem[] nodes, string keyword)
        {
            foreach (var node in nodes)
            {
                if (node.Type == "file" && node.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    yield return node.Name;
                }
                if (node.Children != null)
                {
                    foreach (var f in FlattenFiles(node.Children, keyword))
                    {
                        yield return f;
                    }
                }
            }
        }

        // WPF 文件选择器简化：用 OpenFileDialog
        // 这里返回的文件路径通过 ViewModel 保存到 KB
        [RelayCommand]
        private void UploadFile(string kb)
        {
            // 占位：实际上传需在 View 中调 MsDialog/OpenFolderDialog
            _toastService.ShowWarning("上传功能：请选文件 → 调用 FileBridge.WriteText");
        }

        [RelayCommand]
        private void SearchKb(string kbName)
        {
            if (string.IsNullOrWhiteSpace(kbName))
            {
                return;
            }

            LoadFiles(kbName);
            // 触发搜索
            var found = FlattenFiles(_fileBridge.Tree(kbName), SearchText);
            Files.Clear();
            foreach (var f in found) Files.Add(f);
        }

        [RelayCommand]
        private void SaveFile(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(SelectedFile))
            {
                _toastService.ShowWarning("请选择文件后再保存");
                return;
            }

            _fileBridge.WriteText(SelectedKb, SelectedFile, content, out _);
            _toastService.ShowSuccess("已保存");
        }

        [RelayCommand]
        private void CreateKb(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _kbService.CreateKb(name);
            LoadKbs();
            // 建库成功后自动选中新库（对齐 Demo），并随 OnSelectedKbChanged 联动 CurrentKb
            SelectedKb = name;
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