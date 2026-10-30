using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.Share;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages;

/// <summary>
/// 整理页：更新知识 / 提问分析 / 检查维护三类任务，共用任务快照、指令组合与结果核对。
/// 复制不是执行完成；只有用户勾选并比较版本后才移除待整理记录。
/// </summary>
public partial class OrganizeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly ChangeService _changes;
    private readonly ScanService _scanner;
    private readonly TaskService _tasks;
    private readonly TaskExportService _exports;
    private readonly ReviewService _reviews;
    private readonly SourceService _sources;
    private readonly McpService _mcp;
    private readonly ToastService _toast;

    public OrganizeViewModel(
        MainWindowViewModel main,
        ChangeService changes,
        ScanService scanner,
        TaskService tasks,
        TaskExportService exports,
        ReviewService reviews,
        SourceService sources,
        McpService mcp,
        ToastService toast)
    {
        _main = main;
        _changes = changes;
        _scanner = scanner;
        _tasks = tasks;
        _exports = exports;
        _reviews = reviews;
        _sources = sources;
        _mcp = mcp;
        _toast = toast;

        _main.PropertyChanged += OnMainPropertyChanged;
        _main.OrganizeSelectionRequested += OnOrganizeSelectionRequested;
        LoadKb(_main.CurrentKb);
    }

    public ObservableCollection<PendingChangeRow> PendingRows { get; } = [];
    public ObservableCollection<SourceChoiceRow> SourceChoices { get; } = [];
    public ObservableCollection<TaskItemRow> TaskItemRows { get; } = [];
    public ObservableCollection<WikiChangeRow> WikiChanges { get; } = [];
    public ObservableCollection<OrganizeTask> ActiveTasks { get; } = [];
    public ObservableCollection<OrganizeTask> RecentTasks { get; } = [];

    public ObservableCollection<string> ScopeOptions { get; } = ["待整理变更", "所选资料", "全部资料"];
    public ObservableCollection<string> AskTypes { get; } = ["回答问题", "对比主题"];
    public ObservableCollection<string> CheckTypes { get; } = ["检查知识质量", "处理已删除资料的引用"];
    public ObservableCollection<string> TransportOptions { get; } = ["文件直接访问", "MCP 连接"];

    [ObservableProperty] private string _selectedKb = "";
    [ObservableProperty] private int _activeTab;
    [ObservableProperty] private string _scope = "待整理变更";
    [ObservableProperty] private string _askType = "回答问题";
    [ObservableProperty] private string _checkType = "检查知识质量";
    [ObservableProperty] private string _transport = "文件直接访问";
    [ObservableProperty] private string _questionInput = "";
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private string _pendingSummary = "";
    [ObservableProperty] private int _allSourcesCount;
    [ObservableProperty] private OrganizeTask? _currentTask;
    [ObservableProperty] private string _taskStatusText = "";
    [ObservableProperty] private string _taskInstructionPreview = "";
    [ObservableProperty] private bool _showPreview;
    [ObservableProperty] private bool _showResult;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _lastConfirmMessage = "";
    [ObservableProperty] private string _confirmNote = "";
    [ObservableProperty] private bool _ruleNeedsReview;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _mcpHint = "";

    public bool HasKb => _main.HasKb;
    public bool IsUpdateTab => ActiveTab == 0;
    public bool IsAskTab => ActiveTab == 1;
    public bool IsCheckTab => ActiveTab == 2;
    public bool IsScopePending => Scope == "待整理变更";
    public bool IsScopeSelected => Scope == "所选资料";
    public bool IsScopeAll => Scope == "全部资料";
    public bool HasPending => PendingRows.Count > 0;
    public bool HasActiveTasks => ActiveTasks.Count > 0;
    public bool HasTask => CurrentTask != null;
    public bool HasTaskItems => TaskItemRows.Count > 0;
    public bool HasWikiChanges => WikiChanges.Count > 0;
    public bool TaskRuleChanged => CurrentTask?.RuleChanged == true;
    public string TransportHint => Transport == "MCP 连接"
        ? (_mcp.Running
            ? (_mcp.Policy.WriteEnabled ? "MCP 连接已开启写入：本库知识可写。" : "此连接只能读取内容，不能写回知识。")
            : "MCP 连接未开启；复制的是任务说明，需在外部客户端发起。")
        : "文件直接访问：由外部 AI 使用本机文件权限读取和写回。";

    public string CopyButtonText => CurrentTask == null ? "复制整理指令" : "再次复制";

    public bool CanCopy => ActiveTab switch
    {
        0 => Scope switch
        {
            "待整理变更" => PendingRows.Any(r => r.IsSelected),
            "所选资料" => SourceChoices.Any(r => r.IsSelected),
            "全部资料" => AllSourcesCount > 0,
            _ => false,
        },
        1 => !string.IsNullOrWhiteSpace(QuestionInput),
        2 => CheckType == "检查知识质量" || PendingRows.Any(r => r.Change.Action == "删除"),
        _ => false,
    };

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb)) LoadKb(_main.CurrentKb);
    }

    private void OnOrganizeSelectionRequested()
    {
        if (string.IsNullOrWhiteSpace(_main.CurrentKb)) return;
        if (!SelectedKb.Equals(_main.CurrentKb, StringComparison.Ordinal)) LoadKb(_main.CurrentKb);
        Scope = "所选资料";
        var wanted = _main.PendingOrganizeSelection ?? [];
        ReloadSourceChoices(wanted);
        ActiveTab = 0;
        _toast.ShowSuccess("已带入所选资料，确认范围后复制整理指令");
    }

    public void LoadKb(string kb)
    {
        SelectedKb = kb;
        CurrentTask = null;
        TaskItemRows.Clear();
        WikiChanges.Clear();
        ShowPreview = false;
        ShowResult = false;
        LastConfirmMessage = "";
        RuleNeedsReview = false;
        OnPropertyChanged(nameof(HasKb));
        if (string.IsNullOrWhiteSpace(kb) || !_main.HasKb) return;
        ReloadPending();
        ReloadSourceChoices(_main.PendingOrganizeSelection);
        ReloadTasks();
        RefreshRuleStatus();
    }

    private void ReloadPending()
    {
        var selected = PendingRows.Where(r => r.IsSelected).Select(r => r.Change.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PendingRows.Clear();
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        foreach (var change in _changes.Load(SelectedKb).OrderBy(c => c.File, StringComparer.Ordinal))
        {
            var row = new PendingChangeRow(change) { IsSelected = selected.Contains(change.File) };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PendingChangeRow.IsSelected)) NotifyCopyState();
            };
            PendingRows.Add(row);
        }
        PendingCount = PendingRows.Count;
        var add = PendingRows.Count(r => r.Change.Action == "新增");
        var mod = PendingRows.Count(r => r.Change.Action == "修改");
        var del = PendingRows.Count(r => r.Change.Action == "删除");
        PendingSummary = PendingCount == 0
            ? "暂无待整理变更。你仍可以选择资料重新整理。"
            : $"待整理 {PendingCount} 项：新增 {add}、修改 {mod}、删除 {del}";
        AllSourcesCount = string.IsNullOrWhiteSpace(SelectedKb) ? 0 : _sources.List(SelectedKb).Count;
        _main.RefreshStatusBar();
        NotifyCopyState();
    }

    private void ReloadSourceChoices(IEnumerable<string>? preferred)
    {
        var wanted = preferred?.Select(ChangeService.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        SourceChoices.Clear();
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        foreach (var source in _sources.List(SelectedKb))
        {
            var row = new SourceChoiceRow(source) { IsSelected = wanted.Contains(source.RelPath) };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SourceChoiceRow.IsSelected)) NotifyCopyState();
            };
            SourceChoices.Add(row);
        }
        NotifyCopyState();
    }

    private void ReloadTasks()
    {
        ActiveTasks.Clear();
        RecentTasks.Clear();
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        var recent = _tasks.Recent(SelectedKb);
        foreach (var task in recent.Where(t => t.State is TaskState.Prepared or TaskState.Copied or TaskState.PartiallyConfirmed))
            ActiveTasks.Add(task);
        foreach (var task in recent.Where(t => t.State is TaskState.Confirmed or TaskState.Abandoned).Take(10))
            RecentTasks.Add(task);
        OnPropertyChanged(nameof(HasActiveTasks));
    }

    private void RefreshRuleStatus()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        RuleNeedsReview = _reviews.GetRuleStatus(SelectedKb).NeedsReview;
    }

    partial void OnActiveTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsUpdateTab));
        OnPropertyChanged(nameof(IsAskTab));
        OnPropertyChanged(nameof(IsCheckTab));
        NotifyCopyState();
    }

    partial void OnScopeChanged(string value)
    {
        OnPropertyChanged(nameof(IsScopePending));
        OnPropertyChanged(nameof(IsScopeSelected));
        OnPropertyChanged(nameof(IsScopeAll));
        NotifyCopyState();
    }

    partial void OnQuestionInputChanged(string value) => NotifyCopyState();
    partial void OnCheckTypeChanged(string value) => NotifyCopyState();
    partial void OnTransportChanged(string value)
    {
        OnPropertyChanged(nameof(TransportHint));
        if (Transport == "MCP 连接" && _mcp.Running && !_mcp.Policy.WriteEnabled && ActiveTab == 0)
            McpHint = "此连接只能读取内容，不能写回知识。可切换文件直读方式，或在连接页显式开启写入。";
        else McpHint = "";
    }

    partial void OnCurrentTaskChanged(OrganizeTask? value)
    {
        OnPropertyChanged(nameof(HasTask));
        OnPropertyChanged(nameof(TaskRuleChanged));
        OnPropertyChanged(nameof(CopyButtonText));
    }

    private void NotifyCopyState()
    {
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(HasPending));
    }

    [RelayCommand]
    private void SelectAllPending()
    {
        foreach (var row in PendingRows) row.IsSelected = true;
        NotifyCopyState();
    }

    [RelayCommand]
    private void ClearPendingSelection()
    {
        foreach (var row in PendingRows) row.IsSelected = false;
        NotifyCopyState();
    }

    [RelayCommand]
    private void SelectAllSources()
    {
        foreach (var row in SourceChoices) row.IsSelected = true;
        NotifyCopyState();
    }

    [RelayCommand]
    private void ClearSourceSelection()
    {
        foreach (var row in SourceChoices) row.IsSelected = false;
        NotifyCopyState();
    }

    [RelayCommand]
    private async Task CopyTaskAsync()
    {
        if (!ValidateBeforeCreate()) return;
        IsBusy = true;
        try
        {
            var task = BuildTask();
            if (task == null) return;
            try
            {
                System.Windows.Clipboard.SetText(task.Prompt);
                _tasks.MarkCopied(task.Id);
                task.State = TaskState.Copied;
                TaskStatusText = "已复制整理指令。请到外部 AI 工具中粘贴执行。";
                _toast.ShowSuccess("已复制整理指令。请到外部 AI 工具中粘贴执行。");
            }
            catch
            {
                TaskStatusText = "已准备任务，但复制剪贴板失败；可重试复制同一快照。";
                _toast.ShowError("复制剪贴板失败，任务已保留");
            }
            SetCurrentTask(task);
            ShowPreview = false;
            ReloadTasks();
        }
        finally
        {
            IsBusy = false;
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void PreviewTask()
    {
        if (!ValidateBeforeCreate()) return;
        var task = BuildTask();
        if (task == null) return;
        SetCurrentTask(task);
        ShowPreview = true;
        TaskStatusText = "已准备任务，尚未复制。";
    }

    private bool ValidateBeforeCreate()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb) || !_main.HasKb)
        {
            _toast.ShowWarning("请先选择或创建知识库");
            return false;
        }
        if (ActiveTab == 0)
        {
            if (Scope == "待整理变更" && !PendingRows.Any(r => r.IsSelected))
            {
                _toast.ShowWarning("请先勾选要处理的待整理项");
                return false;
            }
            if (Scope == "所选资料" && !SourceChoices.Any(r => r.IsSelected))
            {
                _toast.ShowWarning("请先勾选要整理的资料");
                return false;
            }
            if (Scope == "全部资料" && AllSourcesCount == 0)
            {
                _toast.ShowWarning("当前库还没有资料");
                return false;
            }
        }
        if (ActiveTab == 1 && string.IsNullOrWhiteSpace(QuestionInput))
        {
            _toast.ShowWarning(AskType == "回答问题" ? "请填写你想了解的问题" : "请填写想比较的主题");
            return false;
        }
        if (ActiveTab == 2 && CheckType == "处理已删除资料的引用" && !PendingRows.Any(r => r.Change.Action == "删除"))
        {
            _toast.ShowWarning("当前没有已删除资料的引用需要处理");
            return false;
        }
        return true;
    }

    private OrganizeTask? BuildTask()
    {
        var transport = Transport == "MCP 连接" ? "mcp" : "file";
        switch (ActiveTab)
        {
            case 0:
            {
                var scope = Scope switch
                {
                    "所选资料" => TaskScope.Selected,
                    "全部资料" => TaskScope.All,
                    _ => TaskScope.Pending,
                };
                var selected = scope == TaskScope.Selected
                    ? SourceChoices.Where(r => r.IsSelected).Select(r => r.RelPath).ToList()
                    : PendingRows.Where(r => r.IsSelected).Select(r => r.Change.File).ToList();
                return _tasks.CreateUpdateTask(SelectedKb, scope, selected, transport);
            }
            case 1:
                return _tasks.CreateAskTask(SelectedKb, AskType == "对比主题" ? TaskProtocol.Compare : TaskProtocol.Query, QuestionInput, transport);
            case 2:
                return _tasks.CreateCheckTask(SelectedKb, CheckType == "处理已删除资料的引用" ? TaskProtocol.Clean : TaskProtocol.Lint, transport);
            default:
                return null;
        }
    }

    private void SetCurrentTask(OrganizeTask task)
    {
        CurrentTask = task;
        TaskInstructionPreview = task.Prompt;
        ShowPreview = true;
        ShowResult = false;
        TaskItemRows.Clear();
        WikiChanges.Clear();
        foreach (var item in task.Items)
        {
            var row = new TaskItemRow(item);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TaskItemRow.IsSelected)) OnPropertyChanged(nameof(ResultSummary));
            };
            TaskItemRows.Add(row);
        }
        OnPropertyChanged(nameof(HasTaskItems));
        ResultSummary = TaskItemRows.Count == 0
            ? "本次任务没有需要清除的待整理记录，结束后请核对知识变化。"
            : "勾选你已核对完成的资料；默认不勾选。";
        TaskStatusText = task.State switch
        {
            TaskState.Copied => "已复制，等待你在外部 AI 中执行",
            TaskState.Prepared => "已准备，尚未复制",
            _ => task.StateText,
        };
        OnPropertyChanged(nameof(TaskRuleChanged));
    }

    [RelayCommand]
    private void RefreshPending()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        _scanner.Scan(SelectedKb, recordChanges: true);
        ReloadPending();
        if (CurrentTask != null)
        {
            CurrentTask = _tasks.RefreshAndSave(CurrentTask);
            RebuildFromTask(CurrentTask);
        }
        ReadTasks();
        _toast.ShowSuccess("已刷新待整理清单");
    }

    [RelayCommand]
    private void ViewChanges()
    {
        if (CurrentTask == null)
        {
            _toast.ShowWarning("请先生成任务，再查看本次变化");
            return;
        }
        CurrentTask = _tasks.RefreshAndSave(CurrentTask);
        LoadWikiChanges(CurrentTask);
        ShowResult = true;
    }

    private void LoadWikiChanges(OrganizeTask task)
    {
        WikiChanges.Clear();
        foreach (var change in _tasks.ReviewWikiChanges(task.Kb, task.WikiBaseline))
            WikiChanges.Add(new WikiChangeRow(change));
        OnPropertyChanged(nameof(HasWikiChanges));
        ResultSummary = WikiChanges.Count == 0
            ? "任务准备以来没有检测到知识文件变化。"
            : $"任务准备以来检测到 {WikiChanges.Count} 项知识文件变化（可能包含其他程序的修改）。";
    }

    private void RebuildFromTask(OrganizeTask task)
    {
        TaskItemRows.Clear();
        foreach (var item in task.Items)
        {
            var row = new TaskItemRow(item);
            TaskItemRows.Add(row);
        }
        OnPropertyChanged(nameof(HasTaskItems));
        OnPropertyChanged(nameof(TaskRuleChanged));
        RuleNeedsReview = _reviews.GetRuleStatus(task.Kb).NeedsReview;
    }

    [RelayCommand]
    private void SelectAllTaskItems()
    {
        foreach (var row in TaskItemRows.Where(r => r.CanSelect)) row.IsSelected = true;
        OnPropertyChanged(nameof(ResultSummary));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (CurrentTask == null) return;
        var selected = TaskItemRows.Where(r => r.IsSelected).Select(r => r.Item.RelPath).ToList();
        if (selected.Count == 0)
        {
            _toast.ShowWarning("请先勾选已核对完成的资料");
            return;
        }
        IsBusy = true;
        try
        {
            var result = _tasks.Confirm(CurrentTask.Id, selected, ConfirmNote);
            LastConfirmMessage = result.Message;
            if (result.Success) _toast.ShowSuccess(result.Message);
            else _toast.ShowError(result.Message);
            CurrentTask = _tasks.Load(CurrentTask.Id) ?? CurrentTask;
            RebuildFromTask(CurrentTask);
            LoadWikiChanges(CurrentTask);
            ReloadPending();
            ReadTasks();
            RefreshRuleStatus();
        }
        finally
        {
            IsBusy = false;
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void LoadTask(OrganizeTask? task)
    {
        if (task == null) return;
        var loaded = _tasks.Load(task.Id);
        if (loaded == null)
        {
            _toast.ShowError("任务记录不存在或已损坏");
            return;
        }
        CurrentTask = _tasks.RefreshAndSave(loaded);
        SetCurrentTask(CurrentTask);
        LoadWikiChanges(CurrentTask);
        ShowResult = true;
        ActiveTab = CurrentTask.Kind switch
        {
            TaskKind.Ask => 1,
            TaskKind.Check => 2,
            _ => 0,
        };
        if (CurrentTask.Kind == TaskKind.Ask) QuestionInput = CurrentTask.Input;
        _toast.ShowSuccess("已恢复任务，可继续核对");
    }

    [RelayCommand]
    private void AbandonTask(OrganizeTask? task)
    {
        var target = task ?? CurrentTask;
        if (target == null) return;
        _tasks.Abandon(target.Id);
        CurrentTask = null;
        ShowResult = false;
        ShowPreview = false;
        ReadTasks();
        _toast.ShowSuccess("已放弃任务，不会清除任何待整理记录");
    }

    [RelayCommand]
    private void ExportTask(string mode)
    {
        if (CurrentTask == null)
        {
            _toast.ShowWarning("请先生成任务，再导出任务包");
            return;
        }
        var includeSources = mode == "with-sources";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = includeSources ? "导出任务包（含资料）" : "导出任务包（不含资料）",
            Filter = "任务包 (*.zip)|*.zip",
            FileName = SelectedKb + "-整理任务-" + CurrentTask.Id + ".zip",
        };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return;
        try
        {
            var saved = _exports.Export(CurrentTask, includeSources, dialog.FileName);
            _toast.ShowSuccess(includeSources
                ? "已导出任务包（含资料与规则）：" + saved
                : "已导出任务说明与清单（不含资料）：" + saved);
        }
        catch (Exception e)
        {
            _toast.ShowError("导出任务包失败：" + e.Message);
        }
    }

    [RelayCommand]
    private void HidePreview() => ShowPreview = false;

    [RelayCommand]
    private void HideResult() => ShowResult = false;

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
        _toast.ShowSuccess("已稍后处理当前规则提醒；规则变化后仍会再次提醒");
    }

    [RelayCommand]
    private void GoSources() => _main.NavigateTo("sources");

    public void MarkVisited() => _main.RememberPage("organize");

    private void ReadTasks() => ReloadTasks();
}

public partial class PendingChangeRow : ObservableObject
{
    public PendingChangeRow(Change change) => Change = change;
    public Change Change { get; }
    public string DisplayPath => Change.File;
    public string Action => Change.Action;
    public string Time => Change.Time;
    public string TypeText => Change.Action switch
    {
        "新增" => "新增",
        "修改" => "修改",
        "删除" => "删除",
        _ => Change.Action,
    };
    [ObservableProperty] private bool _isSelected;
}

public partial class SourceChoiceRow : ObservableObject
{
    public SourceChoiceRow(SourceInfo source) => Source = source;
    public SourceInfo Source { get; }
    public string Name => Source.Name;
    public string RelPath => Source.RelPath;
    public string MetaText => Source.TypeText + " · " + Source.SizeText;
    [ObservableProperty] private bool _isSelected;
}

public partial class TaskItemRow : ObservableObject
{
    public TaskItemRow(TaskItem item) => Item = item;
    public TaskItem Item { get; }
    public string DisplayPath => Item.RelPath;
    public string Action => Item.Action;
    public string Status => Item.StatusText;
    public bool CanSelect => Item.CanSelect;
    public bool CanClearChange => Item.ConfirmClearsChange;
    [ObservableProperty] private bool _isSelected;
    public string NoteText => Item.Note ?? "";
}

public sealed record WikiChangeRow(WikiChange Change)
{
    public string DisplayPath => Change.RelPath;
    public string Action => Change.DisplayAction;
}
