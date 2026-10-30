using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Win32;

using Wpf.Ui.Controls;

namespace LLM_Wiki.ViewModels.Pages;

/// <summary>
/// 资料页：原格式资料列表、添加/新建、预览与编辑、移入回收站、回收站、所选去整理。
/// 勾选与行单击分开；筛选后保留勾选并显示隐藏数量。
/// </summary>
public partial class SourceViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly SourceService _sources;
    private readonly TrashService _trash;
    private readonly ChangeService _changes;
    private readonly ScanService _scanner;
    private readonly UnsavedChangesCoordinator _unsaved;
    private readonly KnowledgeService _knowledge;
    private readonly ToastService _toast;

    public SourceViewModel(
        MainWindowViewModel main,
        SourceService sources,
        TrashService trash,
        ChangeService changes,
        ScanService scanner,
        UnsavedChangesCoordinator unsaved,
        KnowledgeService knowledge,
        SourcePreviewService preview,
        ToastService toast)
    {
        _main = main;
        _sources = sources;
        _trash = trash;
        _changes = changes;
        _scanner = scanner;
        _unsaved = unsaved;
        _knowledge = knowledge;
        _toast = toast;

        Workspace = new DocumentWorkspaceViewModel(sources, knowledge, preview, toast)
        {
            NewFileDirectory = "raw/sources",
        };
        Rows = [];
        FilteredRows = CollectionViewSource.GetDefaultView(Rows);
        FilteredRows.Filter = o => o is SourceRow row
            && (string.IsNullOrWhiteSpace(SearchKeyword) || row.Name.Contains(SearchKeyword.Trim(), StringComparison.OrdinalIgnoreCase))
            && row.MatchesType(TypeFilter);

        _unsaved.Register("source-workspace", Workspace);
        _unsaved.RegisterSaver("source-workspace", Workspace.TrySaveAsync);
        _unsaved.RegisterDiscarder("source-workspace", Workspace.DiscardDraft);
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.OpenRequested += OnOpenRequested;
        // 页内“去整理”计数联动：保存产生新变更即刷新（02§9.2）
        Workspace.DocumentSaved += RefreshPendingFromChanges;
        // 02§3/§5：进入编辑前校验 ≤1 MiB；超限回到只读预览，提供系统打开
        Workspace.PropertyChanged += OnWorkspacePropertyChanged;
        LoadKb(_main.CurrentKb);
    }

    public DocumentWorkspaceViewModel Workspace { get; }

    /// <summary>同名冲突时由页面询问：覆盖同名 / 跳过同名 / 取消本批（D03，只含真正冲突的项）。</summary>
    public Func<IReadOnlyList<ImportConflictItem>, Task<SourceConflictPolicy>>? ResolveConflict { get; set; }

    [ObservableProperty] private string _selectedKb = "";
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private string _typeFilter = "全部";
    [ObservableProperty] private string _selectedRelPath = "";
    [ObservableProperty] private string _pageHint = "";
    [ObservableProperty] private string _importSummary = "";
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _undoTrashId;
    [ObservableProperty] private ObservableCollection<ImportResultRow> _importResults = [];
    [ObservableProperty] private string _scanWarning = "";
    [ObservableProperty] private System.Collections.Generic.List<string> _undoTrashIds = [];

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanModify));

    partial void OnScanWarningChanged(string value) => OnPropertyChanged(nameof(ScanIncomplete));

    public ObservableCollection<SourceRow> Rows { get; }
    public ICollectionView FilteredRows { get; }
    public ObservableCollection<TrashEntry> TrashEntries { get; } = [];

    public string[] TypeFilters { get; } = SourceFormatRegistry.FilterOptions;

    public bool HasKb => _main.HasKb;
    public bool HasSelection => SelectedRows.Count > 0;
    public bool IsBinarySelected => CurrentInfo is { IsText: false };
    public bool HasImportFailure => ImportResults.Any(r => r.Result.Status == SourceImportStatus.Failed);
    public bool HasPendingChanges => PendingCount > 0;
    public bool ScanIncomplete => !string.IsNullOrEmpty(ScanWarning);
    public bool CanModify => !IsBusy;
    public int FilteredCount => FilteredRows.Cast<object>().Count();
    public SourceInfo? CurrentInfo => string.IsNullOrWhiteSpace(SelectedRelPath)
        ? null
        : _sources.Get(SelectedKb, SelectedRelPath);

    public string SelectedCountText
    {
        get
        {
            var selected = SelectedRows.Count;
            if (selected == 0) return "未选择资料";
            var hidden = SelectedRows.Count(r => !FilteredRows.Cast<object>().Contains(r));
            return hidden > 0 ? $"已选 {selected} 项，其中 {hidden} 项被筛选隐藏" : $"已选 {selected} 项";
        }
    }

    public string PendingText => PendingCount > 0 ? $"{PendingCount} 项资料变更尚未整理" : "暂无待整理变更";

    private IReadOnlyList<SourceRow> SelectedRows => Rows.Where(r => r.IsSelected).ToList();

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb)) LoadKb(_main.CurrentKb);
    }

    private void OnOpenRequested(FileSearchResult result)
    {
        if (result.Kind != "资料") return;
        var row = Rows.FirstOrDefault(r => r.Source.RelPath.Equals(result.RelPath, StringComparison.OrdinalIgnoreCase));
        if (row != null) _ = SelectRowAsync(row);
    }

    private void RefreshPendingFromChanges()
    {
        PendingCount = string.IsNullOrWhiteSpace(SelectedKb) ? 0 : _changes.Load(SelectedKb).Count;
    }

    /// <summary>
    /// 02§3/§5：进入编辑前校验 ≤1 MiB 与格式（SourceService 行模型 CanEditText）。
    /// 超限时立即回到只读预览，编辑内容未被改动，不丢任何输入。
    /// </summary>
    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentWorkspaceViewModel.Mode)) return;
        if (Workspace.Mode != WorkspaceMode.Editing || Workspace.IsNewDocument || Workspace.Role != DocumentRole.Source) return;
        var info = _sources.Get(SelectedKb, Workspace.CurrentRelativePath);
        if (info == null || info.CanEditText) return;
        Workspace.OpenSourceText(SelectedKb, Workspace.CurrentRelativePath);
        _toast.ShowWarning($"{info.Name} 超过 1 MiB 或格式限制，应用内保持只读；请用“打开原件”在系统程序中编辑。");
    }

    public void LoadKb(string kb)
    {
        SelectedKb = kb;
        Rows.Clear();
        SelectedRelPath = "";
        Workspace.ResetToEmpty();
        PendingCount = string.IsNullOrWhiteSpace(kb) ? 0 : _changes.Load(kb).Count;
        PageHint = _main.HasKb
            ? "原格式保存 · 不生成 Markdown 副本"
            : "创建知识库后，这里用来放要整理的资料。";
        if (!string.IsNullOrWhiteSpace(kb) && _main.HasKb)
        {
            RunScan();
            Reload();
        }
        OnPropertyChanged(nameof(HasKb));
    }

    /// <summary>扫描结果完整性（02§9.2）：失败/不完整保留页头持久提示，不吞异常，可重试。</summary>
    private void RunScan()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb) || !_main.HasKb)
        {
            ScanWarning = "";
            return;
        }
        try
        {
            var result = _scanner.Scan(SelectedKb, recordChanges: true);
            ScanWarning = result.Complete
                ? ""
                : "部分目录或文件无法读取，本次不判断删除。" + result.Warning;
        }
        catch (Exception e)
        {
            ScanWarning = "扫描失败：" + e.Message;
        }
    }

    public void Reload()
    {
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Source.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Rows.Clear();
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        foreach (var source in _sources.List(SelectedKb))
        {
            var row = new SourceRow(source) { IsSelected = selected.Contains(source.RelPath) };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SourceRow.IsSelected))
                {
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectedCountText));
                }
            };
            Rows.Add(row);
        }
        FilteredRows.Refresh();
        PendingCount = _changes.Load(SelectedKb).Count;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(FilteredCount));
    }

    [RelayCommand]
    private void DismissImportResults()
    {
        ImportResults = [];
        ImportSummary = "";
    }

    partial void OnSearchKeywordChanged(string value)
    {
        FilteredRows.Refresh();
        OnPropertyChanged(nameof(FilteredCount));
    }

    partial void OnTypeFilterChanged(string value)
    {
        FilteredRows.Refresh();
        OnPropertyChanged(nameof(FilteredCount));
    }

    partial void OnImportResultsChanged(ObservableCollection<ImportResultRow> value)
        => OnPropertyChanged(nameof(HasImportFailure));
    partial void OnSelectedRelPathChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentInfo));
        OnPropertyChanged(nameof(IsBinarySelected));
    }

    public async Task SelectRowAsync(SourceRow row)
    {
        if (!await _unsaved.CanLeaveAsync()) return;
        await SelectRowCoreAsync(row);
    }

    private async Task SelectRowCoreAsync(SourceRow row)
    {
        SelectedRelPath = row.Source.RelPath;
        if (row.Source.IsText) Workspace.OpenSourceText(SelectedKb, row.Source.RelPath);
        else Workspace.OpenReadOnlyInfo(SelectedKb, row.Source.RelPath);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 新建资料：菜单选择 Markdown/纯文本（02§5，默认 md）。
    /// 预填带后缀的默认名；用户改名后缺后缀仍按 .md 补（SourceService.CreateText）。
    /// </summary>
    [RelayCommand]
    private void NewSource(string? kind)
    {
        if (!_main.HasKb)
        {
            _toast.ShowWarning("请先创建或选择知识库");
            return;
        }
        Workspace.EnterNewSource(SelectedKb);
        Workspace.NewFileName = string.Equals(kind, "txt", StringComparison.OrdinalIgnoreCase)
            ? "新建文本.txt"
            : "新建文档.md";
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (!_main.HasKb)
        {
            _toast.ShowWarning("请先创建或选择知识库");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "添加资料",
            Filter = SourceFormatRegistry.FileDialogFilter(),
            Multiselect = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        await ImportPathsAsync(dialog.FileNames);
    }

    /// <summary>D03：只返回与现有资料真正同名的项，含现有/本次的大小与修改时间。</summary>
    public IReadOnlyList<ImportConflictItem> FindConflicts(IEnumerable<string> paths)
    {
        var existing = _sources.List(SelectedKb).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<ImportConflictItem>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!existing.TryGetValue(name, out var current)) continue;
            var incoming = new FileInfo(path);
            conflicts.Add(new ImportConflictItem(
                name,
                current.SizeText,
                SourceRules.RelativeTime(current.Modified),
                SourceRules.SizeText(incoming.Length),
                SourceRules.RelativeTime(incoming.LastWriteTime)));
        }
        return conflicts;
    }

    public async Task ImportPathsAsync(IReadOnlyList<string> paths, SourceConflictPolicy policy = SourceConflictPolicy.Ask)
    {
        if (!_main.HasKb || paths.Count == 0) return;
        if (policy == SourceConflictPolicy.Ask)
        {
            var conflicts = FindConflicts(paths);
            if (conflicts.Count > 0 && ResolveConflict != null)
            {
                policy = await ResolveConflict(conflicts);
                if (policy == SourceConflictPolicy.Ask) return;
            }
        }
        IsBusy = true;
        ImportResults.Clear();
        try
        {
            var results = new List<ImportResultRow>();
            foreach (var path in paths)
            {
                var result = _sources.ImportFile(SelectedKb, path, policy);
                results.Add(new ImportResultRow(Path.GetFileName(path), result));
            }
            ImportResults = new ObservableCollection<ImportResultRow>(results);
            var added = results.Count(r => r.Result.Status == SourceImportStatus.Added);
            var skipped = results.Count(r => r.Result.Status == SourceImportStatus.Skipped);
            var failed = results.Count(r => r.Result.Status == SourceImportStatus.Failed);
            ImportSummary = $"已添加 {added} 份" + (skipped > 0 ? $"，跳过 {skipped} 份" : "") + (failed > 0 ? $"，失败 {failed} 份" : "");
            if (failed > 0) _toast.ShowWarning(ImportSummary);
            else _toast.ShowSuccess(ImportSummary);
            Reload();
        }
        finally
        {
            IsBusy = false;
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RetryFailedImportsAsync()
    {
        var failed = ImportResults.Where(r => r.Result.Status == SourceImportStatus.Failed).Select(r => r.Name).ToList();
        if (failed.Count == 0) return;
        _toast.ShowWarning("请重新选择失败的资料文件：" + string.Join("、", failed));
        await AddFilesAsync();
    }

    [RelayCommand]
    private async Task MoveSelectedToTrashAsync()
    {
        var selected = SelectedRows.ToList();
        if (selected.Count == 0) return;
        if (!await _unsaved.CanLeaveAsync()) return;

        // 02§3：本机任务运行中且在读取范围内的源禁止修改，移入回收站前逐项拒绝
        var agent = App.Services.GetService<LocalAgentService>();
        var movedNames = new List<string>();
        var movedIds = new List<string>();
        var failed = new List<string>();
        foreach (var row in selected)
        {
            if (agent is { IsRunning: true } && agent.CanReadSource(row.Source.RelPath))
            {
                failed.Add(row.Name + "：该文件正被本机任务使用，未移入回收站");
                continue;
            }
            var result = _sources.MoveToTrash(SelectedKb, row.Source.RelPath);
            if (result.Success && result.Entry != null)
            {
                movedNames.Add(row.Name);
                movedIds.Add(result.Entry.Id);
            }
            else failed.Add(row.Name + "：" + result.Message);
        }
        Reload();
        Workspace.ResetToEmpty();
        SelectedRelPath = "";
        UndoTrashIds = movedIds;
        UndoTrashId = movedIds.Count > 0 ? movedIds[^1] : null;
        if (movedNames.Count > 0)
        {
            // N03（02§9.2）：可恢复移除不确认，8 秒通知 + 撤销；撤销逐条走恢复服务
            var message = movedNames.Count == 1
                ? $"{movedNames[0]} 可恢复"
                : $"{movedNames[0]} 等 {movedNames.Count} 项 可恢复";
            _toast.ShowWithAction(
                "已移入回收站",
                message,
                ControlAppearance.Success,
                "撤销",
                UndoLastTrashCommand,
                null,
                TimeSpan.FromSeconds(8));
        }
        if (failed.Count > 0)
            _toast.ShowError("部分资料未移除：" + string.Join("；", failed));
    }

    /// <summary>撤销本批移入（N03）：逐条恢复；同名冲突按 D05 走另存，自动取首个可用名。</summary>
    [RelayCommand]
    private async Task UndoLastTrashAsync()
    {
        List<string> ids = UndoTrashIds.Count > 0
            ? UndoTrashIds.ToList()
            : string.IsNullOrWhiteSpace(UndoTrashId) ? [] : [UndoTrashId];
        if (ids.Count == 0) return;
        var restored = new List<string>();
        var errors = new List<string>();
        foreach (var id in ids)
        {
            var result = RestoreTrashWithFallback(id);
            if (result.Success) restored.Add(result.RelPath);
            else errors.Add(result.Message);
        }
        UndoTrashIds = [];
        UndoTrashId = null;
        Reload();
        if (restored.Count > 0 && errors.Count == 0)
            _toast.ShowSuccess(restored.Count == 1 ? "已恢复：" + restored[0] : $"已恢复 {restored.Count} 项");
        else if (restored.Count > 0)
            _toast.ShowWarning($"已恢复 {restored.Count} 项，{errors.Count} 项失败：" + string.Join("；", errors));
        else
            _toast.ShowError("恢复失败：" + string.Join("；", errors));
        await Task.CompletedTask;
    }

    /// <summary>恢复并处理同名：冲突时预填“原名（恢复1）.扩展名”自动另存，不覆盖现有文件。</summary>
    private TrashRestoreResult RestoreTrashWithFallback(string id)
    {
        var result = _trash.Restore(SelectedKb, id);
        if (result.Success || !result.Message.Contains("同名", StringComparison.Ordinal)) return result;
        var entry = _trash.List(SelectedKb).FirstOrDefault(e => e.Id == id);
        if (entry == null) return result;
        var renamed = _trash.Restore(SelectedKb, id, SuggestRestoreName(entry.OriginalRel, entry.OriginalName));
        return renamed.Success ? renamed : result;
    }

    /// <summary>原位置目录下首个可用名：原名、原名（恢复1）… 直到不存在（02§9.1 D05）。</summary>
    public string SuggestRestoreName(string originalRel, string originalName)
    {
        var dir = RestoreDirectory(originalRel, originalName);
        if (dir == null) return originalName;
        if (!File.Exists(Path.Combine(dir, originalName))) return originalName;
        var stem = Path.GetFileNameWithoutExtension(originalName);
        var ext = Path.GetExtension(originalName);
        for (var i = 1; i <= 999; i++)
        {
            var candidate = $"{stem}（恢复{i}）{ext}";
            if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
        }
        return $"{stem}（恢复-{DateTime.Now:yyyyMMddHHmmss}）{ext}";
    }

    /// <summary>按原相对路径与文件名推算恢复目标全路径；供提交前 File.Exists 预检。</summary>
    public string? RestoreTargetPath(string originalRel, string name)
    {
        var dir = RestoreDirectory(originalRel, name);
        return dir == null ? null : Path.Combine(dir, name);
    }

    private string? RestoreDirectory(string originalRel, string name)
    {
        var root = _main.CurrentKbRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var rel = string.IsNullOrWhiteSpace(originalRel) ? "raw/sources/" + name : originalRel;
        return Path.GetDirectoryName(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    public void OpenTrash()
    {
        TrashEntries.Clear();
        foreach (var entry in _trash.List(SelectedKb)) TrashEntries.Add(entry);
    }

    public bool RestoreTrash(string id, string? newName, out string message)
    {
        var result = _trash.Restore(SelectedKb, id, newName);
        message = result.Success ? result.RelPath : result.Message;
        if (result.Success) Reload();
        return result.Success;
    }

    public bool PurgeTrash(string id, out string error)
    {
        var ok = _trash.Purge(SelectedKb, id, out error);
        if (ok) OpenTrash();
        return ok;
    }

    public void PurgeAllTrash()
    {
        _trash.PurgeAll(SelectedKb);
        OpenTrash();
        Reload();
    }

    public bool ExportTrash(string id, string destPath, out string error)
        => _trash.Export(SelectedKb, id, destPath, out error);

    public long TrashTotalBytes => _trash.TotalBytes(SelectedKb);

    [RelayCommand]
    private void ExportCurrent()
    {
        if (CurrentInfo == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出资料",
            FileName = CurrentInfo.Name,
            Filter = "所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        if (_sources.Export(SelectedKb, CurrentInfo.RelPath, dialog.FileName, out var error))
            _toast.ShowSuccess("已导出：" + Path.GetFileName(dialog.FileName));
        else _toast.ShowError("导出失败：" + error);
    }

    [RelayCommand]
    private void OpenKbFolder()
    {
        var root = _main.CurrentKbRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true }); }
        catch (Exception e) { _toast.ShowError("打开文件夹失败：" + e.Message); }
    }

    [RelayCommand]
    private void Refresh()
    {
        if (!_main.HasKb) return;
        RunScan();
        Reload();
    }

    [RelayCommand]
    private void OrganizeSelected()
    {
        var selected = SelectedRows.ToList();
        if (selected.Count == 0) return;
        // 02§6：带入整理前核对文件真实存在，缺失项移除并提示，不把不存在的源带进整理
        var available = new List<string>();
        var missing = 0;
        foreach (var row in selected)
        {
            if (_sources.Exists(SelectedKb, row.Source.RelPath)) available.Add(row.Source.RelPath);
            else missing++;
        }
        if (available.Count == 0)
        {
            _toast.ShowWarning("所选资料文件均已缺失，请刷新后重选。");
            return;
        }
        if (missing > 0)
            _toast.ShowWarning($"已移除 {missing} 个缺失项，按 {available.Count} 项继续整理。");
        _main.PendingOrganizeSelection = available;
        _main.RequestOrganizeSelection();
        _main.NavigateTo("organize");
    }

    [RelayCommand]
    private void GoOrganize() => _main.NavigateTo("organize");

    public void MarkVisited() => _main.RememberPage("sources");
}

public partial class SourceRow : ObservableObject
{
    public SourceRow(SourceInfo source) => Source = source;

    public SourceInfo Source { get; }
    public string Name => Source.Name;
    public string RelPath => Source.RelPath;
    public string TypeText => Source.TypeText;
    public string MetaText => $"{Source.TypeText} · {Source.SizeText} · {Source.ModifiedText}";
    public string TypeBadge => Source.TypeBadge;

    [ObservableProperty] private bool _isSelected;

    public bool MatchesType(string filter)
        => filter is null or "全部" || filter == Source.CategoryText;
}

public sealed record ImportResultRow(string Name, SourceImportItem Result)
{
    public string StatusText => Result.Status switch
    {
        SourceImportStatus.Added => "已添加",
        SourceImportStatus.Skipped => "已跳过",
        SourceImportStatus.Conflict => "同名冲突",
        _ => "失败",
    };
    public string Detail => Result.Message;
}

/// <summary>D03 同名冲突项：现有文件与本次文件的大小/修改时间展示信息。</summary>
public sealed record ImportConflictItem(
    string Name,
    string ExistingSize,
    string ExistingModified,
    string IncomingSize,
    string IncomingModified);
