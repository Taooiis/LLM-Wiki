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
    private readonly LocalAgentService _agent;
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
        LocalAgentService agent,
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
        _agent = agent;
        _toast = toast;

        _main.PropertyChanged += OnMainPropertyChanged;
        _main.OrganizeSelectionRequested += OnOrganizeSelectionRequested;
        _agent.Changed += OnAgentChanged;
        _agent.Finished += OnAgentFinished;
        LoadKb(_main.CurrentKb);
        SelectedAgentOption = _agent.Profile.Ready ? "本机工具：" + AgentTools.Name(_agent.Profile.AdapterId) : "手动复制";
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
    [ObservableProperty] private bool _agentEndedNotice;
    [ObservableProperty] private bool _confirmShowStale;

    public bool HasKb => _main.HasKb;
    public bool IsReviewMode => CurrentTask != null && CurrentTask.State is TaskState.NeedsReview or TaskState.PartiallyConfirmed;
    public bool IsPreparationMode => !IsReviewMode;
    public bool ShowPendingList => IsPreparationMode && IsScopePending;
    public bool ShowSelectedList => IsPreparationMode && IsScopeSelected;
    public bool ShowAllScope => IsPreparationMode && IsScopeAll;
    public string ScopePanelTitle => IsReviewMode ? "本次资料核对" : "选择处理范围";
    public string ScopePanelHint => IsReviewMode ? "勾选表示已核对完成；只清除仍匹配的记录。" : "勾选与行预览分离";
    public bool IsResultPanelVisible => ShowResult && !IsReviewMode;
    public int TaskSourceCount => CurrentTask?.Items.Count ?? 0;
    public int TaskPendingCount => TaskItemRows.Count(r => !r.Item.Confirmed && string.IsNullOrEmpty(r.Item.InvalidReason));
    public int TaskChangedCount => TaskItemRows.Count(r => !string.IsNullOrEmpty(r.Item.InvalidReason));
    public int SelectedTaskItemCount => TaskItemRows.Count(r => r.IsSelected);
    public string ConfirmButtonText => $"确认完成这 {SelectedTaskItemCount} 项";
    public bool CanConfirmSelection => SelectedTaskItemCount > 0 && CurrentTask?.CanConfirm == true && !IsAgentRunning;
    public string TaskKindText => CurrentTask?.ProtocolText ?? "";
    public string TaskScopeText => CurrentTask?.Scope switch
    {
        TaskScope.Selected => "所选资料",
        TaskScope.All => "全部资料",
        _ => "待整理变更",
    };
    public string TaskTransportText => CurrentTask?.Transport == "mcp" ? "MCP 连接" : "文件直接访问";
    public string TaskCreatedText => CurrentTask?.CreatedAt ?? "";
    public string TaskAttemptText => CurrentTask?.LastAttemptStatus ?? "尚无本机执行记录";
    public string TaskAttemptStatus => CurrentTask?.LastAttemptStatus ?? _agent.StatusText;
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
    public bool IsMcpTransport => Transport == "MCP 连接";

    /// <summary>本任务存在本机执行记录（运行中或已有 attempt 回执）即显示运行输出面板（03§5）。</summary>
    public bool HasAttemptOutput => IsAgentRunning
        || (CurrentTask != null
            && (!string.IsNullOrEmpty(CurrentTask.AttemptId) || !string.IsNullOrEmpty(CurrentTask.LastAttemptStatus)));
    public string TransportHint => Transport == "MCP 连接"
        ? (_mcp.Running
            ? (_mcp.Policy.WriteEnabled ? "MCP 连接已开启写入：本库知识可写。" : "此连接只能读取内容，不能写回知识。")
            : "MCP 连接未开启；复制的是任务说明，需在外部客户端发起。")
        : "文件直接访问：由外部 AI 使用本机文件权限读取和写回。";

    public string CopyButtonText => CurrentTask == null
        ? (IsMcpTransport ? "复制任务说明" : "复制整理指令")
        : "再次复制";

    public string PendingSelectionText
    {
        get
        {
            var selected = PendingRows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) return "未选择待整理项";
            var add = selected.Count(r => r.Action == "新增");
            var mod = selected.Count(r => r.Action == "修改");
            var del = selected.Count(r => r.Action == "删除");
            return $"已选 {selected.Count} 项：新增 {add}、修改 {mod}、删除 {del}";
        }
    }

    public string SourceSelectionText
    {
        get
        {
            var count = SourceChoices.Count(r => r.IsSelected);
            return count == 0 ? "未选择资料" : $"已选 {count} 份资料";
        }
    }

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
        AgentEndedNotice = false;
        ConfirmShowStale = false;
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
                if (e.PropertyName == nameof(PendingChangeRow.IsSelected))
                {
                    NotifyCopyState();
                    OnPropertyChanged(nameof(PendingSelectionText));
                }
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
                if (e.PropertyName == nameof(SourceChoiceRow.IsSelected))
                {
                    NotifyCopyState();
                    OnPropertyChanged(nameof(SourceSelectionText));
                }
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
        foreach (var task in recent.Where(t => t.State is TaskState.Prepared or TaskState.Copied or TaskState.NeedsReview or TaskState.PartiallyConfirmed))
            ActiveTasks.Add(task);
        foreach (var task in recent.Where(t => t.State is TaskState.Confirmed or TaskState.ClosedWithRemaining or TaskState.Abandoned).Take(10))
            RecentTasks.Add(task);
        OnPropertyChanged(nameof(HasActiveTasks));
    }

    private void RefreshRuleStatus()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        RuleNeedsReview = _reviews.GetRuleStatus(SelectedKb).NeedsReview;
    }

    partial void OnShowResultChanged(bool value) => OnPropertyChanged(nameof(IsResultPanelVisible));

    private void NotifyReviewState()
    {
        OnPropertyChanged(nameof(IsReviewMode));
        OnPropertyChanged(nameof(IsPreparationMode));
        OnPropertyChanged(nameof(ShowPendingList));
        OnPropertyChanged(nameof(ShowSelectedList));
        OnPropertyChanged(nameof(ShowAllScope));
        OnPropertyChanged(nameof(ScopePanelTitle));
        OnPropertyChanged(nameof(ScopePanelHint));
        OnPropertyChanged(nameof(IsResultPanelVisible));
        OnPropertyChanged(nameof(TaskSourceCount));
        OnPropertyChanged(nameof(TaskPendingCount));
        OnPropertyChanged(nameof(TaskChangedCount));
        OnPropertyChanged(nameof(TaskAttemptStatus));
        OnPropertyChanged(nameof(SelectedTaskItemCount));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(CanConfirmSelection));
        OnPropertyChanged(nameof(TaskKindText));
        OnPropertyChanged(nameof(TaskScopeText));
        OnPropertyChanged(nameof(TaskTransportText));
        OnPropertyChanged(nameof(TaskCreatedText));
        OnPropertyChanged(nameof(TaskAttemptText));
    }

    partial void OnActiveTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsUpdateTab));
        OnPropertyChanged(nameof(IsAskTab));
        OnPropertyChanged(nameof(IsCheckTab));
        OnPropertyChanged(nameof(PrimaryActionText));
        NotifyCopyState();
    }

    partial void OnScopeChanged(string value)
    {
        OnPropertyChanged(nameof(IsScopePending));
        OnPropertyChanged(nameof(IsScopeSelected));
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(ShowPendingList));
        OnPropertyChanged(nameof(ShowSelectedList));
        OnPropertyChanged(nameof(ShowAllScope));
        NotifyCopyState();
    }

    partial void OnQuestionInputChanged(string value) => NotifyCopyState();
    partial void OnCheckTypeChanged(string value) => NotifyCopyState();
    partial void OnTransportChanged(string value)
    {
        OnPropertyChanged(nameof(IsMcpTransport));
        OnPropertyChanged(nameof(TransportHint));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionEnabled));
        OnPropertyChanged(nameof(CopyButtonText));
        // MCP 模式只允许“复制任务说明”，本机开始不可用（03§5 L60）。
        McpHint = Transport == "MCP 连接"
            ? (_mcp.Running
                ? (!_mcp.Policy.WriteEnabled
                    ? "MCP 连接只能读取内容，不能写回知识；外部 MCP 客户端模式只允许复制任务说明，本机开始不可用。"
                    : "外部 MCP 客户端模式只允许复制任务说明，由外部客户端发起执行；本机开始不可用。")
                : "MCP 连接未开启：复制的是任务说明，需在外部 MCP 客户端发起；只允许复制，本机开始不可用。")
            : "";
    }

    partial void OnCurrentTaskChanged(OrganizeTask? value)
    {
        OnPropertyChanged(nameof(HasTask));
        OnPropertyChanged(nameof(TaskRuleChanged));
        OnPropertyChanged(nameof(CopyButtonText));
        OnPropertyChanged(nameof(HasAttemptOutput));
        NotifyReviewState();
    }

    private void NotifyCopyState()
    {
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PrimaryActionEnabled));
        OnPropertyChanged(nameof(PendingSelectionText));
        OnPropertyChanged(nameof(SourceSelectionText));
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
                CopyPromptToClipboard(task);
                TaskStatusText = "已复制整理指令，请到外部工具执行";
                // N02：复制事实，信息 4 秒；Task 仍 Prepared/Copied，不代表已执行
                _toast.Show("已复制整理指令", "请到外部工具执行");
            }
            catch
            {
                // N04：剪贴板失败 8 秒错误 + 重试复制动作；任务已准备则保留，重试同一快照
                TaskStatusText = "已准备任务，但复制剪贴板失败；可重试复制同一快照。";
                _toast.ShowWithAction(
                    "复制失败：剪贴板暂不可用",
                    "任务已保留，可重试复制同一快照。",
                    Wpf.Ui.Controls.ControlAppearance.Danger,
                    "重试复制",
                    RetryCopyCurrentTaskCommand);
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

    /// <summary>复制当前已准备任务的同一快照，不重复创建任务（N04 重试动作）。</summary>
    [RelayCommand]
    private void RetryCopyCurrentTask()
    {
        var task = CurrentTask;
        if (task == null || string.IsNullOrEmpty(task.Prompt))
        {
            _toast.ShowWarning("没有可重试复制的任务");
            return;
        }
        try
        {
            CopyPromptToClipboard(task);
            TaskStatusText = "已复制整理指令，请到外部工具执行";
            _toast.Show("已复制整理指令", "请到外部工具执行");
        }
        catch
        {
            TaskStatusText = "已准备任务，但复制剪贴板失败；可重试复制同一快照。";
            _toast.ShowError("复制失败：剪贴板暂不可用");
            _toast.ShowWithAction(
                "复制失败：剪贴板暂不可用",
                "任务已保留，可重试复制同一快照。",
                Wpf.Ui.Controls.ControlAppearance.Danger,
                "重试复制",
                RetryCopyCurrentTaskCommand);
        }
    }

    private void CopyPromptToClipboard(OrganizeTask task)
    {
        System.Windows.Clipboard.SetText(task.Prompt);
        _tasks.MarkCopied(task.Id);
        task.CopiedAt = task.CopiedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        if (task.State == TaskState.Prepared) task.State = TaskState.Copied;
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
        AgentEndedNotice = false;
        ConfirmShowStale = false;
        TaskItemRows.Clear();
        WikiChanges.Clear();
        foreach (var item in task.Items)
        {
            var row = new TaskItemRow(item);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TaskItemRow.IsSelected)) return;
                OnPropertyChanged(nameof(ResultSummary));
                OnPropertyChanged(nameof(SelectedTaskItemCount));
                OnPropertyChanged(nameof(ConfirmButtonText));
                OnPropertyChanged(nameof(CanConfirmSelection));
            };
            TaskItemRows.Add(row);
        }
        OnPropertyChanged(nameof(HasTaskItems));
        OnPropertyChanged(nameof(SelectedTaskItemCount));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(CanConfirmSelection));
        ResultSummary = TaskItemRows.Count == 0
            ? "本次任务没有需要清除的待整理记录，结束后请核对知识变化。"
            : "勾选你已核对完成的资料；默认不勾选。";
        TaskStatusText = task.State switch
        {
            TaskState.Copied => "已复制，等待你在外部 AI 中执行",
            TaskState.NeedsReview => "外部程序已结束，请核对结果",
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
        if (CurrentTask.State is TaskState.Prepared or TaskState.Copied)
        {
            _tasks.MarkNeedsReview(CurrentTask.Id, "", "用户主动进入核对");
            CurrentTask = _tasks.Load(CurrentTask.Id) ?? CurrentTask;
        }
        CurrentTask = _tasks.RefreshAndSave(CurrentTask);
        RebuildFromTask(CurrentTask);
        LoadWikiChanges(CurrentTask);
        ShowResult = true;
        NotifyReviewState();
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
        OnPropertyChanged(nameof(SelectedTaskItemCount));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(CanConfirmSelection));
        RuleNeedsReview = _reviews.GetRuleStatus(task.Kb).NeedsReview;
    }

    [RelayCommand]
    private void SelectAllTaskItems()
    {
        foreach (var row in TaskItemRows.Where(r => r.CanSelect)) row.IsSelected = true;
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(SelectedTaskItemCount));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(CanConfirmSelection));
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
            if (result.Success)
            {
                // N05：部分结果（有新变化仍待整理）用警告 8 秒；全部成功才用成功 3 秒（03§11 L146）
                if (result.Stale > 0)
                {
                    _toast.ShowWarning($"已确认 {result.Confirmed} 项；{result.Stale} 项有新变化，仍待整理");
                }
                else
                {
                    _toast.ShowSuccess($"已确认 {result.Confirmed} 项");
                }
                AgentEndedNotice = false;
            }
            else
            {
                _toast.ShowError(result.Message);
            }
            try
            {
                RefreshAfterConfirm();
                ConfirmShowStale = false;
            }
            catch
            {
                // 确认已持久保存，仅显示刷新失败：持久 InfoBar + 重试显示，不重复提交确认（03§11 L147）
                ConfirmShowStale = true;
            }
        }
        finally
        {
            IsBusy = false;
        }
        await Task.CompletedTask;
    }

    private void RefreshAfterConfirm()
    {
        if (CurrentTask != null)
        {
            CurrentTask = _tasks.Load(CurrentTask.Id) ?? CurrentTask;
            RebuildFromTask(CurrentTask);
            LoadWikiChanges(CurrentTask);
        }
        ReloadPending();
        ReadTasks();
        RefreshRuleStatus();
    }

    /// <summary>“确认已保存，显示待刷新”的重试：只重新加载显示，不重复提交确认（03§11 L147）。</summary>
    [RelayCommand]
    private void RetryShowAfterConfirm()
    {
        try
        {
            RefreshAfterConfirm();
            ConfirmShowStale = false;
            _toast.ShowSuccess("已刷新显示；确认结果保持不变");
        }
        catch
        {
            ConfirmShowStale = true;
            _toast.ShowError("显示刷新仍失败；确认已保存，可稍后重试");
        }
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

    /// <summary>
    /// “我已在外部执行，核对结果”（03§10 L125 / §6 L74）：Prepared/Copied 任务不再等本机事件，
    /// 用户声明已在外部执行后进入核对（MarkNeedsReview → NeedsReview 态），复用既有核对流程。
    /// </summary>
    [RelayCommand]
    private void MarkExecutedExternally(OrganizeTask? task)
    {
        var target = task ?? CurrentTask;
        if (target == null) return;
        var loaded = _tasks.Load(target.Id);
        if (loaded == null)
        {
            _toast.ShowError("任务记录不存在或已损坏");
            return;
        }
        CurrentTask = _tasks.RefreshAndSave(loaded);
        if (CurrentTask.State is TaskState.Prepared or TaskState.Copied)
        {
            _tasks.MarkNeedsReview(CurrentTask.Id, "", "用户主动进入核对");
            CurrentTask = _tasks.Load(CurrentTask.Id) ?? CurrentTask;
        }
        SetCurrentTask(CurrentTask);
        ShowPreview = false;
        TaskStatusText = "已记录“我已在外部执行”；请核对结果后勾选确认。";
        RebuildFromTask(CurrentTask);
        LoadWikiChanges(CurrentTask);
        ShowResult = true;
        ActiveTab = CurrentTask.Kind switch
        {
            TaskKind.Ask => 1,
            TaskKind.Check => 2,
            _ => 0,
        };
        NotifyReviewState();
        ReadTasks();
        _toast.ShowSuccess("已进入核对；请确认外部执行结果后勾选确认");
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
        AgentEndedNotice = false;
        ConfirmShowStale = false;
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
    public string DisplayName => System.IO.Path.GetFileName(Change.File);
    public string TypeBadge => SourceFormatRegistry.CategoryOf(DisplayPath) switch
    {
        SourceCategory.Text => "T",
        SourceCategory.Document => "W",
        SourceCategory.Spreadsheet => "X",
        SourceCategory.Presentation => "P",
        SourceCategory.Pdf => "PDF",
        SourceCategory.Data => "{}",
        SourceCategory.Image => "IMG",
        _ => "?",
    };
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
    public string TypeBadge => Source.TypeBadge;
    public string MetaText => Source.TypeText + " · " + Source.SizeText;
    [ObservableProperty] private bool _isSelected;
}

public partial class TaskItemRow : ObservableObject
{
    public TaskItemRow(TaskItem item) => Item = item;
    public TaskItem Item { get; }
    public string DisplayPath => Item.RelPath;
    public string DisplayName => System.IO.Path.GetFileName(Item.RelPath);
    public string Action => Item.Action;
    public string Status => Item.StatusText;
    public string ReviewText => Item.ReviewText;
    public bool CanSelect => Item.CanSelect;
    public bool CanClearChange => Item.ConfirmClearsChange;
    public bool IsChanged => !string.IsNullOrEmpty(Item.InvalidReason);
    [ObservableProperty] private bool _isSelected;
    public string NoteText => Item.Note ?? "";
}

public sealed record WikiChangeRow(WikiChange Change)
{
    public string DisplayPath => Change.RelPath;
    public string Action => Change.DisplayAction;
}
