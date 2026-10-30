using System.ComponentModel;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages;

/// <summary>
/// 知识库设置：基本信息、目标与结构（purpose.md / schema.md 独立草稿与保存状态）、危险删库。
/// 两个编辑器共用一个离开保护：保存全部失败则不继续离开。
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
        _main.PropertyChanged += OnMainPropertyChanged;
        LoadKb(_main.CurrentKb);
    }

    public Func<Task<UnsavedChoice>>? ConfirmUnsaved { get; set; }

    [ObservableProperty] private string _selectedKb = "";
    [ObservableProperty] private string _kbPath = "";
    [ObservableProperty] private bool _directoryOk;
    [ObservableProperty] private string _purposeDraft = "";
    [ObservableProperty] private string _schemaDraft = "";
    [ObservableProperty] private string _purposeStatus = "";
    [ObservableProperty] private string _schemaStatus = "";
    [ObservableProperty] private bool _purposeExists;
    [ObservableProperty] private bool _schemaExists;
    [ObservableProperty] private bool _ruleNeedsReview;
    [ObservableProperty] private string _deleteConfirmText = "";
    [ObservableProperty] private bool _isBusy;

    public bool HasKb => _main.HasKb;
    public bool HasUnsavedChanges => PurposeDraft != _purposeBase || SchemaDraft != _schemaBase;
    public bool CanDelete => !string.IsNullOrWhiteSpace(DeleteConfirmText)
                             && DeleteConfirmText.Trim().Equals(SelectedKb, StringComparison.Ordinal);
    public string DirectoryStatus => DirectoryOk ? "目录可访问" : "目录不可访问";

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb)) LoadKb(_main.CurrentKb);
    }

    public void LoadKb(string kb)
    {
        SelectedKb = kb;
        DeleteConfirmText = "";
        OnPropertyChanged(nameof(HasKb));
        if (string.IsNullOrWhiteSpace(kb) || !_main.HasKb)
        {
            KbPath = "";
            DirectoryOk = false;
            PurposeDraft = "";
            SchemaDraft = "";
            _purposeBase = "";
            _schemaBase = "";
            PurposeStatus = "未选择知识库";
            SchemaStatus = "未选择知识库";
            PurposeExists = false;
            SchemaExists = false;
            return;
        }

        KbPath = Path.Combine(_main.CurrentKbRoot);
        DirectoryOk = Directory.Exists(KbPath);
        LoadRule("purpose.md");
        LoadRule("schema.md");
        RuleNeedsReview = _reviews.GetRuleStatus(kb).NeedsReview;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void LoadRule(string file)
    {
        if (!_rules.TryRead(SelectedKb, file, out var content, out var hash, out var exists, out var error))
        {
            if (file == "purpose.md")
            {
                PurposeDraft = "";
                _purposeBase = "";
                PurposeExists = false;
                PurposeStatus = error;
            }
            else
            {
                SchemaDraft = "";
                _schemaBase = "";
                SchemaExists = false;
                SchemaStatus = error;
            }
            return;
        }

        if (file == "purpose.md")
        {
            PurposeDraft = content;
            _purposeBase = content;
            _purposeHash = hash;
            PurposeExists = exists;
            PurposeStatus = exists ? "已保存" : "目标文件缺失";
        }
        else
        {
            SchemaDraft = content;
            _schemaBase = content;
            _schemaHash = hash;
            SchemaExists = exists;
            SchemaStatus = exists ? "已保存" : "规则文件缺失";
        }
    }

    partial void OnPurposeDraftChanged(string value)
    {
        PurposeStatus = value == _purposeBase ? (PurposeExists ? "已保存" : "目标文件缺失") : "未保存";
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnSchemaDraftChanged(string value)
    {
        SchemaStatus = value == _schemaBase ? (SchemaExists ? "已保存" : "规则文件缺失") : "未保存";
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnDeleteConfirmTextChanged(string value) => OnPropertyChanged(nameof(CanDelete));

    [RelayCommand]
    private async Task SavePurposeAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb))
        {
            _toast.ShowWarning("请先选择知识库");
            return;
        }
        if (PurposeDraft == _purposeBase)
        {
            PurposeStatus = "未修改";
            return;
        }
        IsBusy = true;
        try
        {
            var result = _rules.Save(SelectedKb, "purpose.md", PurposeDraft, _purposeHash);
            if (!result.Success)
            {
                PurposeStatus = "保存失败，内容已保留";
                _toast.ShowError("目标保存失败：" + result.Message);
                return;
            }
            _purposeBase = PurposeDraft;
            _purposeHash = result.Hash;
            PurposeExists = true;
            PurposeStatus = "已保存";
            RuleNeedsReview = _reviews.GetRuleStatus(SelectedKb).NeedsReview;
            _toast.ShowSuccess("目标已保存；已有知识尚未按新规则核对");
        }
        finally
        {
            IsBusy = false;
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveSchemaAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb))
        {
            _toast.ShowWarning("请先选择知识库");
            return;
        }
        if (SchemaDraft == _schemaBase)
        {
            SchemaStatus = "未修改";
            return;
        }
        IsBusy = true;
        try
        {
            var result = _rules.Save(SelectedKb, "schema.md", SchemaDraft, _schemaHash);
            if (!result.Success)
            {
                SchemaStatus = "保存失败，内容已保留";
                _toast.ShowError("规则保存失败：" + result.Message);
                return;
            }
            _schemaBase = SchemaDraft;
            _schemaHash = result.Hash;
            SchemaExists = true;
            SchemaStatus = "已保存";
            RuleNeedsReview = _reviews.GetRuleStatus(SelectedKb).NeedsReview;
            _toast.ShowSuccess("规则已保存；已有知识尚未按新规则核对");
        }
        finally
        {
            IsBusy = false;
        }
        await Task.CompletedTask;
    }

    /// <summary>保存全部：任一失败返回 false，失败草稿保留，成功项不回滚。</summary>
    public async Task<bool> SaveAllAsync()
    {
        var purposeDirty = PurposeDraft != _purposeBase;
        var schemaDirty = SchemaDraft != _schemaBase;
        var ok = true;
        if (purposeDirty) { await SavePurposeAsync(); ok &= !string.IsNullOrEmpty(_purposeHash) && PurposeDraft == _purposeBase; }
        if (schemaDirty) { await SaveSchemaAsync(); ok &= !string.IsNullOrEmpty(_schemaHash) && SchemaDraft == _schemaBase; }
        return ok;
    }

    [RelayCommand]
    private void LoadDefaultPurpose()
    {
        var (purpose, _) = _rules.DefaultTemplates(SelectedKb);
        PurposeDraft = purpose + "\n";
        PurposeStatus = "已填入默认模板，确认后保存";
    }

    [RelayCommand]
    private void LoadDefaultSchema()
    {
        var (_, schema) = _rules.DefaultTemplates(SelectedKb);
        SchemaDraft = schema + "\n";
        SchemaStatus = "已填入默认模板，确认后保存";
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

    public async Task<bool> CanLeaveAsync(CancellationToken cancellationToken = default)
    {
        if (!HasUnsavedChanges) return true;
        var choice = ConfirmUnsaved == null ? UnsavedChoice.Continue : await ConfirmUnsaved();
        if (choice == UnsavedChoice.Continue) return false;
        if (choice == UnsavedChoice.SaveAndLeave) return await SaveAllAsync();
        _purposeBase = PurposeDraft;
        _schemaBase = SchemaDraft;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        return true;
    }

    public void MarkVisited() => _main.RememberPage("kbsettings");
}
