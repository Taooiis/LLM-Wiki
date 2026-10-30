using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class TreeBrowserViewModel : ObservableObject
    {
        private readonly FileBridge _fileBridge;
        private readonly MainWindowViewModel _mainViewModel;

        public TreeBrowserViewModel(FileBridge fileBridge, MainWindowViewModel mainViewModel)
        {
            _fileBridge = fileBridge;
            _mainViewModel = mainViewModel;

            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

            // 页面 VM 懒解析：CurrentKb 首值可能早于本构造，订阅后拉一次现值
            LoadTree(_mainViewModel.CurrentKb);
        }

        private void MainViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb))
            {
                LoadTree(_mainViewModel.CurrentKb);
            }
        }

        [ObservableProperty]
        private string _selectedKb = "";

        [ObservableProperty]
        private ObservableCollection<TreeItemViewModel> _treeItems = new();

        [ObservableProperty]
        private string _selectedFilePath = "";

        [ObservableProperty]
        private string _previewContent = "";

        [ObservableProperty]
        private bool _isPreviewOpen = false;

        [ObservableProperty]
        private bool _isEditMode = false;

        public bool IsPreviewReadOnly => !IsEditMode;

        public void LoadTree(string kb)
        {
            SelectedKb = kb;
            TreeItems.Clear();
            if (string.IsNullOrWhiteSpace(kb))
            {
                return;
            }

            foreach (var item in _fileBridge.Tree(kb))
            {
                TreeItems.Add(new TreeItemViewModel(item));
            }
        }

        public void SelectFile(string relPath)
        {
            SelectedFilePath = relPath;
            var kb = SelectedKb;

            var error = "";
            var content = _fileBridge.ReadText(kb, relPath, out error);
            PreviewContent = content ?? string.Empty;

            // 只有 md/txt 才可编辑
            IsEditMode = relPath.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase)
                || relPath.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase);

            IsPreviewOpen = true;
        }

        [RelayCommand]
        private void SavePreview(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(SelectedFilePath))
            {
                return;
            }

            // SelectedFilePath 为相对库根路径（如 wiki/index.md），原样写回，不再丢目录
            _fileBridge.WriteText(SelectedKb, SelectedFilePath, content, out _);
            IsPreviewOpen = false;
        }

        [RelayCommand]
        private void ClosePreview()
        {
            IsPreviewOpen = false;
        }
    }

    public class TreeItemViewModel : ObservableObject
    {
        private readonly TreeItem _item;

        public TreeItemViewModel(TreeItem item)
        {
            _item = item;
        }

        public string Name => _item.Name;
        public string Type => _item.Type;
        /// <summary>相对库根的路径（如 wiki/index.md）；树构建时由 FileBridge 算好。</summary>
        public string RelPath => _item.RelPath ?? _item.Name;
        public bool HasChildren => _item.Children != null && _item.Children.Length > 0;

        public ObservableCollection<TreeItemViewModel> Children
        {
            get
            {
                var children = new ObservableCollection<TreeItemViewModel>();
                if (_item.Children != null)
                {
                    foreach (var child in _item.Children)
                    {
                        children.Add(new TreeItemViewModel(child));
                    }
                }
                return children;
            }
        }
    }
}
