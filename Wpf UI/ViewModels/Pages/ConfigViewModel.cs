using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

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
        private readonly DashboardViewModel _dashboardViewModel;
        private readonly ToastService _toastService;

        public ConfigViewModel(ConfigService configService, FileBridge fileBridge, MainWindowViewModel mainViewModel, DashboardViewModel dashboardViewModel, ToastService toastService)
        {
            _configService = configService;
            _fileBridge = fileBridge;
            _mainViewModel = mainViewModel;
            _dashboardViewModel = dashboardViewModel;
            _toastService = toastService;

            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

            // 页面 VM 懒解析：CurrentKb 首值可能早于本构造，订阅后拉一次现值
            LoadKb(_mainViewModel.CurrentKb);

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
                _toastService.ShowWarning("请先选择知识库");
                return;
            }

            _fileBridge.WriteText(SelectedKb, "purpose.md", content, out _);
            _toastService.ShowSuccess("purpose.md 已保存");
        }

        [RelayCommand]
        private void SaveSchema(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                _toastService.ShowWarning("请先选择知识库");
                return;
            }

            _fileBridge.WriteText(SelectedKb, "schema.md", content, out _);
            _toastService.ShowSuccess("schema.md 已保存");
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
                _toastService.ShowWarning("存储路径无效，请先修正");
                return;
            }

            var error = "";
            var ok = _configService.SetWikisDir(WikisDirInput, out error);
            if (!ok)
            {
                _toastService.ShowError("保存路径失败：" + error);
                return;
            }

            WikisDir = _configService.WikisDir;
            WikisDirInput = WikisDir;
            // 存储路径变更立即生效：重载库列表（选中失效时自动回退到首个库，见 Dashboard.LoadOverview）
            _dashboardViewModel.LoadOverview();
            _toastService.ShowSuccess("存储路径已更新：" + WikisDir);
        }

        [RelayCommand]
        private void ResetWikisDir()
        {
            // 恢复默认并立即保存生效（对齐 Demo"恢复默认"行为）
            var defaultWikis = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki", "wikis");
            WikisDirInput = defaultWikis;
            ValidateWikisDir();
            SaveWikisDir();
        }
    }
}
