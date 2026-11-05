using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
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

    private string _purposeBase = "";
    private string _schemaBase = "";
    private string _purposeHash = "";
    private string _schemaHash = "";

    public KbSettingsViewModel(
        MainWindowViewModel main,
        RuleService rules,
        ReviewService reviews,
        UnsavedChangesCoordinator unsaved,
        ToastService toast)
    {
        _main = main;
        _rules = rules;
        _reviews = reviews;
        _unsaved = unsaved;
        _toast = toast;
        _unsaved.Register("kb-settings", this);
        // D13 退出保护：登记“直接保存”与“放弃草稿”委托（放弃 = 磁盘重读，见 DiscardChanges）。
        _unsaved.RegisterSaver("kb-settings", SaveAllAsync);
        _unsaved.RegisterDiscarder("kb-settings", DiscardChanges);
        _main.PropertyChanged += OnMainPropertyChanged;
        LoadKb(_main.CurrentKb);
        RefreshKbRows();
    }

    public Func<Task<UnsavedChoice>>? ConfirmUnsaved { get; set; }

    /// <summary>D14 默认模板替换草稿：参数为目标名与模板摘要（前几行），取消侧为“保留当前草稿”。</summary>
    public Func<string, string, Task<bool>>? ConfirmReplaceDraft { get; set; }

    /// <summary>D04 磁盘版本冲突：确认“载入磁盘版本”（danger，初焦取消）。</summary>
    public Func<string, Task<bool>>? ConfirmLoadDiskVersion { get; set; }

    /// <summary>D07 永久删除知识库：参数为待删除库名列表（批量勾选删除复用同一确认框）。</summary>
    public Func<IReadOnlyList<string>, Task<bool>>? ConfirmDeleteKb { get; set; }

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

    /// <summary>知识库管理列表：每行可勾选，支持批量删除；当前库带标记。</summary>
    public ObservableCollection<KbRowViewModel> KbRows { get; } = [];

    public bool HasKb => _main.HasKb;
    public bool PurposeDirty => PurposeDraft != _purposeBase;
    public bool SchemaDirty => SchemaDraft != _schemaBase;
    public bool HasUnsavedChanges => PurposeDirty || SchemaDirty;
    public bool HasPurposeError => PurposeError.Length > 0;
    public bool HasSchemaError => SchemaError.Length > 0;
    public string DirectoryStatus => DirectoryOk ? "目录可访问" : "目录不可访问";

    /// <summary>勾选数量与批量删除按钮的可用性/提示文案。</summary>
    public int SelectedKbCount => KbRows.Count(r => r.IsSelected);
    public bool HasKbSelection => SelectedKbCount > 0;
    public string SelectionSummary => SelectedKbCount == 0
        ? "勾选要删除的知识库"
        : $"已勾选 {SelectedKbCount} 个知识库";

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
        // 建库/删库后主列表整体替换，同步刷新管理列表
        else if (e.PropertyName == nameof(MainWindowViewModel.Kbs)) RefreshKbRows();
    }

    public void LoadKb(string kb)
    {
        SelectedKb = kb;
        DeleteBlockedByDraft = false;
        UpdateCurrentMarkers();
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

    partial void OnDirectoryOkChanged(bool value) => OnPropertyChanged(nameof(DirectoryStatus));

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

    /// <summary>
    /// 同步管理列表：勾选状态按库名保留，当前库标记跟随 SelectedKb。
    /// 主列表整体替换（建库/删库）后调用。
    /// </summary>
    public void RefreshKbRows()
    {
        var selected = KbRows.Where(r => r.IsSelected).Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        KbRows.Clear();
        foreach (var name in _main.Kbs)
        {
            KbRows.Add(new KbRowViewModel(name, OnKbSelectionChanged)
            {
                IsCurrent = string.Equals(name, SelectedKb, StringComparison.Ordinal),
                IsSelected = selected.Contains(name),
            });
        }
        NotifySelectionChanged();
    }

    /// <summary>切换当前库后同步列表里的“当前”标记（不重建集合，避免闪动）。</summary>
    private void UpdateCurrentMarkers()
    {
        foreach (var row in KbRows)
            row.IsCurrent = string.Equals(row.Name, SelectedKb, StringComparison.Ordinal);
    }

    private void OnKbSelectionChanged() => NotifySelectionChanged();

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedKbCount));
        OnPropertyChanged(nameof(HasKbSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        DeleteSelectedKbsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>列表行「切换」：与其他入口共用未保存草稿保护。</summary>
    [RelayCommand]
    private async Task SwitchKbAsync(KbRowViewModel? row)
    {
        if (row == null || row.IsCurrent) return;
        await _main.SwitchKbAsync(row.Name);
    }

    /// <summary>全选 / 取消全选。</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        // 已全选时变成全不选，否则一次选中全部
        var selectAll = !KbRows.All(r => r.IsSelected);
        foreach (var row in KbRows) row.IsSelected = selectAll;
    }

    /// <summary>列表行「删除」：单库删除，复用批量删除的确认与草稿保护。</summary>
    [RelayCommand]
    private Task DeleteKbRowAsync(KbRowViewModel? row)
        => row == null ? Task.CompletedTask : DeleteKbsAsync([row.Name]);

    /// <summary>勾选后批量删除。</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedKbsAsync()
        => DeleteKbsAsync(KbRows.Where(r => r.IsSelected).Select(r => r.Name).ToList());

    private bool CanDeleteSelected() => HasKbSelection;

    /// <summary>
    /// 删除一组知识库：涉及当前库时先做草稿阻断（就地 InfoBar，不弹框），
    /// 再统一确认一次；逐个删除使单个失败不影响其余库，最后汇总结果。
    /// </summary>
    private async Task DeleteKbsAsync(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;

        if (names.Contains(SelectedKb, StringComparer.Ordinal) && HasUnsavedChanges)
        {
            DeleteBlockedByDraft = true;
            return;
        }
        DeleteBlockedByDraft = false;

        if (ConfirmDeleteKb != null && !await ConfirmDeleteKb(names)) return;

        var failed = new List<string>();
        foreach (var name in names)
        {
            // 逐个调用：主视图负责停服务、切库；失败项收集后统一提示
            if (!await _main.DeleteKbAsync(name, showToast: false)) failed.Add(name);
        }

        RefreshKbRows();
        if (failed.Count == 0)
        {
            _toast.ShowSuccess(names.Count == 1
                ? "知识库已永久删除：" + names[0]
                : $"已删除 {names.Count} 个知识库");
        }
        else
        {
            _toast.ShowError("以下知识库未删除：" + string.Join("、", failed));
        }
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

/// <summary>
/// 知识库管理列表的一行：名称、是否当前库、是否勾选待删除。
/// 勾选变化通过回调通知父 ViewModel 刷新批量删除按钮状态。
/// </summary>
public partial class KbRowViewModel : SelectableRow
{
    private readonly Action _onSelectionChanged;

    public KbRowViewModel(string name, Action onSelectionChanged)
    {
        Name = name;
        _onSelectionChanged = onSelectionChanged;
    }

    public string Name { get; }

    [ObservableProperty] private bool _isCurrent;

    /// <summary>当前库禁用“切换”按钮。</summary>
    public bool IsNotCurrent => !IsCurrent;

    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(IsNotCurrent));
    protected override void OnSelectedChanged(bool value) => _onSelectionChanged();
}
