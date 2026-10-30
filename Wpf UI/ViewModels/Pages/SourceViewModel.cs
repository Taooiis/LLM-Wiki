using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

using Microsoft.Win32;

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

        Workspace = new DocumentWorkspaceViewModel(sources, knowledge, toast)
        {
            NewFileDirectory = "raw/sources",
        };
        Rows = [];
        FilteredRows = CollectionViewSource.GetDefaultView(Rows);
        FilteredRows.Filter = o => o is SourceRow row
            && (string.IsNullOrWhiteSpace(SearchKeyword) || row.Name.Contains(SearchKeyword.Trim(), StringComparison.OrdinalIgnoreCase))
            && row.MatchesType(TypeFilter);

        _unsaved.Register("source-workspace", Workspace);
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.OpenRequested += OnOpenRequested;
        LoadKb(_main.CurrentKb);
    }

    public DocumentWorkspaceViewModel Workspace { get; }

    /// <summary>同名冲突时由页面询问：覆盖同名 / 跳过同名 / 返回。</summary>
    public Func<IReadOnlyList<string>, Task<SourceConflictPolicy>>? ResolveConflict { get; set; }

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

    public ObservableCollection<SourceRow> Rows { get; }
    public ICollectionView FilteredRows { get; }
    public ObservableCollection<TrashEntry> TrashEntries { get; } = [];

    public string[] TypeFilters { get; } = ["全部", "Markdown", "文本", "Word", "Excel"];

    public bool HasKb => _main.HasKb;
    public bool HasSelection => SelectedRows.Count > 0;
    public bool IsBinarySelected => CurrentInfo is { IsText: false };
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

    public string PendingText => PendingCount > 0 ? $"有 {PendingCount} 项资料变更尚未整理" : "暂无待整理变更";

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
            try { _scanner.Scan(kb, recordChanges: true); } catch { }
            Reload();
        }
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasKb));
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
        OnPropertyChanged(nameof(PendingText));
    }

    partial void OnSearchKeywordChanged(string value) => FilteredRows.Refresh();
    partial void OnTypeFilterChanged(string value) => FilteredRows.Refresh();
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

    [RelayCommand]
    private void NewSource()
    {
        if (!_main.HasKb)
        {
            _toast.ShowWarning("请先创建或选择知识库");
            return;
        }
        Workspace.EnterNewSource(SelectedKb);
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
            Filter = "支持的资料 (*.md;*.txt;*.docx;*.xlsx)|*.md;*.txt;*.docx;*.xlsx|Markdown (*.md)|*.md|文本 (*.txt)|*.txt|Word (*.docx)|*.docx|Excel (*.xlsx)|*.xlsx",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        await ImportPathsAsync(dialog.FileNames);
    }

    public bool FindConflicts(IEnumerable<string> paths)
    {
        var existing = _sources.List(SelectedKb).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return paths.Any(p => existing.Contains(Path.GetFileName(p)));
    }

    public async Task ImportPathsAsync(IReadOnlyList<string> paths, SourceConflictPolicy policy = SourceConflictPolicy.Ask)
    {
        if (!_main.HasKb || paths.Count == 0) return;
        if (policy == SourceConflictPolicy.Ask && FindConflicts(paths) && ResolveConflict != null)
        {
            policy = await ResolveConflict(paths);
            if (policy == SourceConflictPolicy.Ask) return;
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

        var moved = new List<string>();
        var failed = new List<string>();
        TrashEntry? last = null;
        foreach (var row in selected)
        {
            var result = _sources.MoveToTrash(SelectedKb, row.Source.RelPath);
            if (result.Success)
            {
                moved.Add(row.Name);
                last = result.Entry;
            }
            else failed.Add(row.Name + "：" + result.Message);
        }
        Reload();
        Workspace.ResetToEmpty();
        SelectedRelPath = "";
        UndoTrashId = last?.Id;
        if (moved.Count > 0)
            _toast.ShowSuccess($"已移入回收站 {moved.Count} 项，可恢复；已有知识不会立即删除。");
        if (failed.Count > 0)
            _toast.ShowError("部分资料未移除：" + string.Join("；", failed));
    }

    [RelayCommand]
    private async Task UndoLastTrashAsync()
    {
        if (string.IsNullOrWhiteSpace(UndoTrashId)) return;
        var result = _trash.Restore(SelectedKb, UndoTrashId);
        if (result.Success)
        {
            UndoTrashId = null;
            Reload();
            _toast.ShowSuccess("已恢复：" + result.RelPath);
        }
        else _toast.ShowError("恢复失败：" + result.Message);
        await Task.CompletedTask;
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
        _scanner.Scan(SelectedKb, recordChanges: true);
        Reload();
    }

    [RelayCommand]
    private void OrganizeSelected()
    {
        var selected = SelectedRows.Select(r => r.Source.RelPath).ToList();
        if (selected.Count == 0) return;
        _main.PendingOrganizeSelection = selected;
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

    [ObservableProperty] private bool _isSelected;

    public bool MatchesType(string filter)
        => filter is null or "全部" || filter == TypeText;
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
