using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;

namespace LLM_Wiki.ViewModels
{
    /// <summary>工作区模式（指南 §6.2）。</summary>
    public enum WorkspaceMode
    {
        Empty,     // 未选择文件：引导新建/上传
        Preview,   // 预览 + 元信息，可进编辑
        Editing,   // 编辑器（含新建）
        ReadOnly   // 只读类型：预览 + 系统打开
    }

    /// <summary>保存状态（常驻显示，指南 §6.2/§6.3）。</summary>
    public enum WorkspaceSaveState
    {
        None,     // 新建未保存过
        Saved,    // 已保存
        Dirty,    // 有未保存更改
        Saving,   // 正在保存
        Failed    // 保存失败（保留内容，可重试）
    }

    /// <summary>未保存确认的选择结果。</summary>
    public enum UnsavedChoice
    {
        Continue,       // 继续编辑（默认/Esc）
        Discard,        // 放弃更改
        SaveAndLeave    // 保存后离开（失败时必须回到编辑）
    }

    /// <summary>
    /// 文档工作区 ViewModel：源文档页与目录浏览页共用的右侧编辑/预览区。
    /// 状态机：Empty → Preview ⇄ Editing / ReadOnly；保存失败留在 Editing（指南 §6.2）。
    /// 实现 IUnsavedChangesGuard，供切换文件/知识库前统一询问。
    /// </summary>
    public partial class DocumentWorkspaceViewModel : ObservableObject, IUnsavedChangesGuard
    {
        private readonly FileBridge _fileBridge;
        private readonly ToastService _toastService;

        /// <summary>未保存确认回调（宿主页注入，弹统一三按钮对话框）。</summary>
        public Func<Task<UnsavedChoice>>? ConfirmUnsaved { get; set; }

        /// <summary>保存成功后通知宿主刷新列表/待处理变更。</summary>
        public event Action? DocumentSaved;

        private string _kb = "";
        private string _savedContent = "";   // 打开或保存成功时的基线内容，用于 dirty 判定
        private string _diskContent = "";    // 载入时的磁盘内容（放弃更改时恢复）

        public DocumentWorkspaceViewModel(FileBridge fileBridge, ToastService toastService)
        {
            _fileBridge = fileBridge;
            _toastService = toastService;
        }

        [ObservableProperty]
        private WorkspaceMode _mode = WorkspaceMode.Empty;

        [ObservableProperty]
        private WorkspaceSaveState _saveState = WorkspaceSaveState.None;

        [ObservableProperty]
        private string _currentFileName = "";

        [ObservableProperty]
        private string _currentRelativePath = "";

        [ObservableProperty]
        private string _previewContent = "";

        [ObservableProperty]
        private string _editorContent = "";

        [ObservableProperty]
        private string _saveErrorMessage = "";

        [ObservableProperty]
        private bool _isNewDocument;

        [ObservableProperty]
        private string _newFileName = "";

        /// <summary>新建文档落盘目录（相对库根，如 raw/sources）；空则不启用新建。</summary>
        public string NewFileDirectory { get; set; } = "raw/sources";

        public bool HasUnsavedChanges => Mode == WorkspaceMode.Editing && SaveState == WorkspaceSaveState.Dirty;

        // ============ XAML 绑定辅助属性（由 Mode/SaveState 派生） ============

        public Visibility WorkspaceFrameVisibility =>
            Mode == WorkspaceMode.Empty ? Visibility.Collapsed : Visibility.Visible;

        public Visibility IsPreviewContentVisible =>
            Mode == WorkspaceMode.Preview || Mode == WorkspaceMode.ReadOnly ? Visibility.Visible : Visibility.Collapsed;

        public Visibility IsEditingContentVisible =>
            Mode == WorkspaceMode.Editing ? Visibility.Visible : Visibility.Collapsed;

        public Visibility IsReadOnlyMode =>
            Mode == WorkspaceMode.ReadOnly ? Visibility.Visible : Visibility.Collapsed;

        public Visibility IsPreviewEditable =>
            Mode == WorkspaceMode.Preview && IsEditableFile ? Visibility.Visible : Visibility.Collapsed;

        public Visibility IsNewDocumentVisible =>
            Mode == WorkspaceMode.Editing && IsNewDocument ? Visibility.Visible : Visibility.Collapsed;

        public Visibility HasSaveError =>
            Mode == WorkspaceMode.Editing && SaveState == WorkspaceSaveState.Failed ? Visibility.Visible : Visibility.Collapsed;

        public string SaveStateText => SaveState switch
        {
            WorkspaceSaveState.Saved => "已保存",
            WorkspaceSaveState.Dirty => "● 有未保存更改",
            WorkspaceSaveState.Saving => "正在保存…",
            WorkspaceSaveState.Failed => "保存失败",
            _ => ""
        };

        public System.Windows.Media.Brush SaveStateBrush => SaveState switch
        {
            WorkspaceSaveState.Saved => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0xCB, 0x5F)),
            WorkspaceSaveState.Dirty => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xE1, 0x00)),
            WorkspaceSaveState.Failed => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0, 0, 0, 0))
        };

        public string PathBreadcrumb =>
            string.IsNullOrWhiteSpace(CurrentRelativePath) ? "" : CurrentRelativePath;

        public string FileTypeLabel =>
            CurrentRelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "Markdown"
            : CurrentRelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "文本"
            : "文件";


        public bool IsEditableFile =>
            CurrentRelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || CurrentRelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        /// <summary>编辑区内容变化 → dirty 判定（与基线比较，防重复通知）。</summary>
        partial void OnEditorContentChanged(string value)
        {
            if (Mode != WorkspaceMode.Editing)
            {
                return;
            }
            SaveState = value == _savedContent ? WorkspaceSaveState.Saved : WorkspaceSaveState.Dirty;
        }

        partial void OnModeChanged(WorkspaceMode value)
        {
            OnPropertyChanged(nameof(WorkspaceFrameVisibility));
            OnPropertyChanged(nameof(IsPreviewContentVisible));
            OnPropertyChanged(nameof(IsEditingContentVisible));
            OnPropertyChanged(nameof(IsReadOnlyMode));
            OnPropertyChanged(nameof(IsPreviewEditable));
            OnPropertyChanged(nameof(IsNewDocumentVisible));
        }

        partial void OnSaveStateChanged(WorkspaceSaveState value)
        {
            OnPropertyChanged(nameof(SaveStateText));
            OnPropertyChanged(nameof(SaveStateBrush));
        }

        partial void OnIsNewDocumentChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNewDocumentVisible));
        }

        partial void OnSaveErrorMessageChanged(string value)
        {
            OnPropertyChanged(nameof(HasSaveError));
        }

        /// <summary>重置为空态（切知识库/未选择时）。</summary>
        public void ResetToEmpty()
        {
            Mode = WorkspaceMode.Empty;
            SaveState = WorkspaceSaveState.None;
            IsNewDocument = false;
            EditorContent = "";
            PreviewContent = "";
            CurrentFileName = "";
            CurrentRelativePath = "";
            NewFileName = "";
            SaveErrorMessage = "";
            _savedContent = "";
            _diskContent = "";
        }

        /// <summary>预览选定文件（相对路径，如 raw/sources/a.md 或 wiki/index.md）。</summary>
        public void ShowPreview(string kb, string relativePath)
        {
            _kb = kb;
            var error = "";
            var content = _fileBridge.ReadText(kb, relativePath, out error) ?? "";

            _diskContent = content;
            _savedContent = content;
            PreviewContent = content;
            CurrentRelativePath = relativePath.Replace('\\', '/');
            CurrentFileName = Path.GetFileName(CurrentRelativePath);
            IsNewDocument = false;
            NewFileName = "";
            SaveErrorMessage = "";

            // 文本类可编辑；其他类型只读（多文件类型策略，spec 待确认项不改业务）
            Mode = IsEditableFile ? WorkspaceMode.Preview : WorkspaceMode.ReadOnly;
            SaveState = WorkspaceSaveState.Saved;
        }

        /// <summary>进入编辑（已选文件）。</summary>
        public void EnterEditMode()
        {
            if (Mode != WorkspaceMode.Preview || !IsEditableFile)
            {
                return;
            }
            EditorContent = PreviewContent;
            _savedContent = PreviewContent;
            Mode = WorkspaceMode.Editing;
            SaveState = WorkspaceSaveState.Saved;
        }

        /// <summary>新建文档：进入空编辑态，不创建文件（指南 §6.4）。</summary>
        public void EnterNewMode(string kb)
        {
            _kb = kb;
            IsNewDocument = true;
            NewFileName = "";
            EditorContent = "";
            _savedContent = "";
            _diskContent = "";
            CurrentFileName = "";
            CurrentRelativePath = "";
            SaveErrorMessage = "";
            Mode = WorkspaceMode.Editing;
            SaveState = WorkspaceSaveState.None;
        }

        /// <summary>返回预览（取消编辑）。</summary>
        public async Task<bool> CancelEditAsync()
        {
            if (!HasUnsavedChanges)
            {
                ExitToPreview();
                return true;
            }

            var choice = await AskUnsavedAsync();
            if (choice == UnsavedChoice.Continue)
            {
                return false;
            }
            if (choice == UnsavedChoice.SaveAndLeave && !await SaveAsync())
            {
                return false; // 保存失败：留在编辑（指南 §11.4）
            }

            ExitToPreview();
            return true;
        }

        /// <summary>保存（Ctrl+S / 按钮）。返回是否成功；失败保留编辑内容并显示错误（指南 §6.5）。</summary>
        [RelayCommand]
        public async Task<bool> SaveAsync()
        {
            if (Mode != WorkspaceMode.Editing || SaveState == WorkspaceSaveState.Saving)
            {
                return Mode != WorkspaceMode.Editing;
            }

            // 解析目标相对路径
            string rel;
            var name = "";
            if (IsNewDocument)
            {
                name = NewFileName.Trim();
                if (name.Length == 0)
                {
                    SaveState = WorkspaceSaveState.Failed;
                    SaveErrorMessage = "文件名不能为空";
                    return false;
                }
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    SaveState = WorkspaceSaveState.Failed;
                    SaveErrorMessage = "文件名包含非法字符";
                    return false;
                }
                if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    name += ".md";
                }
                rel = $"{NewFileDirectory.TrimEnd('/')}/{name}";

                // 同名冲突不静默覆盖（指南 §6.4）
                var existing = _fileBridge.ReadText(_kb, rel, out _);
                if (existing != null)
                {
                    SaveState = WorkspaceSaveState.Failed;
                    SaveErrorMessage = $"同名文件已存在：{rel}，请换一个名称";
                    return false;
                }
            }
            else
            {
                rel = CurrentRelativePath;
                name = CurrentFileName;
            }

            SaveState = WorkspaceSaveState.Saving;
            SaveErrorMessage = "";

            var error = "";
            var ok = await Task.Run(() => _fileBridge.WriteText(_kb, rel, EditorContent, out error));
            if (!ok)
            {
                SaveState = WorkspaceSaveState.Failed;
                SaveErrorMessage = "保存失败：" + error;
                _toastService.ShowError(SaveErrorMessage);
                return false; // 留在 Editing，内容不丢
            }

            // 成功：更新基线、进入预览、通知宿主刷新
            _savedContent = EditorContent;
            _diskContent = EditorContent;
            PreviewContent = EditorContent;
            CurrentRelativePath = rel.Replace('\\', '/');
            CurrentFileName = Path.GetFileName(rel);
            IsNewDocument = false;
            Mode = WorkspaceMode.Preview;
            SaveState = WorkspaceSaveState.Saved;
            DocumentSaved?.Invoke();
            _toastService.ShowSuccess("已保存：" + CurrentFileName + "，去工作流页重新 Ingest 该文档");
            return true;
        }

        /// <summary>用系统默认程序打开当前文件（只读类型）。</summary>
        [RelayCommand]
        public void OpenWithDefaultProgram()
        {
            if (string.IsNullOrWhiteSpace(_kb) || string.IsNullOrWhiteSpace(CurrentRelativePath))
            {
                return;
            }

            var full = _fileBridge.KbRoot(_kb) + Path.DirectorySeparatorChar + CurrentRelativePath.Replace('/', Path.DirectorySeparatorChar);
            if (!File.Exists(full))
            {
                _toastService.ShowError("文件不存在：" + CurrentRelativePath);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _toastService.ShowError("无法打开文件：" + ex.Message);
            }
        }

        /// <summary>IUnsavedChangesGuard：切换文件/知识库/页面前的统一询问（指南 §15.2）。</summary>
        public async Task<bool> CanLeaveAsync(CancellationToken cancellationToken = default)
        {
            if (!HasUnsavedChanges)
            {
                return true;
            }

            var choice = await AskUnsavedAsync();
            if (choice == UnsavedChoice.Continue)
            {
                return false;
            }
            if (choice == UnsavedChoice.SaveAndLeave && !await SaveAsync())
            {
                return false; // 保存失败不允许离开
            }

            return true;
        }

        private async Task<UnsavedChoice> AskUnsavedAsync()
        {
            if (ConfirmUnsaved == null)
            {
                return UnsavedChoice.Continue; // 未注入确认回调时保守处理：不离开
            }
            return await ConfirmUnsaved();
        }

        /// <summary>退出编辑态：取消 → 回预览（磁盘内容未变，直接恢复）；新建取消 → Empty。</summary>
        private void ExitToPreview()
        {
            if (IsNewDocument)
            {
                Mode = WorkspaceMode.Empty;
                SaveState = WorkspaceSaveState.None;
                EditorContent = "";
                return;
            }

            PreviewContent = _diskContent;
            EditorContent = _diskContent;
            Mode = IsEditableFile ? WorkspaceMode.Preview : WorkspaceMode.ReadOnly;
            SaveState = WorkspaceSaveState.Saved;
        }
    }
}
