using System.Collections.ObjectModel;
using System.Windows.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;

namespace LLM_Wiki.ViewModels.Pages;

public partial class OrganizeViewModel
{
    /// <summary>下拉项来自注册表（AgentTools.Tools），UI 不写死任何具体工具。</summary>
    public ObservableCollection<string> AgentOptions { get; } = [.. AgentTools.Tools.Select(t => "本机工具：" + t.DisplayName), "手动复制"];

    [ObservableProperty] private string _selectedAgentOption = "手动复制";
    [ObservableProperty] private bool _isAgentRunning;
    [ObservableProperty] private string _agentOutput = "";
    [ObservableProperty] private bool _agentCanForce;
    [ObservableProperty] private string _agentRunStatus = "";

    public bool IsLocalMode => SelectedAgentOption.StartsWith("本机", StringComparison.Ordinal);
    public bool AgentReady => _agent.Profile.Ready;
    public bool ShowSecondaryCopy => IsLocalMode && AgentReady;

    /// <summary>运行输出面板持久：本任务存在 attempt 记录即显示（03§5，任务恢复后仍可见）。</summary>
    public bool AgentOutputTruncated { get; private set; }

    public string PrimaryActionText
    {
        get
        {
            if (IsAgentRunning) return "运行中…";
            if (!IsLocalMode || !AgentReady) return IsMcpTransport ? "复制任务说明" : "复制整理指令";
            return ActiveTab switch
            {
                1 => "开始检查",
                _ => "开始整理",
            };
        }
    }

    /// <summary>MCP 模式只允许“复制任务说明”，本机开始按钮禁用（03§5 L60）；运行中同样禁用。</summary>
    public bool PrimaryActionEnabled => !IsAgentRunning && (IsLocalMode && AgentReady ? (CanCopy && !IsMcpTransport) : CanCopy);

    partial void OnSelectedAgentOptionChanged(string value)
    {
        if (value.StartsWith("本机工具", StringComparison.Ordinal) && !_agent.IsRunning)
        {
            var id = AgentTools.IdOf(value["本机工具：".Length..]);
            if (_agent.Profile.AdapterId != id) _agent.SelectProfile(id);
        }
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionEnabled));
        OnPropertyChanged(nameof(ShowSecondaryCopy));
    }

    partial void OnIsAgentRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(PrimaryActionEnabled));
        OnPropertyChanged(nameof(CanConfirmSelection));
        OnPropertyChanged(nameof(HasAttemptOutput));
    }

    private void OnAgentChanged() => Ui.Run(RefreshAgentState);

    private void RefreshAgentState()
    {
        IsAgentRunning = _agent.IsRunning;
        AgentOutput = _agent.OutputText;
        AgentRunStatus = _agent.StatusText;
        AgentOutputTruncated = _agent.OutputTruncated;
        AgentCanForce = _agent.State == AgentRunState.Cancelling;
        if (IsAgentRunning) TaskStatusText = _agent.StatusText;
        OnPropertyChanged(nameof(AgentReady));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionEnabled));
        OnPropertyChanged(nameof(ShowSecondaryCopy));
        OnPropertyChanged(nameof(AgentOutputTruncated));
        if (AgentReady && SelectedAgentOption == "手动复制" && CurrentTask == null)
            SelectedAgentOption = "本机工具：" + AgentTools.Name(_agent.Profile.AdapterId);
    }

    private System.Windows.Threading.DispatcherTimer? _runTimer;
    private DateTime _runStartedAt;

    /// <summary>运行反馈计时器：OpenCode 冷启动与模型响应间有数十秒静默期，逐秒计时让"正在运行"可感知。</summary>
    private void StartRunFeedback()
    {
        _runStartedAt = DateTime.Now;
        if (_runTimer == null)
        {
            _runTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _runTimer.Tick += (_, _) =>
            {
                if (!_agent.IsRunning) { _runTimer.Stop(); return; }
                var seconds = (int)(DateTime.Now - _runStartedAt).TotalSeconds;
                TaskStatusText = $"运行中 · OpenCode 正在处理 · 已等待 {seconds} 秒（首次启动与模型响应可能较慢，输出面板有进展摘要）";
            };
        }
        _runTimer.Start();
    }

    private void StopRunFeedback() => _runTimer?.Stop();

    private void OnAgentFinished(AgentAttempt attempt)
    {
        Ui.Run(() => RunFinishAgent(attempt));
    }

    /// <summary>事件回调入口:async void 仅在此薄包装;异常不外泄到事件源。</summary>
    private async void RunFinishAgent(AgentAttempt attempt)
    {
        try { await FinishAgentAsync(attempt); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
    }

    private async Task FinishAgentAsync(AgentAttempt attempt)
    {
        RefreshAgentState();
        StopRunFeedback();
        IsAgentRunning = false;
        AgentCanForce = false;
        TaskStatusText = attempt.Detail ?? "外部程序已结束，请核对结果";
        if (CurrentTask == null || !CurrentTask.Id.Equals(attempt.TaskId, StringComparison.OrdinalIgnoreCase)) return;

        _tasks.MarkNeedsReview(CurrentTask.Id, attempt.Id, attempt.Detail);
        CurrentTask = _tasks.Load(CurrentTask.Id) ?? CurrentTask;
        // 页面顶部全宽 InfoBar：外部程序已结束，请核对结果（Agent 结束事件驱动，核对完成后消失）
        AgentEndedNotice = true;
        OnPropertyChanged(nameof(HasAttemptOutput));
        RebuildFromTask(CurrentTask);
        ShowResult = true;
        await LoadWikiChangesAsync(CurrentTask);
        ReloadPending();
        ReadTasks();
        _toast.ShowWarning(attempt.Detail ?? "外部程序已结束，请核对结果");
    }

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (IsLocalMode && AgentReady)
        {
            StartLocal();
            await Task.CompletedTask;
        }
        else
        {
            await CopyTaskAsync();
        }
    }

    private void StartLocal()
    {
        // 点开始时默认刷新待整理清单：本次任务必须看到刚放进库的文件，不消费上次扫描的缓存；已勾选项保持勾选
        var checkedFiles = PendingRows.Where(r => r.IsSelected).Select(r => r.Change.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(SelectedKb))
        {
            _scanner.Scan(SelectedKb, recordChanges: true);
            ReloadPending();
            foreach (var row in PendingRows.Where(r => checkedFiles.Contains(r.Change.File)))
                row.IsSelected = true;
        }
        if (!ValidateBeforeCreate()) return;
        if (IsMcpTransport)
        {
            // MCP 模式只允许“复制任务说明”，本机开始不可用（03§5 L60）
            _toast.ShowWarning("外部 MCP 客户端模式只允许复制任务说明；本机开始不可用。");
            return;
        }
        if (_agent.IsRunning)
        {
            _toast.ShowWarning("已有本机任务在运行；首版不支持后台排队。");
            return;
        }
        var task = BuildTask();
        if (task == null) return;

        var unreadable = task.Items
            .Where(i => i.Action != "删除" && !_agent.CanReadSource(i.RelPath))
            .Select(i => System.IO.Path.GetFileName(i.RelPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unreadable.Count > 0)
        {
            _toast.ShowWarning("本机工具尚未验证这些格式：" + string.Join("、", unreadable) + "。请改用复制指令，或只选择文本资料。");
            return;
        }
        if (ActiveTab == 0 && !_agent.Profile.AllowWorkspaceWrite)
        {
            _toast.ShowWarning("本机工具当前为只读配置，不能写回知识。请到连接页开启当前库写权限，或使用复制指令。");
            return;
        }

        SetCurrentTask(task);
        ShowResult = false;
        ShowPreview = false;
        var attempt = _agent.Start(task.Kb, _main.CurrentKbRoot, task.Id, task.Prompt,
            _agent.Profile.AllowWorkspaceWrite, out var error);
        if (attempt == null)
        {
            TaskStatusText = "启动失败：" + error;
            _toast.ShowError(TaskStatusText);
            return;
        }
        IsAgentRunning = true;
        AgentCanForce = false;
        AgentOutput = "";
        AgentOutputTruncated = false;
        OnPropertyChanged(nameof(AgentOutputTruncated));
        TaskStatusText = "运行中 · OpenCode 正在处理（首次启动与模型响应可能较慢）";
        StartRunFeedback();
        _toast.ShowSuccess("已启动 " + AgentTools.Name(_agent.Profile.AdapterId) + "，任务已自动传入");
    }

    [RelayCommand]
    private void RequestCancel()
    {
        if (!IsAgentRunning) return;
        _agent.RequestCancel();
        TaskStatusText = "已请求结束；5 秒后可强制结束本次进程。";
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null) dispatcher.BeginInvoke(() => AgentCanForce = IsAgentRunning);
        });
    }

    [RelayCommand]
    private void ForceKill()
    {
        if (!_agent.ForceKill(out var error))
        {
            _toast.ShowError(error);
            return;
        }
        AgentCanForce = false;
        _toast.ShowWarning("已请求强制结束本次进程；请核对可能产生的部分修改。");
    }

    [RelayCommand]
    private void CloseWithRemaining()
    {
        if (CurrentTask == null) return;
        if (IsAgentRunning)
        {
            _toast.ShowWarning("本机任务仍在运行，不能结束待核对任务。");
            return;
        }
        _tasks.CloseWithRemaining(CurrentTask.Id);
        CurrentTask = _tasks.Load(CurrentTask.Id) ?? CurrentTask;
        TaskStatusText = CurrentTask.StateText;
        ShowResult = false;
        AgentEndedNotice = false;
        OnPropertyChanged(nameof(HasAttemptOutput));
        ReadTasks();
        ReloadPending();
        _toast.ShowSuccess("任务已结束；未确认的资料仍保留在待整理清单。");
    }
}
