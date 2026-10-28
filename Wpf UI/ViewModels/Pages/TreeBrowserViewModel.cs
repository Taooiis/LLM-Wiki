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

        public void SelectFile(string filePath)
        {
            SelectedFilePath = filePath;
            var kb = SelectedKb;
            var file = System.IO.Path.GetFileName(filePath);

            var error = "";
            var content = _fileBridge.ReadText(kb, file, out error);
            PreviewContent = content ?? string.Empty;

            // 只有 md/txt 才可编辑
            IsEditMode = file.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase);

            IsPreviewOpen = true;
        }

        [RelayCommand]
        private void SavePreview(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(SelectedFilePath))
            {
                return;
            }

            var file = System.IO.Path.GetFileName(SelectedFilePath);
            _fileBridge.WriteText(SelectedKb, file, content, out _);
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
