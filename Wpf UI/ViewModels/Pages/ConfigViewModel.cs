using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class ConfigViewModel : ObservableObject
    {
        private readonly ConfigService _configService;
        private readonly FileBridge _fileBridge;
        private readonly MainWindowViewModel _mainViewModel;

        public ConfigViewModel(ConfigService configService, FileBridge fileBridge, MainWindowViewModel mainViewModel)
        {
            _configService = configService;
            _fileBridge = fileBridge;
            _mainViewModel = mainViewModel;

            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

            WikisDir = _configService.WikisDir;
            WikisDirInput = _configService.WikisDir;
        }

        private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb))
            {
                LoadPurposeSchema(_mainViewModel.CurrentKb);
            }
        }

        [ObservableProperty]
        private string _selectedKb = "";

        [ObservableProperty]
        private string _wikisDir = "";

        [ObservableProperty]
        private string _wikisDirInput = "";

        [ObservableProperty]
        private string _purposeContent = "";

        [ObservableProperty]
        private string _schemaContent = "";

        [ObservableProperty]
        private bool _isPathValid = true;

        [ObservableProperty]
        private string _pathErrorMessage = "";

        public void LoadKb(string kb)
        {
            SelectedKb = kb;
            LoadPurposeSchema(kb);
        }

        private void LoadPurposeSchema(string kb)
        {
            if (string.IsNullOrWhiteSpace(kb))
            {
                PurposeContent = string.Empty;
                SchemaContent = string.Empty;
                return;
            }

            var purposeError = "";
            var purposeContent = _fileBridge.ReadText(kb, "purpose.md", out purposeError);
            PurposeContent = purposeContent ?? string.Empty;

            var schemaError = "";
            var schemaContent = _fileBridge.ReadText(kb, "schema.md", out schemaError);
            SchemaContent = schemaContent ?? string.Empty;
        }

        [RelayCommand]
        private void SavePurpose(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                MessageBox.Show("请先选择知识库", "保存 purpose.md", MessageBoxButton.OK);
                return;
            }

            _fileBridge.WriteText(SelectedKb, "purpose.md", content, out _);
            MessageBox.Show("purpose.md 已保存", "保存", MessageBoxButton.OK);
        }

        [RelayCommand]
        private void SaveSchema(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                MessageBox.Show("请先选择知识库", "保存 schema.md", MessageBoxButton.OK);
                return;
            }

            _fileBridge.WriteText(SelectedKb, "schema.md", content, out _);
            MessageBox.Show("schema.md 已保存", "保存", MessageBoxButton.OK);
        }

        [RelayCommand]
        public void ValidateWikisDir()
        {
            if (string.IsNullOrWhiteSpace(WikisDirInput))
            {
                IsPathValid = false;
                PathErrorMessage = "请输入存储路径";
                return;
            }

            var resolved = PathGuard.ResolveWikisDir(WikisDirInput, AppContext.BaseDirectory);
            if (resolved == null)
            {
                IsPathValid = false;
                PathErrorMessage = "路径非法：请填完整的绝对路径（如 D:\\知识库 或 \\\\电脑\\共享）或相对路径，单个反斜杠开头不可用";
                return;
            }

            try
            {
                Directory.CreateDirectory(resolved);
                IsPathValid = true;
                PathErrorMessage = "";
            }
            catch (Exception e)
            {
                IsPathValid = false;
                PathErrorMessage = "路径不可用，无法创建目录：" + e.Message;
            }
        }

        [RelayCommand]
        private void SaveWikisDir()
        {
            if (!IsPathValid)
            {
                MessageBox.Show("存储路径无效，请先修正", "保存路径", MessageBoxButton.OK);
                return;
            }

            var error = "";
            var ok = _configService.SetWikisDir(WikisDirInput, out error);
            if (!ok)
            {
                MessageBox.Show(error, "保存路径失败", MessageBoxButton.OK);
                return;
            }

            WikisDir = _configService.WikisDir;
            MessageBox.Show($"存储路径已更新：{WikisDir}", "保存路径", MessageBoxButton.OK);
        }

        [RelayCommand]
        private void BrowseWikisDir()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择知识库存储目录",
                InitialDirectory = WikisDirInput
            };

            if (dialog.ShowDialog() == true)
            {
                WikisDirInput = dialog.FolderName;
                ValidateWikisDir();
            }
        }

        [RelayCommand]
        private void ResetWikisDir()
        {
            var defaultWikis = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki", "wikis");
            WikisDirInput = defaultWikis;
            ValidateWikisDir();
        }
    }
}
