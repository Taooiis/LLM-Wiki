using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Data;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.ViewModels.Pages
{
    public partial class SourceViewModel : ObservableObject
    {
        private readonly KbService _kbService;
        private readonly FileBridge _fileBridge;
        private readonly MainWindowViewModel _mainViewModel;
        private readonly INavigationService _navigationService;
        private readonly ToastService _toastService;

        public SourceViewModel(KbService kbService, FileBridge fileBridge, MainWindowViewModel mainViewModel, INavigationService navigationService, ToastService toastService)
        {
            _kbService = kbService;
            _fileBridge = fileBridge;
            _mainViewModel = mainViewModel;
            _navigationService = navigationService;
            _toastService = toastService;

            // 过滤视图：文件名包含搜索词 且 类型匹配（全部/Markdown/文本）
            FilteredFiles = CollectionViewSource.GetDefaultView(Files);
            FilteredFiles.Filter = o => o is SourceEntry e
                && (string.IsNullOrWhiteSpace(SearchKeyword) || e.Name.Contains(SearchKeyword.Trim(), StringComparison.OrdinalIgnoreCase))
                && e.MatchesType(TypeFilter);

            // 文档工作区（指南阶段 2）：源文档页编辑/新建/预览统一走右侧工作区
            Workspace = new DocumentWorkspaceViewModel(_fileBridge, _toastService)
            {
                NewFileDirectory = "raw/sources"
            };
            Workspace.DocumentSaved += OnWorkspaceDocumentSaved;

            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

            // 页面 VM 是懒解析单例：CurrentKb 的首次赋值可能早于本构造，订阅后必须拉一次现值
            LoadKb(_mainViewModel.CurrentKb);
        }

        /// <summary>右侧文档工作区（预览/编辑/只读/新建）。</summary>
        public DocumentWorkspaceViewModel Workspace { get; }

        /// <summary>工作区保存成功：刷新列表与待处理变更。</summary>
        private void OnWorkspaceDocumentSaved()
        {
            LoadFiles(SelectedKb);
            LoadChanges(SelectedKb);
        }

        /// <summary>
        /// 选择文件：先经未保存保护，再加载预览（列表存裸文件名，源文档位于 raw/sources/ 下）。
        /// </summary>
        public async Task SelectFileAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            if (!await Workspace.CanLeaveAsync())
            {
                return; // 用户选择继续编辑：不切换
            }

            SelectedFile = fileName;
            Workspace.ShowPreview(SelectedKb, "raw/sources/" + fileName);
        }

        private bool _kbSwitchLocked;

        private async void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb))
            {
                // 回滚引起的再入：直接加载目标库
                if (_kbSwitchLocked)
                {
                    LoadKb(_mainViewModel.CurrentKb);
                    return;
                }

                // 切库前未保存保护（指南阶段 2 验收项）；继续编辑则回滚全局库选择
                if (!await Workspace.CanLeaveAsync())
                {
                    _kbSwitchLocked = true;
                    _mainViewModel.CurrentKb = SelectedKb;
                    _kbSwitchLocked = false;
                    return;
                }

                LoadKb(_mainViewModel.CurrentKb);
            }
        }

        [ObservableProperty]
        private string _selectedKb = "";

        [ObservableProperty]
        private ObservableCollection<SourceEntry> _files = new();

        [ObservableProperty]
        private string _selectedFile = "";

        /// <summary>文件名搜索词：只过滤当前已加载列表（02§3.1，不触发磁盘读取）。</summary>
        [ObservableProperty]
        private string _searchKeyword = "";

        /// <summary>类型筛选键：全部 / Markdown / 文本（02§3.1，仅覆盖白名单内类型）。</summary>
        [ObservableProperty]
        private string _typeFilter = "全部";

        /// <summary>过滤后的文件视图（搜索词 + 类型筛选，供列表绑定与计数）。</summary>
        public ICollectionView FilteredFiles { get; }

        [ObservableProperty]
        private ObservableCollection<ChangeItem> _changes = new();

        partial void OnSearchKeywordChanged(string value) => FilteredFiles.Refresh();

        partial void OnTypeFilterChanged(string value) => FilteredFiles.Refresh();

        public void LoadKb(string kb)
        {
            SelectedKb = kb;
            LoadFiles(kb);
            LoadChanges(kb);
            ResetWorkspace();
        }

        public void LoadFiles(string kb)
        {
            Files.Clear();
            if (string.IsNullOrWhiteSpace(kb))
            {
                return;
            }

            // 只列源文档（raw/sources/ 下 md/txt，含大小/修改时间元数据），不再取树顶层
            foreach (var entry in _fileBridge.SourceEntries(kb))
            {
                Files.Add(entry);
            }
            FilteredFiles.Refresh();
        }

        /// <summary>清空工作区（切库/无库时回到空态引导）。</summary>
        public void ResetWorkspace()
        {
            Workspace.ResetToEmpty();
        }

        public void LoadChanges(string kb)
        {
            Changes.Clear();
            if (string.IsNullOrWhiteSpace(kb))
            {
                return;
            }

            foreach (var change in _fileBridge.Changes(kb))
            {
                Changes.Add(new ChangeItem(change.File, change.Action, change.Time, change.Hash));
            }
        }

        [RelayCommand]
        private void DeleteFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            // 列表存的是文件名，源文档统一位于 raw/sources/ 下
            _fileBridge.Delete(SelectedKb, "raw/sources/" + fileName, out _);
            LoadFiles(SelectedKb);
            SelectedFile = string.Empty;
            // 删除后工作区回空态
            Workspace.ResetToEmpty();
        }

        [RelayCommand]
        private void Refresh()
        {
            LoadFiles(SelectedKb);
            LoadChanges(SelectedKb);
        }

        [RelayCommand]
        private void GenerateIngestForSelected()
        {
            // 勾选变更的 File 为含 raw/sources/ 前缀的相对路径；
            // 归一化统一在 WorkflowViewModel.GenerateIngest 入口做（见 ledger Ruling T4）
            var selected = Changes.Where(c => c.IsSelected).Select(c => c.File).ToList();
            if (selected.Count == 0)
            {
                _toastService.ShowWarning("请先勾选要处理的变更");
                return;
            }

            _mainViewModel.PendingIngestFiles = string.Join(",", selected);
            _mainViewModel.PendingIngestKb = SelectedKb;

            _navigationService.Navigate(typeof(Views.Pages.WorkflowPage));
        }

        [RelayCommand]
        private void ClearSelectedChanges()
        {
            var selected = Changes.Where(c => c.IsSelected).Select(c => c.File).ToList();
            if (selected.Count == 0)
            {
                return;
            }

            _fileBridge.ClearChanges(SelectedKb, selected.ToArray());
            foreach (var c in Changes.Where(c => c.IsSelected).ToList())
            {
                c.IsSelected = false;
            }
            LoadChanges(SelectedKb);
        }

        [RelayCommand]
        private async Task UploadFile()
        {
            if (string.IsNullOrWhiteSpace(SelectedKb))
            {
                _toastService.ShowWarning("请先选择一个知识库");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "选择要上传的文件",
                Filter = "所有支持的文件 (*.md;*.txt;*.docx;*.xlsx)|*.md;*.txt;*.docx;*.xlsx|文本文件 (*.md;*.txt)|*.md;*.txt|Office 文件 (*.docx;*.xlsx)|*.docx;*.xlsx",
                Multiselect = false
            };

            if (dialog.ShowDialog(Application.Current.MainWindow) != true)
            {
                return;
            }

            var sourcePath = dialog.FileName;
            var fileName = Path.GetFileName(sourcePath);
            var ext = Path.GetExtension(fileName).ToLowerInvariant();

            // 读取文件内容
            byte[] rawBytes;
            string content;
            string? error;
            try
            {
                rawBytes = File.ReadAllBytes(sourcePath);
                if (ext == ".docx" || ext == ".xlsx")
                {
                    // Office 文件：先保存原件到 raw/original/，Markdown 副本写 raw/sources/
                    var originalRel = "raw/original/" + fileName;
                    File.Copy(sourcePath, Path.Combine(GetKbRoot(), originalRel), overwrite: true);

                    var (md, convErr) = ext == ".docx"
                        ? OfficeConverter.ConvertDocx(fileName, rawBytes)
                        : OfficeConverter.ConvertXlsx(fileName, rawBytes);
                    if (md == null)
                    {
                        _toastService.ShowError("Office 转换失败：" + convErr);
                        return;
                    }
                    content = md;
                    error = "";
                }
                else
                {
                    content = Encoding.UTF8.GetString(rawBytes);
                    error = "";
                }
            }
            catch (Exception ex)
            {
                _toastService.ShowError("读取文件失败：" + ex.Message);
                return;
            }

            // Markdown 副本写入 raw/sources/（文件名 + .md 后缀，避免与已有 md 重名）
            var rel = "raw/sources/" + fileName;
            if (ext == ".docx" || ext == ".xlsx")
                rel = "raw/sources/" + Path.GetFileNameWithoutExtension(fileName) + ".md";

            var ok = _fileBridge.WriteText(SelectedKb, rel, content, out error);
            if (!ok)
            {
                _toastService.ShowError("上传失败：" + error);
                return;
            }

            LoadFiles(SelectedKb);
            LoadChanges(SelectedKb);
            _toastService.ShowSuccess($"已上传：{fileName}");
        }

        private string GetKbRoot()
        {
            return _fileBridge.KbRoot(SelectedKb);
        }
    }

    /// <summary>待处理变更条目；IsSelected 可通知（列表滚动回收后勾选不丢）。</summary>
    public partial class ChangeItem : ObservableObject
    {
        public string File { get; }
        public string Action { get; }
        public string Time { get; }
        public string Hash { get; }

        [ObservableProperty]
        private bool _isSelected;

        public ChangeItem(string file, string action, string time, string hash)
        {
            File = file;
            Action = action;
            Time = time;
            Hash = hash;
        }
    }
}
