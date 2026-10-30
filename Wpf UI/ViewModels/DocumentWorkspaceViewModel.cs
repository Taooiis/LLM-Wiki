using System.IO;

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
    private readonly ToastService _toast;

    private string _kb = "";
    private string _diskHash = "";
    private string _savedContent = "";

    public DocumentWorkspaceViewModel(SourceService sources, KnowledgeService knowledge, ToastService toast)
    {
        _sources = sources;
        _knowledge = knowledge;
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

    public string NewFileDirectory { get; set; } = "raw/sources";

    public bool HasUnsavedChanges => Mode == WorkspaceMode.Editing && SaveState == WorkspaceSaveState.Dirty;

    public Visibility WorkspaceFrameVisibility => Mode == WorkspaceMode.Empty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility IsPreviewContentVisible => Mode is WorkspaceMode.Preview or WorkspaceMode.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsMarkdownPreviewVisible => Mode == WorkspaceMode.Preview && Role == DocumentRole.Knowledge ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsPlainPreviewVisible => Mode == WorkspaceMode.Preview && Role == DocumentRole.Source || Mode == WorkspaceMode.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsEditingContentVisible => Mode == WorkspaceMode.Editing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsReadOnlyMode => Mode == WorkspaceMode.ReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsPreviewEditable => Mode == WorkspaceMode.Preview && Role == DocumentRole.Source && CanEdit && !IsNewDocument ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsNewDocumentVisible => Mode == WorkspaceMode.Editing && IsNewDocument ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasSaveError => Mode == WorkspaceMode.Editing && SaveState == WorkspaceSaveState.Failed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowSaveBar => Mode == WorkspaceMode.Editing ? Visibility.Visible : Visibility.Collapsed;

    public bool CanEdit => Role == DocumentRole.Source
                           || (Role == DocumentRole.Knowledge && CurrentRelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                               && !CurrentRelativePath.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase));

    public string SaveStateText => SaveState switch
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
        WorkspaceSaveState.Saved => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0xCB, 0x5F)),
        WorkspaceSaveState.Dirty => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xE1, 0x00)),
        WorkspaceSaveState.Failed => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B)),
        _ => System.Windows.Media.Brushes.Transparent,
    };

    public string PathBreadcrumb => CurrentRelativePath;
    public string FileTypeLabel => SourceRules.TypeLabel(CurrentFileName);

    partial void OnEditorContentChanged(string value)
    {
        if (Mode != WorkspaceMode.Editing) return;
        SaveState = value == _savedContent ? WorkspaceSaveState.Saved : WorkspaceSaveState.Dirty;
    }

    partial void OnModeChanged(WorkspaceMode value) => RaiseViewFlags();
    partial void OnSaveStateChanged(WorkspaceSaveState value)
    {
        OnPropertyChanged(nameof(SaveStateText));
        OnPropertyChanged(nameof(SaveStateBrush));
    }
    partial void OnIsNewDocumentChanged(bool value) => RaiseViewFlags();
    partial void OnSaveErrorMessageChanged(string value) => RaiseViewFlags();
    partial void OnRoleChanged(DocumentRole value) => RaiseViewFlags();
    partial void OnCurrentRelativePathChanged(string value) => RaiseViewFlags();

    private void RaiseViewFlags()
    {
        OnPropertyChanged(nameof(WorkspaceFrameVisibility));
        OnPropertyChanged(nameof(IsPreviewContentVisible));
        OnPropertyChanged(nameof(IsMarkdownPreviewVisible));
        OnPropertyChanged(nameof(IsPlainPreviewVisible));
        OnPropertyChanged(nameof(IsEditingContentVisible));
        OnPropertyChanged(nameof(IsReadOnlyMode));
        OnPropertyChanged(nameof(IsPreviewEditable));
        OnPropertyChanged(nameof(IsNewDocumentVisible));
        OnPropertyChanged(nameof(HasSaveError));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PathBreadcrumb));
        OnPropertyChanged(nameof(FileTypeLabel));
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
        _savedContent = "";
        _diskHash = "";
        _kb = "";
    }

    public void OpenSourceText(string kb, string rel)
    {
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
        Role = DocumentRole.Source;
        Mode = WorkspaceMode.Preview;
        SaveState = WorkspaceSaveState.Saved;
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
        ReadOnlyMessage = "此内容通常由外部 AI 维护；默认只读。";
        Role = DocumentRole.Knowledge;
        Mode = WorkspaceMode.Preview;
        SaveState = WorkspaceSaveState.Saved;
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

    [RelayCommand]
    private async Task Save()
    {
        await SaveAsync();
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
            DocumentSaved?.Invoke();
            _toast.ShowSuccess("资料已保存，知识内容尚未更新。");
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
            _toast.ShowSuccess("已保存手动修订；后续整理会提示保留人工内容。");
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
        _toast.ShowSuccess("资料已保存，知识内容尚未更新。");
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

    /// <summary>外部冲突时由用户选择载入磁盘版本；会丢弃当前草稿。</summary>
    [RelayCommand]
    private void ReloadDiskVersion()
    {
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
