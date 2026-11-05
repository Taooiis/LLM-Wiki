using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;

namespace LLM_Wiki.ViewModels.Pages;

public partial class ConnectionsViewModel
{
    public Func<Task<bool>>? ConfirmAgentWrite { get; set; }

    /// <summary>下拉项来自注册表（AgentTools.Tools），UI 不写死任何具体工具。</summary>
    public ObservableCollection<string> AgentToolTypes { get; } = [.. AgentTools.Tools.Select(t => t.DisplayName)];
    [ObservableProperty] private string _selectedAgentTool = AgentTools.Name(AgentTools.OpenCode);
    /// <summary>模型下拉首项：表示不传 --model，跟随 OpenCode 自己的默认配置。</summary>
    public const string ModelFollowDefault = "跟随 CLI 默认";
    public ObservableCollection<string> AgentModels { get; } = [ModelFollowDefault];
    [ObservableProperty] private string _agentModelSelection = ModelFollowDefault;
    private readonly Dictionary<string, AgentProfile> _agentDrafts = new();
    public string AgentAdapterId => AgentTools.IdOf(SelectedAgentTool);
    public string AgentExecutableName => AgentTools.Adapter(AgentAdapterId).ExecutableName;
    public string AgentConfigTitle => SelectedAgentTool + " 配置";
    public string AgentPathHint => "选择本机 " + AgentExecutableName;
    public string AgentIntegrationHint => "CLI 自动接收完整任务，账号与模型由外部工具管理；先检测，再验证实际执行。";
    private AgentProfile CaptureAgentProfile(string id)
    {
        var profile = _agent.LoadProfile(id);
        profile.AdapterId = id; profile.ExecutablePath = AgentPath; profile.VerifiedVersion = AgentVersion;
        profile.Detected = AgentDetected; profile.LoggedIn = AgentLoggedIn; profile.ExecutionVerified = AgentExecutionVerified;
        profile.AllowWorkspaceWrite = AgentAllowWrite; profile.LastCheckedAt = AgentLastVerifiedAt;
        profile.ModelId = AgentModelSelection == ModelFollowDefault ? "" : AgentModelSelection;
        return profile;
    }
    public ObservableCollection<AgentCheckRow> AgentChecks { get; } = [];

    [ObservableProperty] private string _agentPath = "";
    [ObservableProperty] private string _agentVersion = "";
    [ObservableProperty] private bool _agentDetected;
    [ObservableProperty] private bool _agentLoggedIn;
    [ObservableProperty] private bool _agentExecutionVerified;
    [ObservableProperty] private bool _agentAllowWrite;
    [ObservableProperty] private bool _agentBusy;
    [ObservableProperty] private string _agentStatusText = "未配置";
    [ObservableProperty] private string _agentError = "";
    [ObservableProperty] private string _agentLastVerifiedAt = "—";
    [ObservableProperty] private string _agentPendingPath = "";

    /// <summary>自动查找候选路径是否待确认（04§4：自动查找结果先展示，再选择应用）。</summary>
    public bool HasAgentCandidate => !string.IsNullOrWhiteSpace(AgentPendingPath);

    /// <summary>验证标志对应的程序路径；路径一旦变化，旧检测/验证即过期（04§9）。</summary>
    private string _lastVerifiedPath = "";

    /// <summary>加载已保存档案时暂时不触发“路径变化即失效”逻辑。</summary>
    private bool _loadingAgentProfile;

    partial void OnAgentAllowWriteChanged(bool value)
    {
        OnPropertyChanged(nameof(AgentPermissionText));
    }

    partial void OnAgentBusyChanged(bool value) => NotifyAgentCommands();
    partial void OnAgentDetectedChanged(bool value) => NotifyAgentCommands();
    partial void OnAgentLoggedInChanged(bool value) => NotifyAgentCommands();

    /// <summary>04§9 L143：程序路径变化即旧验证过期——输入框每次改动（含每个字符）都立即失效，不等保存。</summary>
    partial void OnAgentPathChanged(string value)
    {
        if (_loadingAgentProfile) return;
        InvalidateAgentVerification();
        NotifyAgentCommands();
    }

    partial void OnAgentPendingPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasAgentCandidate));
        ApplyAgentCandidateCommand.NotifyCanExecuteChanged();
        DismissAgentCandidateCommand.NotifyCanExecuteChanged();
    }

    public bool AgentReady => AgentDetected && AgentLoggedIn && AgentExecutionVerified
                              && !string.IsNullOrWhiteSpace(AgentPath) && System.IO.File.Exists(AgentPath);
    public string AgentPermissionText => AgentAllowWrite ? "允许当前库目录写操作" : "只读";
    public string AgentReadyText => AgentReady ? "本机工具已就绪：" + SelectedAgentTool : "本机工具未就绪";

    private void OnAgentChanged() => Ui.Run(() => RefreshAgentUi());

    public void LoadAgentProfile()
    {
        _agent.ReloadProfile();
        RefreshAgentUi();
    }

    private void RefreshAgentUi(AgentProfile? selected = null)
    {
        var p = selected ?? _agent.Profile;
        _loadingAgentProfile = true;
        try
        {
            SelectedAgentTool = AgentTools.Name(p.AdapterId);
            AgentPath = p.ExecutablePath;
            AgentVersion = p.VerifiedVersion;
            AgentDetected = p.Detected;
            AgentLoggedIn = p.LoggedIn;
            AgentExecutionVerified = p.ExecutionVerified;
            AgentAllowWrite = p.AllowWorkspaceWrite;
            // 档案模型回填下拉；列表尚未拉取时先追加该值，避免显示为空
            AgentModelSelection = string.IsNullOrWhiteSpace(p.ModelId) ? ModelFollowDefault
                : AgentModels.Contains(p.ModelId) ? p.ModelId : AppendModel(p.ModelId);
            AgentStatusText = SelectedAgentTool + (p.Ready ? " 已就绪" : " 未就绪");
            // 档案里的验证结果对应档案中的路径；档案未记录验证时间时诚实显示“—”。
            _lastVerifiedPath = p.ExecutionVerified ? p.ExecutablePath : "";
            AgentLastVerifiedAt = p.ExecutionVerified && !string.IsNullOrWhiteSpace(p.LastCheckedAt)
                ? p.LastCheckedAt
                : "—";
            if (AgentChecks.Count == 0)
            {
                AgentChecks.Add(new AgentCheckRow("安装", p.Detected ? "已检测" : "未验证", p.Detected ? "已有检测记录" : "请选择 " + AgentExecutableName + " 后检测"));
                AgentChecks.Add(new AgentCheckRow("登录", p.LoggedIn ? "已检测" : "未验证", p.LoggedIn ? "外部登录状态已记录" : "请在所选工具中完成登录/模型配置"));
                AgentChecks.Add(new AgentCheckRow("非交互执行", p.ExecutionVerified ? "验证通过" : "未验证", p.ExecutionVerified ? "实际执行探测通过" : "请在连接页验证实际执行"));
            }
            OnPropertyChanged(nameof(SelectedAgentTool));
            OnPropertyChanged(nameof(AgentConfigTitle)); OnPropertyChanged(nameof(AgentPathHint));
            OnPropertyChanged(nameof(AgentIntegrationHint));
            OnPropertyChanged(nameof(AgentReady));
            OnPropertyChanged(nameof(AgentReadyText));
            OnPropertyChanged(nameof(AgentPermissionText));
        }
        finally
        {
            _loadingAgentProfile = false;
            NotifyAgentCommands();
        }
    }

    /// <summary>把档案中的模型 ID 追加进下拉（若不存在）并返回它，保证回填显示。</summary>
    private string AppendModel(string modelId)
    {
        if (!AgentModels.Contains(modelId)) AgentModels.Add(modelId);
        return modelId;
    }

    /// <summary>路径与上次验证路径不同：清空检测/登录/执行验证标志。</summary>
    private void InvalidateAgentVerification()
    {
        if (string.Equals(AgentPath, _lastVerifiedPath, StringComparison.OrdinalIgnoreCase)) return;
        AgentDetected = false;
        AgentLoggedIn = false;
        AgentExecutionVerified = false;
        AgentChecks.Clear();
        AgentStatusText = "程序路径已变化，旧验证已过期，请重新检测与验证";
        OnPropertyChanged(nameof(AgentReady));
        OnPropertyChanged(nameof(AgentReadyText));
    }

    // ---------- CanExecute ----------

    private bool CanBrowseAgentPath() => !AgentBusy;
    private bool CanAutoFindAgent() => !AgentBusy;
    private bool CanDetectAgent() => !AgentBusy && !string.IsNullOrWhiteSpace(AgentPath);
    private bool CanProbeAgent() => !AgentBusy && AgentDetected;
    private bool CanSaveAgent() => !AgentBusy && !_agent.IsRunning;
    private bool CanApplyAgentCandidate() => !string.IsNullOrWhiteSpace(AgentPendingPath);

    private void NotifyAgentCommands()
    {
        OnPropertyChanged(nameof(AgentReady));
        OnPropertyChanged(nameof(AgentReadyText));
        BrowseAgentPathCommand.NotifyCanExecuteChanged();
        AutoFindAgentCommand.NotifyCanExecuteChanged();
        DetectAgentCommand.NotifyCanExecuteChanged();
        ProbeAgentCommand.NotifyCanExecuteChanged();
        SaveAgentCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanBrowseAgentPath))]
    private void BrowseAgentPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 " + SelectedAgentTool + " 原生程序",
            Filter = $"{SelectedAgentTool} ({AgentExecutableName})|{AgentExecutableName}|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return;
        SetAgentPath(dialog.FileName);
    }

    /// <summary>04§4 L40：自动查找结果先展示，再由用户点“应用”才写入路径；不直接覆盖当前配置。</summary>
    [RelayCommand(CanExecute = nameof(CanAutoFindAgent))]
    private void AutoFindAgent()
    {
        var found = AgentTools.FindExecutable(AgentAdapterId);
        if (string.IsNullOrWhiteSpace(found))
        {
            AgentError = $"未在 PATH 或常见安装目录找到 {AgentExecutableName}；请手动选择程序。";
            _toast.ShowWarning(AgentError);
            return;
        }
        AgentPendingPath = found;
        _toast.Show("已找到 " + SelectedAgentTool + " 候选", "请核对候选路径，点击“应用此路径”生效。");
    }

    [RelayCommand(CanExecute = nameof(CanApplyAgentCandidate))]
    private void ApplyAgentCandidate()
    {
        var candidate = AgentPendingPath;
        AgentPendingPath = "";
        SetAgentPath(candidate);
        _toast.ShowSuccess("已应用候选路径：" + candidate);
    }

    [RelayCommand(CanExecute = nameof(CanApplyAgentCandidate))]
    private void DismissAgentCandidate()
    {
        AgentPendingPath = "";
    }

    private void SetAgentPath(string path)
    {
        _loadingAgentProfile = false; // 显式设置路径必须触发失效
        AgentPath = path;
        InvalidateAgentVerification();
        AgentError = "";
    }

    [RelayCommand(CanExecute = nameof(CanDetectAgent))]
    private async Task DetectAgentAsync()
    {
        if (AgentBusy) return;
        if (string.IsNullOrWhiteSpace(AgentPath))
        {
            AgentError = "请先选择 " + AgentExecutableName;
            return;
        }
        AgentBusy = true;
        AgentError = "";
        AgentChecks.Clear();
        try
        {
            var result = await _agent.DetectAsync(AgentPath, CancellationToken.None, AgentAdapterId);
            foreach (var check in result.Checks)
                AgentChecks.Add(new AgentCheckRow(check.Label, check.Status, check.Detail));
            AgentVersion = result.Version;
            AgentDetected = result.Checks.Any(c => c.Key == "install" && c.IsOk)
                            && result.Checks.Any(c => c.Key == "exec" && c.IsOk);
            AgentLoggedIn = result.LoggedIn;
            AgentExecutionVerified = false;
            if (AgentDetected)
            {
                // 检测结果只对本次检测的路径有效
                _lastVerifiedPath = AgentPath;
            }
            AgentStatusText = AgentDetected
                ? (AgentLoggedIn ? "已检测，等待实际执行验证" : "已检测，登录与模型尚未验证")
                : "检测未通过";
            // 安装检测通过后拉取模型列表，填充下拉（失败不阻塞检测流程）
            if (AgentDetected)
            {
                var models = await _agent.ListModelsAsync(AgentPath, CancellationToken.None, AgentAdapterId);
                AgentModels.Clear();
                AgentModels.Add(ModelFollowDefault);
                foreach (var m in models) AgentModels.Add(m);
                if (AgentModels.Count == 1)
                    AgentChecks.Add(new AgentCheckRow("模型列表", "未通过", "opencode models 未返回任何模型；请检查 OpenCode 登录状态。"));
                // 当前选择不在新列表中（例如 CLI 模型已下线）则回落为跟随默认
                if (!AgentModels.Contains(AgentModelSelection)) AgentModelSelection = ModelFollowDefault;
            }
            OnPropertyChanged(nameof(AgentReady));
        }
        finally
        {
            AgentBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanProbeAgent))]
    private async Task ProbeAgentAsync()
    {
        if (AgentBusy) return;
        if (!AgentDetected)
        {
            AgentError = "请先通过安装检测，再验证实际执行。";
            return;
        }
        AgentBusy = true;
        AgentError = "";
        try
        {
            var model = AgentModelSelection == ModelFollowDefault ? null : AgentModelSelection;
            var result = await _agent.ProbeAsync(AgentPath, CancellationToken.None, AgentAdapterId, model);
            AgentExecutionVerified = result.Success;
            AgentLoggedIn = result.Success;
            if (result.Success)
            {
                _lastVerifiedPath = AgentPath;
                AgentLastVerifiedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            }
            AgentChecks.Add(new AgentCheckRow("实际执行", result.Success ? "验证通过" : "未通过", result.Detail));
            AgentStatusText = result.Success ? "实际执行验证通过" : "实际执行验证失败";
            if (!result.Success) AgentError = result.Detail;
            OnPropertyChanged(nameof(AgentReady));
        }
        finally
        {
            AgentBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveAgent))]
    private void SaveAgent()
    {
        if (string.IsNullOrWhiteSpace(AgentPath) || !System.IO.File.Exists(AgentPath))
        {
            AgentError = "程序路径不存在，无法保存。";
            return;
        }
        // 04§9：保存前路径与上次验证路径不同 → 验证已过期，按未验证保存，不带着过期标志入库。
        var pathChangedSinceVerify = !string.Equals(AgentPath, _lastVerifiedPath, StringComparison.OrdinalIgnoreCase);
        if (pathChangedSinceVerify)
        {
            AgentDetected = false;
            AgentLoggedIn = false;
            AgentExecutionVerified = false;
            AgentStatusText = "程序路径已变化，已按未验证保存；请重新检测与验证";
            OnPropertyChanged(nameof(AgentReady));
            OnPropertyChanged(nameof(AgentReadyText));
        }
        var profile = CaptureAgentProfile(AgentAdapterId);
        profile.LastCheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _agent.SaveProfile(profile);
        AgentError = "";
        _toast.ShowSuccess(AgentReady ? "本机工具配置已保存：" + SelectedAgentTool + " 已就绪" : "配置已保存，但尚未通过全部验证");
    }

    [RelayCommand]
    private void SetAgentReadOnly() => AgentAllowWrite = false;

    public async Task RequestAgentWorkspaceWriteAsync()
    {
        if (AgentAllowWrite) return;
        var confirmed = ConfirmAgentWrite == null || await ConfirmAgentWrite();
        if (confirmed) AgentAllowWrite = true;
        else OnPropertyChanged(nameof(AgentAllowWrite));
    }

    [RelayCommand]
    private void OpenAgentHelp() => _toast.Show(
        SelectedAgentTool,
        "请在所选工具自己的界面完成登录和模型配置；应用不读取认证文件。OpenCode 可使用 opencode auth login。");
}

public sealed record AgentCheckRow(string Label, string Status, string Detail)
{
    public bool IsOk => Status is "通过" or "已验证" or "验证通过" or "已检测";
}
