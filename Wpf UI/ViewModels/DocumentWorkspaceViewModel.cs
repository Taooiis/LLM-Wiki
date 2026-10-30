using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;

namespace LLM_Wiki.ViewModels;

public enum WorkspaceMode { Empty, Preview, Editing, ReadOnly }
public enum WorkspaceSaveState { None, Saved, Dirty, Saving, Failed }
public enum UnsavedChoice { Continue, Discard, SaveAndLeave }
public enum DocumentRole { None, Source, Knowledge }

/// <summary>
/// 资料/知识共用的文档工作区：预览、编辑、只读、新建。
/// 保存时带磁盘版本校验；保存失败留在编辑并保留草稿；知识默认只读，手动修订需显式进入。
/// </summary>
public partial class DocumentWorkspaceViewModel : ObservableObject, IUnsavedChangesGuard
{
    private readonly SourceService _sources;
    private readonly KnowledgeService _knowledge;
    private readonly SourcePreviewService _preview;
    private readonly ToastService _toast;

    private string _kb = "";
    private string _diskHash = "";
    private string _savedContent = "";

    public DocumentWorkspaceViewModel(SourceService sources, KnowledgeService knowledge, SourcePreviewService preview, ToastService toast)
    {
        _sources = sources;
        _knowledge = knowledge;
        _preview = preview;
        _toast = toast;
    }

    public Func<Task<UnsavedChoice>>? ConfirmUnsaved { get; set; }

    public event Action? DocumentSaved;

    [ObservableProperty] private WorkspaceMode _mode = WorkspaceMode.Empty;
    [ObservableProperty] private WorkspaceSaveState _saveState = WorkspaceSaveState.None;
    [ObservableProperty] private DocumentRole _role = DocumentRole.None;
    [ObservableProperty] private string _currentFileName = "";
    [ObservableProperty] private string _currentRelativePath = "";
    [ObservableProperty] private string _previewContent = "";
    [ObservableProperty] private string _editorContent = "";
    [ObservableProperty] private string _saveErrorMessage = "";
    [ObservableProperty] private bool _isNewDocument;
    [ObservableProperty] private string _newFileName = "";
    [ObservableProperty] private string _readOnlyMessage = "";
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private bool _isImagePreview;
    [ObservableProperty] private bool _isExtractedPreview;
    [ObservableProperty] private bool _suppressFirstH1;
    [ObservableProperty] private bool _hideChrome;

    /// <summary>“文件已保存，变更记录未完成”等持久提醒（00§9.7）：保留到补记解决，不用成功通知掩盖。</summary>
    [ObservableProperty] private string _savePendingWarning = "";

    public Visibility SavePendingInfoVisible => string.IsNullOrEmpty(SavePendingWarning) ? Visibility.Collapsed : Visibility.Visible;

    public string NewFileDirectory { get; set; } = "raw/sources";

    public bool HasUnsavedChanges => Mode == WorkspaceMode.Editing && SaveState == WorkspaceSaveState.Dirty;
    public bool IsDirty => SaveState == WorkspaceSaveState.Dirty;
    public bool IsKnowledgeRevision => Mode == WorkspaceMode.Editing && Role == DocumentRole.Knowledge;
    public string CurrentVersion => _diskHash;
    public Visibility ChromeVisibility => HideChrome ? Visibility.Collapsed : Visibility.Visible;

    public Visibility WorkspaceFrameVisibility => Mode == WorkspaceMode.Empty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility IsPreviewContentVisible => Mode is WorkspaceMode.Preview or WorkspaceMode.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsMarkdownPreviewVisible => (Mode == WorkspaceMode.Preview && Role == DocumentRole.Knowledge)
        || (IsExtractedPreview && Mode is WorkspaceMode.ReadOnly or WorkspaceMode.Preview)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsPlainPreviewVisible => !IsImagePreview && !IsExtractedPreview
        && (Mode == WorkspaceMode.Preview && Role == DocumentRole.Source || Mode == WorkspaceMode.ReadOnly)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsImagePreviewVisible => IsImagePreview && Mode is WorkspaceMode.ReadOnly or WorkspaceMode.Preview
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsEditingContentVisible => Mode == WorkspaceMode.Editing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsReadOnlyMode => Mode == WorkspaceMode.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsPreviewEditable => Mode == WorkspaceMode.Preview && Role == DocumentRole.Source && CanEdit && !IsNewDocument ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsNewDocumentVisible => Mode == WorkspaceMode.Editing && IsNewDocument ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasSaveError => Mode == WorkspaceMode.Editing && SaveState == WorkspaceSaveState.Failed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowSaveBar => Mode == WorkspaceMode.Editing ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSourceDocument => Role == DocumentRole.Source;

    public bool CanEdit => Role == DocumentRole.Source
                           || (Role == DocumentRole.Knowledge && CurrentRelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                               && !CurrentRelativePath.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase));

    public string SaveStateText => (Mode == WorkspaceMode.Preview && Role == DocumentRole.Knowledge)
        || (Mode == WorkspaceMode.ReadOnly && Role == DocumentRole.Source)
        ? "只读"
        : SaveState switch
    {
        WorkspaceSaveState.Saved => "已保存",
        WorkspaceSaveState.Dirty => "未保存",
        WorkspaceSaveState.Saving => "保存中…",
        WorkspaceSaveState.Failed => "保存失败",
        WorkspaceSaveState.None => IsNewDocument ? "未创建" : "",
        _ => "",
    };

    public System.Windows.Media.Brush SaveStateBrush => SaveState switch
    {
        WorkspaceSaveState.Saved => ThemeBrush("SuccessBrush"),
        WorkspaceSaveState.Dirty => ThemeBrush("WarningBrush"),
        WorkspaceSaveState.Failed => ThemeBrush("DangerBrush"),
        _ => System.Windows.Media.Brushes.Transparent,
    };

    /// <summary>保存状态色随主题走（00§8：不在代码写死主题色）。</summary>
    private static System.Windows.Media.Brush ThemeBrush(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

    public string PathBreadcrumb => CurrentRelativePath;
    public string FileTypeLabel => SourceRules.TypeLabel(CurrentFileName);

    partial void OnEditorContentChanged(string value)
    {
        if (Mode != WorkspaceMode.Editing) return;
        SaveState = value == _savedContent ? WorkspaceSaveState.Saved : WorkspaceSaveState.Dirty;
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnModeChanged(WorkspaceMode value) => RaiseViewFlags();
    partial void OnSaveStateChanged(WorkspaceSaveState value)
    {
        OnPropertyChanged(nameof(SaveStateText));
        OnPropertyChanged(nameof(SaveStateBrush));
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SavePendingInfoVisible));
    }
    partial void OnIsNewDocumentChanged(bool value) => RaiseViewFlags();
    partial void OnSaveErrorMessageChanged(string value) => RaiseViewFlags();
    partial void OnRoleChanged(DocumentRole value) => RaiseViewFlags();
    partial void OnCurrentRelativePathChanged(string value) => RaiseViewFlags();
    partial void OnIsImagePreviewChanged(bool value) => RaiseViewFlags();
    partial void OnIsExtractedPreviewChanged(bool value) => RaiseViewFlags();
    partial void OnHideChromeChanged(bool value) => OnPropertyChanged(nameof(ChromeVisibility));
    partial void OnSavePendingWarningChanged(string value) => OnPropertyChanged(nameof(SavePendingInfoVisible));

    private void RaiseViewFlags()
    {
        OnPropertyChanged(nameof(WorkspaceFrameVisibility));
        OnPropertyChanged(nameof(IsPreviewContentVisible));
        OnPropertyChanged(nameof(IsMarkdownPreviewVisible));
        OnPropertyChanged(nameof(IsPlainPreviewVisible));
        OnPropertyChanged(nameof(IsImagePreviewVisible));
        OnPropertyChanged(nameof(IsEditingContentVisible));
        OnPropertyChanged(nameof(IsReadOnlyMode));
        OnPropertyChanged(nameof(IsPreviewEditable));
        OnPropertyChanged(nameof(IsNewDocumentVisible));
        OnPropertyChanged(nameof(HasSaveError));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PathBreadcrumb));
        OnPropertyChanged(nameof(FileTypeLabel));
        OnPropertyChanged(nameof(IsKnowledgeRevision));
        OnPropertyChanged(nameof(IsSourceDocument));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CurrentVersion));
        OnPropertyChanged(nameof(ChromeVisibility));
    }

    public void ResetToEmpty()
    {
        Mode = WorkspaceMode.Empty;
        SaveState = WorkspaceSaveState.None;
        Role = DocumentRole.None;
        IsNewDocument = false;
        EditorContent = "";
        PreviewContent = "";
        CurrentFileName = "";
        CurrentRelativePath = "";
        NewFileName = "";
        SaveErrorMessage = "";
        ReadOnlyMessage = "";
        PreviewImage = null;
        IsImagePreview = false;
        IsExtractedPreview = false;
        _savedContent = "";
        _diskHash = "";
        _kb = "";
        HideChrome = false;
        SuppressFirstH1 = false;
    }

    public void OpenSourceText(string kb, string rel)
    {
        HideChrome = false;
        var preview = _preview.Get(kb, rel);
        if (preview.IsImage)
        {
            OpenImage(kb, rel, preview);
            return;
        }
        if (preview.IsExtractedText)
        {
            OpenExtracted(kb, rel, preview);
            return;
        }
        if (!preview.IsText)
        {
            OpenReadOnlyInfo(kb, rel, preview.Warning);
            return;
        }
        if (!_sources.TryReadText(kb, rel, out var content, out var hash, out var error))
        {
            OpenReadOnlyInfo(kb, rel, error);
            return;
        }
        _kb = kb;
        _diskHash = hash;
        _savedContent = content;
        PreviewContent = content;
        CurrentRelativePath = ChangeService.Normalize(rel);
        CurrentFileName = Path.GetFileName(rel);
        IsNewDocument = false;
        NewFileName = "";
        SaveErrorMessage = "";
        ReadOnlyMessage = "";
        PreviewImage = null;
        IsImagePreview = false;
        IsExtractedPreview = false;
        Role = DocumentRole.Source;
        Mode = WorkspaceMode.Preview;
        SaveState = WorkspaceSaveState.Saved;
    }

    private void OpenImage(string kb, string rel, SourcePreview preview)
    {
        _kb = kb;
        Role = DocumentRole.Source;
        Mode = WorkspaceMode.ReadOnly;
        CurrentRelativePath = ChangeService.Normalize(rel);
        CurrentFileName = Path.GetFileName(rel);
        IsNewDocument = false;
        PreviewContent = "";
        ReadOnlyMessage = preview.Warning;
        SaveErrorMessage = "";
        SaveState = WorkspaceSaveState.Saved;
        _diskHash = _sources.Hash(kb, rel);
        _savedContent = "";
        IsExtractedPreview = false;
        IsImagePreview = true;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(preview.ImagePath!, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            PreviewImage = bmp;
        }
        catch (Exception e)
        {
            IsImagePreview = false;
            PreviewImage = null;
            OpenReadOnlyInfo(kb, rel, "图片解码失败：" + e.Message);
        }
    }

    private void OpenExtracted(string kb, string rel, SourcePreview preview)
    {
        _kb = kb;
        Role = DocumentRole.Source;
        Mode = WorkspaceMode.ReadOnly;
        CurrentRelativePath = ChangeService.Normalize(rel);
        CurrentFileName = Path.GetFileName(rel);
        IsNewDocument = false;
        PreviewContent = preview.Text;
        ReadOnlyMessage = preview.Title + " · " + preview.Warning;
        SaveErrorMessage = "";
        SaveState = WorkspaceSaveState.Saved;
        _diskHash = _sources.Hash(kb, rel);
        _savedContent = "";
        PreviewImage = null;
        IsImagePreview = false;
        IsExtractedPreview = true;
    }

    /// <summary>非文本资料：只展示文件信息，用系统程序打开；不在文本框里读二进制。</summary>
    public void OpenReadOnlyInfo(string kb, string rel, string? message = null)
    {
        _kb = kb;
        CurrentRelativePath = ChangeService.Normalize(rel);
        CurrentFileName = Path.GetFileName(rel);
        Role = DocumentRole.Source;
        Mode = WorkspaceMode.ReadOnly;
        IsNewDocument = false;
        ReadOnlyMessage = message ?? "此格式保持原文件保存，可在系统程序中打开或导出。";
        PreviewContent = ReadOnlyMessage;
        PreviewImage = null;
        IsImagePreview = false;
        IsExtractedPreview = false;
        SaveState = WorkspaceSaveState.Saved;
        SaveErrorMessage = "";
        _diskHash = _sources.Hash(kb, rel);
        _savedContent = "";
    }

    public void OpenKnowledgePage(string kb, string rel)
    {
        if (!_knowledge.TryRead(kb, rel, out var content, out var hash, out var error))
        {
            OpenReadOnlyInfo(kb, rel, error);
            return;
        }
        _kb = kb;
        _diskHash = hash;
        _savedContent = content;
        PreviewContent = content;
        CurrentRelativePath = ChangeService.Normalize(rel);
        CurrentFileName = Path.GetFileName(rel);
        IsNewDocument = false;
        NewFileName = "";
        SaveErrorMessage = "";
        PreviewImage = null;
        IsImagePreview = false;
        IsExtractedPreview = false;
        ReadOnlyMessage = "此内容通常由外部 AI 维护；默认只读。";
        Role = DocumentRole.Knowledge;
        Mode = WorkspaceMode.Preview;
        SaveState = WorkspaceSaveState.Saved;
        HideChrome = true;
    }

    /// <summary>高级入口“手动修订知识…”：显式进入编辑，普通工具栏不出现编辑按钮。</summary>
    public bool BeginKnowledgeRevision()
    {
        if (Role != DocumentRole.Knowledge || Mode != WorkspaceMode.Preview || !CanEdit) return false;
        EditorContent = PreviewContent;
        _savedContent = PreviewContent;
        Mode = WorkspaceMode.Editing;
        SaveState = WorkspaceSaveState.Saved;
        return true;
    }

    [RelayCommand]
    private void EnterEditMode()
    {
        if (Role != DocumentRole.Source || Mode != WorkspaceMode.Preview) return;
        EditorContent = PreviewContent;
        _savedContent = PreviewContent;
        Mode = WorkspaceMode.Editing;
        SaveState = WorkspaceSaveState.Saved;
    }

    public void EnterNewSource(string kb)
    {
        _kb = kb;
        Role = DocumentRole.Source;
        IsNewDocument = true;
        NewFileName = "";
        EditorContent = "";
        _savedContent = "";
        _diskHash = "";
        CurrentFileName = "";
        CurrentRelativePath = "";
        SaveErrorMessage = "";
        Mode = WorkspaceMode.Editing;
        SaveState = WorkspaceSaveState.None;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        await SaveAsync();
    }

    /// <summary>“文档无变化保存禁用”（00§9.4）：仅编辑中、未在保存中且有变化（或新建）时可用。</summary>
    private bool CanSave() => Mode == WorkspaceMode.Editing
                              && SaveState != WorkspaceSaveState.Saving
                              && (IsNewDocument || SaveState == WorkspaceSaveState.Dirty);

    /// <summary>D13“保存并取消任务后退出”：直接保存当前草稿，不再询问。</summary>
    public Task<bool> TrySaveAsync()
    {
        if (!HasUnsavedChanges) return Task.FromResult(true);
        return SaveAsync();
    }

    /// <summary>D13“放弃草稿”：只丢本次缓冲，不回滚已落盘内容。</summary>
    public void DiscardDraft()
    {
        if (Mode != WorkspaceMode.Editing) return;
        if (IsNewDocument)
        {
            ResetToEmpty();
            return;
        }
        EditorContent = _savedContent;
        PreviewContent = _savedContent;
        Mode = Role == DocumentRole.Source || Role == DocumentRole.Knowledge ? WorkspaceMode.Preview : WorkspaceMode.ReadOnly;
        SaveState = WorkspaceSaveState.Saved;
        SaveErrorMessage = "";
    }

    public async Task<bool> SaveAsync()
    {
        if (Mode != WorkspaceMode.Editing || SaveState == WorkspaceSaveState.Saving) return false;
        SaveErrorMessage = "";

        if (IsNewDocument)
        {
            var name = (NewFileName ?? "").Trim();
            var created = _sources.CreateText(_kb, name, EditorContent);
            if (created.Status != SourceImportStatus.Added)
            {
                SaveState = WorkspaceSaveState.Failed;
                SaveErrorMessage = created.Message;
                return false;
            }
            IsNewDocument = false;
            CurrentRelativePath = created.RelPath;
            CurrentFileName = Path.GetFileName(created.RelPath);
            _diskHash = created.Hash;
            _savedContent = EditorContent;
            PreviewContent = EditorContent;
            Mode = WorkspaceMode.Preview;
            SaveState = WorkspaceSaveState.Saved;
            SavePendingWarning = "";
            DocumentSaved?.Invoke();
            // N01：文件保存事实（00§9.6），不代表知识已整理。
            _toast.ShowSuccess($"{CurrentFileName} 已保存");
            return true;
        }

        SaveState = WorkspaceSaveState.Saving;
        if (Role == DocumentRole.Knowledge)
        {
            var result = _knowledge.SaveManualRevision(_kb, CurrentRelativePath, EditorContent, _diskHash);
            if (!result.Success)
            {
                SaveState = WorkspaceSaveState.Failed;
                SaveErrorMessage = result.Message;
                return false;
            }
            _diskHash = result.Hash;
            _savedContent = EditorContent;
            PreviewContent = EditorContent;
            SaveState = WorkspaceSaveState.Saved;
            SavePendingWarning = "";
            _toast.ShowSuccess($"{CurrentFileName} 已保存");
            return true;
        }

        var save = _sources.SaveText(_kb, CurrentRelativePath, EditorContent, _diskHash);
        if (!save.Success)
        {
            SaveState = WorkspaceSaveState.Failed;
            SaveErrorMessage = save.Message;
            return false;
        }
        _diskHash = save.NewHash;
        _savedContent = EditorContent;
        PreviewContent = EditorContent;
        SaveState = WorkspaceSaveState.Saved;
        DocumentSaved?.Invoke();
        // 文件已写入但变更/日志登记未完成：持久提醒（00§9.7），不用成功通知宣称全部完成。
        SavePendingWarning = string.IsNullOrEmpty(save.Message) ? "" : save.Message;
        _toast.ShowSuccess($"{CurrentFileName} 已保存");
        return true;
    }

    [RelayCommand]
    private async Task CancelEditAsync()
    {
        if (HasUnsavedChanges)
        {
            var choice = await AskUnsavedAsync();
            if (choice == UnsavedChoice.Continue) return;
            if (choice == UnsavedChoice.SaveAndLeave && !await SaveAsync()) return;
        }
        ExitToPreview();
    }

    /// <summary>“完成编辑”：未保存时统一保护，成功后回到预览。</summary>
    [RelayCommand]
    private async Task CompleteEditingAsync()
    {
        if (HasUnsavedChanges)
        {
            var choice = await AskUnsavedAsync();
            if (choice == UnsavedChoice.Continue) return;
            if (choice == UnsavedChoice.SaveAndLeave && !await SaveAsync()) return;
        }
        ExitToPreview();
    }

    private void ExitToPreview()
    {
        if (IsNewDocument)
        {
            ResetToEmpty();
            return;
        }
        EditorContent = _savedContent;
        PreviewContent = _savedContent;
        Mode = Role == DocumentRole.Source || Role == DocumentRole.Knowledge ? WorkspaceMode.Preview : WorkspaceMode.ReadOnly;
        SaveState = WorkspaceSaveState.Saved;
        SaveErrorMessage = "";
    }

    [RelayCommand]
    private void OpenWithDefaultProgram()
    {
        var full = _sources.FullPathFor(_kb, CurrentRelativePath);
        if (full == null || !File.Exists(full))
        {
            _toast.ShowError("文件不存在：" + CurrentRelativePath);
            return;
        }
        if (!SourceRules.IsText(CurrentFileName))
        {
            _toast.ShowWarning("修改并保存原文件后，请返回应用刷新。");
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _toast.ShowError("无法打开文件：" + e.Message);
        }
    }

    /// <summary>D04 磁盘版本冲突（02§9.1）：错误区提供 返回编辑 / 另存副本 / 载入磁盘版本 三个动作。</summary>
    public Func<Task<bool>>? ConfirmLoadDiskVersion { get; set; }

    /// <summary>返回编辑：只清错误提示，草稿与冲突状态保留，可继续修改或另存。</summary>
    [RelayCommand]
    private void ReturnToEditing()
    {
        SaveErrorMessage = "磁盘上的文件已有更新版本；当前显示的是你的草稿。可继续编辑或另存副本。";
        SaveState = WorkspaceSaveState.Dirty;
    }

    /// <summary>另存副本：把当前草稿写入同目录新文件，原件不动；草稿保留。</summary>
    [RelayCommand]
    private void SaveDraftCopy()
    {
        var ext = Path.GetExtension(CurrentRelativePath);
        var baseName = Path.GetFileNameWithoutExtension(CurrentRelativePath);
        var dir = Path.GetDirectoryName(CurrentRelativePath)?.Replace('\\', '/') ?? "";
        var copyRel = string.IsNullOrEmpty(dir) ? $"{baseName}.副本-{DateTime.Now:yyyyMMdd-HHmmss}{ext}" : $"{dir}/{baseName}.副本-{DateTime.Now:yyyyMMdd-HHmmss}{ext}";
        var created = _sources.CreateText(_kb, copyRel, EditorContent);
        if (created.Status != SourceImportStatus.Added)
        {
            SaveErrorMessage = "另存副本失败：" + created.Message;
            return;
        }
        SaveErrorMessage = $"已另存副本：{created.RelPath}。当前编辑的仍是原文件。";
        _toast.ShowSuccess($"已另存副本：{created.RelPath}");
    }

    /// <summary>载入磁盘版本：需要显式确认，确认后才丢弃当前草稿。</summary>
    [RelayCommand]
    private async Task LoadDiskVersionAsync()
    {
        if (ConfirmLoadDiskVersion != null && !await ConfirmLoadDiskVersion()) return;
        if (Role == DocumentRole.Knowledge)
        {
            OpenKnowledgePage(_kb, CurrentRelativePath);
        }
        else
        {
            OpenSourceText(_kb, CurrentRelativePath);
        }
        _toast.ShowWarning("已载入磁盘版本，未保存草稿已丢弃。");
    }

    public async Task<bool> CanLeaveAsync(CancellationToken cancellationToken = default)
    {
        if (!HasUnsavedChanges) return true;
        var choice = await AskUnsavedAsync();
        if (choice == UnsavedChoice.Continue) return false;
        if (choice == UnsavedChoice.SaveAndLeave) return await SaveAsync();
        return true;
    }

    private async Task<UnsavedChoice> AskUnsavedAsync()
        => ConfirmUnsaved == null ? UnsavedChoice.Continue : await ConfirmUnsaved();
}
