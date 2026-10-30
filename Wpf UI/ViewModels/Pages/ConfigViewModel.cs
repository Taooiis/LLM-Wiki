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

            // 初次进入即校验当前路径，状态区不显示"未验证"空壳
            ValidateWikisDir();
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

        /// <summary>路径状态文案（设计文档 06§3：未验证 / 路径可用 / 目录不存在将创建 / 无权限）。</summary>
        [ObservableProperty]
        private string _pathStatusText = "未验证";

        /// <summary>路径状态是否健康：决定状态图标的绿色/警示色。</summary>
        public bool PathIsOk => PathStatusText.StartsWith("路径可用", StringComparison.Ordinal);

        partial void OnPathStatusTextChanged(string value) => OnPropertyChanged(nameof(PathIsOk));

        /// <summary>purpose.md 是否已存在（缺失时提供创建默认模板入口，设计文档 06§4）。</summary>
        [ObservableProperty]
        private bool _purposeExists;

        [ObservableProperty]
        private bool _schemaExists;

        /// <summary>purpose.md 保存状态提示（存在/已保存/未保存）。</summary>
        [ObservableProperty]
        private string _purposeStatus = "";

        [ObservableProperty]
        private string _schemaStatus = "";

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
                PurposeExists = false;
                SchemaExists = false;
                PurposeStatus = "";
                SchemaStatus = "";
                return;
            }

            var purposeError = "";
            var purposeContent = _fileBridge.ReadText(kb, "purpose.md", out purposeError);
            PurposeContent = purposeContent ?? string.Empty;
            PurposeExists = purposeContent != null;
            PurposeStatus = PurposeExists ? "文件存在" : "文件缺失";

            var schemaError = "";
            var schemaContent = _fileBridge.ReadText(kb, "schema.md", out schemaError);
            SchemaContent = schemaContent ?? string.Empty;
            SchemaExists = schemaContent != null;
            SchemaStatus = SchemaExists ? "文件存在" : "文件缺失";
        }

        [RelayCommand]
        private void SavePurpose(string content)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                _toastService.ShowWarning("请先选择知识库");
                return;
            }

            // 保存失败不能报告成功（设计文档 06§4、07§1.4）
            if (!_fileBridge.WriteText(SelectedKb, "purpose.md", content, out var error))
            {
                _toastService.ShowError("purpose.md 保存失败：" + error + "，内容已保留");
                PurposeStatus = "保存失败，内容未写入";
                return;
            }

            PurposeContent = content;
            PurposeExists = true;
            PurposeStatus = "已保存";
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

            if (!_fileBridge.WriteText(SelectedKb, "schema.md", content, out var error))
            {
                _toastService.ShowError("schema.md 保存失败：" + error + "，内容已保留");
                SchemaStatus = "保存失败，内容未写入";
                return;
            }

            SchemaContent = content;
            SchemaExists = true;
            SchemaStatus = "已保存";
            _toastService.ShowSuccess("schema.md 已保存");
        }

        /// <summary>文件缺失时创建默认模板（设计文档 06§4：创建前展示将写入的内容摘要）。</summary>
        [RelayCommand]
        private void CreateDefaultPurpose()
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                _toastService.ShowWarning("请先选择知识库");
                return;
            }

            var template = DefaultPurposeTemplate();
            PurposeContent = template;
            PurposeStatus = "已填入默认模板，确认后点保存";
        }

        [RelayCommand]
        private void CreateDefaultSchema()
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                _toastService.ShowWarning("请先选择知识库");
                return;
            }

            var template = DefaultSchemaTemplate();
            SchemaContent = template;
            SchemaStatus = "已填入默认模板，确认后点保存";
        }

        private static string DefaultPurposeTemplate() =>
            "# 知识库目标\n\n"
            + "## 目标\n\n（描述这个知识库要解决什么问题、服务谁）\n\n"
            + "## 关键问题\n\n- \n\n"
            + "## 范围与边界\n\n- 包含：\n- 不包含：\n";

        private static string DefaultSchemaTemplate() =>
            "# 结构规则\n\n"
            + "## 页面约定\n\n"
            + "- 每个 wiki 页面顶部使用 YAML frontmatter，含 sources: []\n"
            + "- 断言标注来源：[来源: 文件名]\n"
            + "- 页内互链使用 [[页面名]]\n\n"
            + "## 目录分类\n\n"
            + "- entities/：实体（人、产品、组织）\n"
            + "- concepts/：概念与方法\n"
            + "- sources/：源文档摘要\n"
            + "- queries/、synthesis/、comparisons/：问答、综合、对比\n";

        [RelayCommand]
        public void ValidateWikisDir()
        {
            if (string.IsNullOrWhiteSpace(WikisDirInput))
            {
                IsPathValid = false;
                PathErrorMessage = "请输入存储路径";
                PathStatusText = "未填写路径";
                return;
            }

            var resolved = PathGuard.ResolveWikisDir(WikisDirInput, AppContext.BaseDirectory);
            if (resolved == null)
            {
                IsPathValid = false;
                PathErrorMessage = "路径非法：请填完整的绝对路径（如 D:\\知识库 或 \\\\电脑\\共享）或相对路径，单个反斜杠开头不可用";
                PathStatusText = "路径格式错误";
                return;
            }

            // 已存在与"不存在但可创建"分开提示，避免把创建失败误报成可用（设计文档 06§3）
            var existed = Directory.Exists(resolved);
            try
            {
                Directory.CreateDirectory(resolved);
                IsPathValid = true;
                PathErrorMessage = "";
                PathStatusText = existed ? "路径可用 · 目录存在" : "路径可用 · 目录不存在（保存后自动创建）";
            }
            catch (UnauthorizedAccessException)
            {
                IsPathValid = false;
                PathErrorMessage = "无权限写入该路径，请换一个有写权限的目录";
                PathStatusText = "无权限";
            }
            catch (Exception e)
            {
                IsPathValid = false;
                PathErrorMessage = "路径不可用，无法创建目录：" + e.Message;
                PathStatusText = "不可用";
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
            PathStatusText = "路径可用 · 已保存";
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
