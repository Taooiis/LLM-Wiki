using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;

using Markdig;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly KbService _kbService;
        private readonly FileBridge _fileBridge;
        private readonly ToastService _toastService;

        public DashboardViewModel(KbService kbService, FileBridge fileBridge, ToastService toastService)
        {
            _kbService = kbService;
            _fileBridge = fileBridge;
            _toastService = toastService;
            LoadKbs();
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
        }

        [RelayCommand]
        private void DeleteKb(string name)
        {
            _kbService.DeleteKb(name);
            LoadKbs();
        }
    }
}