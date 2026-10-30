using System.ComponentModel;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.Share;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages;

/// <summary>
/// 知识库设置：基本信息、目标与结构（purpose.md / schema.md 独立草稿与保存状态）、危险删库。
/// 两个编辑器共用一个离开保护：保存全部失败则不继续离开；放弃更改从磁盘重读还原（05§4）。
/// Purpose 与 Schema 各自维护 Dirty/Busy/冲突状态，保存其一不影响另一编辑区（05§3）。
/// </summary>
public partial class KbSettingsViewModel : ObservableObject, IUnsavedChangesGuard
{
    private readonly MainWindowViewModel _main;
    private readonly RuleService _rules;
    private readonly ReviewService _reviews;
    private readonly UnsavedChangesCoordinator _unsaved;
    private readonly ToastService _toast;
    private readonly ShareService _share;

    private string _purposeBase = "";
    private string _schemaBase = "";
    private string _purposeHash = "";
    private string _schemaHash = "";

    public KbSettingsViewModel(
        MainWindowViewModel main,
        RuleService rules,
        ReviewService reviews,
        UnsavedChangesCoordinator unsaved,
        ToastService toast,
        ShareService share)
    {
        _main = main;
        _rules = rules;
        _reviews = reviews;
        _unsaved = unsaved;
        _toast = toast;
        _share = share;
        _unsaved.Register("kb-settings", this);
        // D13 退出保护：登记“直接保存”与“放弃草稿”委托（放弃 = 磁盘重读，见 DiscardChanges）。
        _unsaved.RegisterSaver("kb-settings", SaveAllAsync);
        _unsaved.RegisterDiscarder("kb-settings", DiscardChanges);
        _main.PropertyChanged += OnMainPropertyChanged;
        LoadKb(_main.CurrentKb);
    }

    public Func<Task<UnsavedChoice>>? ConfirmUnsaved { get; set; }

    /// <summary>D14 默认模板替换草稿：参数为目标名与模板摘要（前几行），取消侧为“保留当前草稿”。</summary>
    public Func<string, string, Task<bool>>? ConfirmReplaceDraft { get; set; }

    /// <summary>D04 磁盘版本冲突：确认“载入磁盘版本”（danger，初焦取消）。</summary>
    public Func<string, Task<bool>>? ConfirmLoadDiskVersion { get; set; }

    /// <summary>D07 永久删除知识库：640 宽危险确认框。</summary>
    public Func<Task<bool>>? ConfirmDeleteLibrary { get; set; }

    [ObservableProperty] private string _selectedKb = "";
    [ObservableProperty] private string _kbPath = "";
    [ObservableProperty] private bool _directoryOk;
    [ObservableProperty] private string _purposeDraft = "";
    [ObservableProperty] private string _schemaDraft = "";
    [ObservableProperty] private string _purposeStatus = "";
    [ObservableProperty] private string _schemaStatus = "";

    /// <summary>编辑区内联显示的失败/冲突原因（05§2：错误留在对应编辑区）。</summary>
    [ObservableProperty] private string _purposeError = "";
    [ObservableProperty] private string _schemaError = "";

    /// <summary>D04：保存时发现磁盘版本已离开基线，就地显示冲突与两个动作按钮（05§8.1）。</summary>
    [ObservableProperty] private bool _purposeConflict;
    [ObservableProperty] private bool _schemaConflict;

    [ObservableProperty] private bool _purposeExists;
    [ObservableProperty] private bool _schemaExists;

    /// <summary>保存中各自 Busy（05§2/§3）：保存目标不再锁规则编辑区。</summary>
    [ObservableProperty] private bool _purposeBusy;
    [ObservableProperty] private bool _schemaBusy;

    [ObservableProperty] private bool _ruleNeedsReview;

    /// <summary>D07 阻断：存在未保存草稿时，危险区就地 InfoBar 提示，不弹确认框（05§8.2）。</summary>
    [ObservableProperty] private bool _deleteBlockedByDraft;

    [ObservableProperty] private string _deleteConfirmText = "";

    public bool HasKb => _main.HasKb;
    public bool PurposeDirty => PurposeDraft != _purposeBase;
    public bool SchemaDirty => SchemaDraft != _schemaBase;
    public bool HasUnsavedChanges => PurposeDirty || SchemaDirty;
    public bool HasPurposeError => PurposeError.Length > 0;
    public bool HasSchemaError => SchemaError.Length > 0;
    public bool CanDelete => !string.IsNullOrWhiteSpace(DeleteConfirmText)
                             && DeleteConfirmText.Trim().Equals(SelectedKb, StringComparison.Ordinal);
    public string DirectoryStatus => DirectoryOk ? "目录可访问" : "目录不可访问";

    /// <summary>状态徽章语义（05§2）：Conflict/Failed/Saving/Dirty/Missing/Unchanged，驱动小圆点语义色。</summary>
    public string PurposeState => PurposeConflict ? "Conflict"
        : PurposeBusy ? "Saving"
        : PurposeError.Length > 0 ? "Failed"
        : PurposeDirty ? "Dirty"
        : !PurposeExists ? "Missing"
        : "Unchanged";

    public string SchemaState => SchemaConflict ? "Conflict"
        : SchemaBusy ? "Saving"
        : SchemaError.Length > 0 ? "Failed"
        : SchemaDirty ? "Dirty"
        : !SchemaExists ? "Missing"
        : "Unchanged";

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb)) LoadKb(_main.CurrentKb);
    }

    public void LoadKb(string kb)
    {
        SelectedKb = kb;
        DeleteConfirmText = "";
        DeleteBlockedByDraft = false;
        OnPropertyChanged(nameof(HasKb));
        if (string.IsNullOrWhiteSpace(kb) || !_main.HasKb)
        {
            KbPath = "";
            DirectoryOk = false;
            _purposeBase = "";
            _schemaBase = "";
            _purposeHash = "";
            _schemaHash = "";
            PurposeDraft = "";
            SchemaDraft = "";
            PurposeExists = false;
            SchemaExists = false;
            PurposeConflict = false;
            SchemaConflict = false;
            PurposeError = "";
            SchemaError = "";
            PurposeStatus = "未选择知识库";
            SchemaStatus = "未选择知识库";
            OnPropertyChanged(nameof(HasUnsavedChanges));
            RefreshPurposeStatus();
            RefreshSchemaStatus();
            return;
        }

        KbPath = Path.Combine(_main.CurrentKbRoot);
        DirectoryOk = Directory.Exists(KbPath);
        LoadRule("purpose.md");
        LoadRule("schema.md");
        RuleNeedsReview = _reviews.GetRuleStatus(kb).NeedsReview;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        // 切库后草稿值可能恰好与旧值相同（不触发 PropertyChanged），显式刷新保存命令与状态徽章
        RefreshPurposeStatus();
        RefreshSchemaStatus();
    }

    /// <summary>从磁盘读取单个规则文件，草稿与基线都还原为磁盘事实（加载与“放弃更改”共用）。</summary>
    private void LoadRule(string file)
    {
        var isPurpose = file == "purpose.md";
        if (!_rules.TryRead(SelectedKb, file, out var content, out var hash, out var exists, out var error))
        {
            if (isPurpose)
            {
                _purposeBase = "";
                _purposeHash = "";
                PurposeDraft = "";
                PurposeExists = false;
                PurposeConflict = false;
                PurposeError = error;
                PurposeStatus = "读取失败，编辑区已清空";
            }
            else
            {
                _schemaBase = "";
                _schemaHash = "";
                SchemaDraft = "";
                SchemaExists = false;
                SchemaConflict = false;
                SchemaError = error;
                SchemaStatus = "读取失败，编辑区已清空";
            }
            return;
        }

        if (isPurpose)
        {
            _purposeBase = content;
            _purposeHash = hash;
            PurposeDraft = content;
            PurposeExists = exists;
            PurposeConflict = false;
            PurposeError = "";
            PurposeStatus = exists ? "未修改" : "目标文件缺失";
        }
        else
        {
            _schemaBase = content;
            _schemaHash = hash;
            SchemaDraft = content;
            SchemaExists = exists;
            SchemaConflict = false;
            SchemaError = "";
            SchemaStatus = exists ? "未修改" : "规则文件缺失";
        }
    }

    partial void OnPurposeDraftChanged(string value)
    {
        PurposeError = ""; // 重新编辑后清除上一次的失败原因
        if (!HasUnsavedChanges) DeleteBlockedByDraft = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        RefreshPurposeStatus();
    }

    partial void OnSchemaDraftChanged(string value)
    {
        SchemaError = "";
        if (!HasUnsavedChanges) DeleteBlockedByDraft = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        RefreshSchemaStatus();
    }

    partial void OnPurposeErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasPurposeError));
        RefreshPurposeStatus();
    }

    partial void OnSchemaErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasSchemaError));
        RefreshSchemaStatus();
    }

    partial void OnPurposeConflictChanged(bool value) => RefreshPurposeStatus();

    partial void OnSchemaConflictChanged(bool value) => RefreshSchemaStatus();

    partial void OnPurposeBusyChanged(bool value) => RefreshPurposeStatus();

    partial void OnSchemaBusyChanged(bool value) => RefreshSchemaStatus();

    partial void OnDirectoryOkChanged(bool value)
    {
        OnPropertyChanged(nameof(DirectoryStatus));
        DeleteLibraryCommand.NotifyCanExecuteChanged();
    }

    partial void OnDeleteConfirmTextChanged(string value) => DeleteLibraryCommand.NotifyCanExecuteChanged();

    private void RefreshPurposeStatus()
    {
        OnPropertyChanged(nameof(PurposeState));
        SavePurposeCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSchemaStatus()
    {
        OnPropertyChanged(nameof(SchemaState));
        SaveSchemaCommand.NotifyCanExecuteChanged();
    }

    private bool CanSavePurpose() => PurposeDirty && !PurposeBusy;
    private bool CanSaveSchema() => SchemaDirty && !SchemaBusy;
    private bool CanDeleteLibrary() => HasKb && DirectoryOk && CanDelete;

    [RelayCommand(CanExecute = nameof(CanSavePurpose))]
    private async Task SavePurposeAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb) || !PurposeDirty) return;
        PurposeBusy = true;
        PurposeStatus = "保存中…";
        try
        {
            var result = _rules.Save(SelectedKb, "purpose.md", PurposeDraft, _purposeHash);
            if (!result.Success)
            {
                PurposeError = result.Message;
                if (DiskVersionChanged("purpose.md", _purposeHash))
                {
                    // D04：磁盘版本已离开基线，就地显示冲突，不静默覆盖（05§4/§8.1）
                    PurposeConflict = true;
                    PurposeStatus = "磁盘版本已变化，未写入";
                }
                else
                {
                    PurposeStatus = "保存失败，内容已保留";
                }
                return;
            }

            _purposeBase = PurposeDraft;
            _purposeHash = result.Hash;
            PurposeExists = true;
            PurposeConflict = false;
            PurposeError = "";
            PurposeStatus = "未修改";
            RuleNeedsReview = _reviews.GetRuleStatus(SelectedKb).NeedsReview;
            _toast.ShowSuccess("目标已保存");
        }
        finally
        {
            PurposeBusy = false;
            RefreshPurposeStatus();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveSchema))]
    private async Task SaveSchemaAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb) || !SchemaDirty) return;
        SchemaBusy = true;
        SchemaStatus = "保存中…";
        try
        {
            var result = _rules.Save(SelectedKb, "schema.md", SchemaDraft, _schemaHash);
            if (!result.Success)
            {
                SchemaError = result.Message;
                if (DiskVersionChanged("schema.md", _schemaHash))
                {
                    SchemaConflict = true;
                    SchemaStatus = "磁盘版本已变化，未写入";
                }
                else
                {
                    SchemaStatus = "保存失败，内容已保留";
                }
                return;
            }

            _schemaBase = SchemaDraft;
            _schemaHash = result.Hash;
            SchemaExists = true;
            SchemaConflict = false;
            SchemaError = "";
            SchemaStatus = "未修改";
            RuleNeedsReview = _reviews.GetRuleStatus(SelectedKb).NeedsReview;
            _toast.ShowSuccess("组织规则已保存");
        }
        finally
        {
            SchemaBusy = false;
            RefreshSchemaStatus();
        }
    }

    /// <summary>保存失败时核对磁盘当前版本是否已离开保存时的基线（D04 冲突判定）。</summary>
    private bool DiskVersionChanged(string file, string expectedHash)
    {
        if (!_rules.TryRead(SelectedKb, file, out _, out var diskHash, out var exists, out _)) return false;
        if (!exists) return !string.IsNullOrEmpty(expectedHash);
        return !string.Equals(diskHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>D04 动作一：载入磁盘版本（danger 确认后），重读磁盘并丢弃该草稿。</summary>
    [RelayCommand]
    private async Task LoadDiskPurposeAsync()
    {
        if (ConfirmLoadDiskVersion != null && !await ConfirmLoadDiskVersion("目标与边界（purpose.md）")) return;
        LoadRule("purpose.md");
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    [RelayCommand]
    private async Task LoadDiskSchemaAsync()
    {
        if (ConfirmLoadDiskVersion != null && !await ConfirmLoadDiskVersion("组织规则（schema.md）")) return;
        LoadRule("schema.md");
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>D04 动作二：另存草稿副本到剪贴板（零副作用，不在库目录写入非契约文件），并明确“原规则未更改”。</summary>
    [RelayCommand]
    private void CopyPurposeDraftCopy()
    {
        if (PurposeDraft.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(PurposeDraft);
            _toast.ShowSuccess("草稿副本已复制到剪贴板；磁盘上的原规则未更改");
        }
        catch
        {
            _toast.ShowError("复制剪贴板失败，请手动选择文本复制");
        }
    }

    [RelayCommand]
    private void CopySchemaDraftCopy()
    {
        if (SchemaDraft.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(SchemaDraft);
            _toast.ShowSuccess("草稿副本已复制到剪贴板；磁盘上的原规则未更改");
        }
        catch
        {
            _toast.ShowError("复制剪贴板失败，请手动选择文本复制");
        }
    }

    /// <summary>保存全部：任一失败返回 false，失败草稿保留，成功项不回滚（05§4）。</summary>
    public async Task<bool> SaveAllAsync()
    {
        var ok = true;
        if (PurposeDirty) { await SavePurposeAsync(); ok &= !PurposeDirty; }
        if (SchemaDirty) { await SaveSchemaAsync(); ok &= !SchemaDirty; }
        return ok;
    }

    /// <summary>放弃两份草稿：从磁盘重读，草稿与基线都还原为磁盘事实（D02 放弃语义，05§4）。</summary>
    public void DiscardChanges()
    {
        LoadRule("purpose.md");
        LoadRule("schema.md");
        DeleteBlockedByDraft = false;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>D14：缺文件且编辑区空白时直接填入；已有输入时经确认后替换草稿，标未保存、不落盘。</summary>
    [RelayCommand]
    private async Task LoadDefaultPurposeAsync()
    {
        var (purpose, _) = _rules.DefaultTemplates(SelectedKb);
        if (!string.IsNullOrWhiteSpace(PurposeDraft) && ConfirmReplaceDraft != null
            && !await ConfirmReplaceDraft("目标与边界（purpose.md）", SummarizeTemplate(purpose)))
            return;
        PurposeDraft = purpose + "\n";
        PurposeStatus = "未保存";
    }

    [RelayCommand]
    private async Task LoadDefaultSchemaAsync()
    {
        var (_, schema) = _rules.DefaultTemplates(SelectedKb);
        if (!string.IsNullOrWhiteSpace(SchemaDraft) && ConfirmReplaceDraft != null
            && !await ConfirmReplaceDraft("组织规则（schema.md）", SummarizeTemplate(schema)))
            return;
        SchemaDraft = schema + "\n";
        SchemaStatus = "未保存";
    }

    /// <summary>D14 detail：将填入模板的摘要（前 6 行）。</summary>
    private static string SummarizeTemplate(string template)
    {
        var lines = template.Replace("\r", "").Split('\n');
        var head = lines.Take(6).ToArray();
        var summary = string.Join("\n", head);
        return lines.Length > head.Length ? summary + "\n……" : summary;
    }

    /// <summary>D07（05§8.2）：先检查草稿（就地 InfoBar，不弹框），再走 640 宽危险确认；随后停止检查共享服务并删除。</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteLibrary))]
    private async Task DeleteLibraryAsync()
    {
        if (!HasKb) return;
        if (HasUnsavedChanges)
        {
            DeleteBlockedByDraft = true;
            return;
        }
        DeleteBlockedByDraft = false;
        if (ConfirmDeleteLibrary != null && !await ConfirmDeleteLibrary()) return;

        // 共享服务停止检查：绑定本库的浏览分享必须先停；停止后仍在运行则拒绝删除。
        // （ShareService.Stop() 无返回值且内部吞异常，只能核对 Running 状态；真实失败信号待后续改 ShareService。）
        if (_share.Running && _share.BoundKb.Equals(SelectedKb, StringComparison.OrdinalIgnoreCase))
        {
            _share.Stop();
            if (_share.Running)
            {
                _toast.ShowError("浏览分享服务停止失败，未删除知识库");
                return;
            }
        }

        if (await _main.DeleteCurrentKbAsync()) DeleteConfirmText = "";
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (string.IsNullOrWhiteSpace(KbPath)) return;
        try
        {
            System.Windows.Clipboard.SetText(KbPath);
            _toast.ShowSuccess("已复制知识库路径");
        }
        catch { _toast.ShowError("复制剪贴板失败"); }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (string.IsNullOrWhiteSpace(KbPath) || !Directory.Exists(KbPath)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(KbPath) { UseShellExecute = true }); }
        catch (Exception e) { _toast.ShowError("打开文件夹失败：" + e.Message); }
    }

    [RelayCommand]
    private void AcknowledgeRules()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        _reviews.AcknowledgeRules(SelectedKb);
        RuleNeedsReview = false;
        _toast.ShowSuccess("已确认按新规则核对现有知识");
    }

    [RelayCommand]
    private void IgnoreRules()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        _reviews.IgnoreCurrentRules(SelectedKb);
        RuleNeedsReview = false;
        _toast.ShowSuccess("已稍后处理当前规则提醒；规则再次变化时仍会提醒");
    }

    public async Task<bool> CanLeaveAsync(CancellationToken cancellationToken = default)
    {
        if (!HasUnsavedChanges) return true;
        var choice = ConfirmUnsaved == null ? UnsavedChoice.Continue : await ConfirmUnsaved();
        if (choice == UnsavedChoice.Continue) return false;
        if (choice == UnsavedChoice.SaveAndLeave) return await SaveAllAsync();
        // 放弃更改：从磁盘重读还原草稿与基线（旧行为把被放弃的修改当基线，磁盘未写入却显示“已保存”——05§4）
        DiscardChanges();
        return true;
    }

    public void MarkVisited() => _main.RememberPage("kbsettings");
}
