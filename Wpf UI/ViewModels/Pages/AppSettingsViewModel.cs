using System.IO;
using System.Reflection;
using System.Windows;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.Share;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages;

/// <summary>应用设置：知识库根目录（全局事务）、外观、关于。</summary>
public partial class AppSettingsViewModel : ObservableObject
{
    private const string AgentBusyText = "本机任务正在运行，请先到整理页结束或取消任务";

    private readonly ConfigService _config;
    private readonly MainWindowViewModel _main;
    private readonly UnsavedChangesCoordinator _unsaved;
    private readonly ShareService _share;
    private readonly McpService _mcp;
    private readonly UpdateService _update;
    private readonly ToastService _toast;
    private readonly LocalAgentService _agent;
    private CancellationTokenSource? _validateCts;
    private int _validateVersion;
    private int _validatingVersion = -1;
    private string _statusText = "";
    private bool _statusCanApply;

    public AppSettingsViewModel(
        ConfigService config,
        MainWindowViewModel main,
        UnsavedChangesCoordinator unsaved,
        ShareService share,
        McpService mcp,
        UpdateService update,
        ToastService toast,
        LocalAgentService agent)
    {
        _config = config;
        _main = main;
        _unsaved = unsaved;
        _share = share;
        _mcp = mcp;
        _update = update;
        _toast = toast;
        _agent = agent;

        WikisDir = config.WikisDir;
        WikisDirInput = config.WikisDir;
        CurrentTheme = config.Theme;
        AppVersion = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0");
        _agent.Changed += OnAgentChanged;
        _ = ValidatePathAsync();
    }

    [ObservableProperty] private string _wikisDir = "";
    [ObservableProperty] private string _wikisDirInput = "";
    [ObservableProperty] private string _pathStatus = "";
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private bool _isValidating;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string _currentTheme = "Dark";
    [ObservableProperty] private string _appVersion = "";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private string _themePersistError = "";

    public Func<string, Task<bool>>? ConfirmApplyPath { get; set; }
    public bool IsDarkTheme => CurrentTheme != "Light";
    public bool IsLightTheme => CurrentTheme == "Light";
    public bool HasThemePersistError => !string.IsNullOrEmpty(ThemePersistError);

    partial void OnCurrentThemeChanged(string value)
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
    }

    partial void OnThemePersistErrorChanged(string value) => OnPropertyChanged(nameof(HasThemePersistError));

    partial void OnWikisDirInputChanged(string value) => _ = ValidatePathAsync();

    private async Task ValidatePathAsync()
    {
        _validateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _validateCts = cts;
        var version = ++_validateVersion;
        var input = (WikisDirInput ?? "").Trim();

        // 06§3：校验状态圈归属发起它的校验版本；提前 return 与旧版本返回统一复位。
        void ResetRing()
        {
            if (_validatingVersion != version) return;
            _validatingVersion = -1;
            IsValidating = false;
        }

        if (input.Length == 0)
        {
            ResetRing();
            SetStatus("请填写根目录", false);
            return;
        }
        var resolved = PathGuard.ResolveWikisDir(input, AppContext.BaseDirectory);
        if (resolved == null)
        {
            ResetRing();
            SetStatus("路径格式不正确", false);
            return;
        }

        IsValidating = true;
        _validatingVersion = version;
        SetStatus("正在检查目录", false);
        await Task.Delay(250, cts.Token).ContinueWith(_ => { });
        if (cts.IsCancellationRequested || version != _validateVersion)
        {
            // 旧版本校验返回时也复位；若最新校验已接管状态圈则保持其显示。
            ResetRing();
            return;
        }

        try
        {
            // 06§3：目录存在性/可写性探测（含临时试写）移入后台线程，UI 不卡。
            var (text, canApply) = await Task.Run(() => ProbePath(resolved), cts.Token);
            if (version != _validateVersion)
            {
                ResetRing();
                return;
            }
            SetStatus(text, canApply);
            // 06§3：相对路径展示解析后的绝对路径。
            if (!Path.IsPathRooted(input)) PathStatus += "，将解析为：" + resolved;
        }
        catch (OperationCanceledException)
        {
            ResetRing();
        }
        finally
        {
            ResetRing();
        }
    }

    private static (string Text, bool CanApply) ProbePath(string resolved)
    {
        if (Directory.Exists(resolved))
        {
            return ConfigService.CanUseDirectory(resolved, out var error)
                ? ("目录可用", true)
                : ("无权限访问：" + error, false);
        }

        var parent = Directory.GetParent(resolved);
        if (parent == null) return ("路径格式不正确", false);
        return Directory.Exists(parent.FullName) && ConfigService.CanUseDirectory(parent.FullName, out _)
            ? ("应用后创建目录", true)
            : ("上级目录不存在或不可写", false);
    }

    private void SetStatus(string text, bool canApply)
    {
        _statusText = text;
        _statusCanApply = canApply;
        var unchanged = string.Equals(WikisDirInput?.Trim(), _config.WikisDir, StringComparison.OrdinalIgnoreCase);
        // 06§8：未修改时禁用并说明“路径未变化”；06§4/D11：本机任务运行中禁止换根。
        PathStatus = unchanged && canApply ? "路径未变化" : text;
        CanApply = canApply && !unchanged && !_agent.IsRunning;
    }

    private void OnAgentChanged()
    {
        // 本机任务起止影响“应用路径”可用性（06§4/D11），需在 UI 线程刷新状态。
        var dispatcher = Application.Current?.Dispatcher;
        void Refresh()
        {
            // 任务结束后清掉“任务运行中”的就地错误，按输入重新校验。
            if (!_agent.IsRunning && string.Equals(_statusText, AgentBusyText, StringComparison.Ordinal))
            {
                _ = ValidatePathAsync();
                return;
            }
            SetStatus(_statusText, _statusCanApply);
        }
        if (dispatcher == null || dispatcher.CheckAccess()) Refresh();
        else dispatcher.BeginInvoke(Refresh);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择知识库根目录",
            Multiselect = false,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            WikisDirInput = dialog.FolderName;
    }

    [RelayCommand]
    private async Task ApplyPathAsync()
    {
        if (!CanApply || IsApplying) return;
        // P0（06§4/D11）：本机任务运行中禁止换根，就地报错不应用。
        if (!GuardAgentIdle()) return;
        if (!await _unsaved.CanLeaveAsync()) return;

        var wasShare = _share.Running;
        var wasMcp = _mcp.Running;
        var impacts = new List<string>();
        if (wasShare) impacts.Add($"浏览分享（{_share.BoundKb}）");
        if (wasMcp) impacts.Add($"HTTP MCP（{_mcp.BoundKb}）");
        var impactText = impacts.Count == 0
            ? "当前没有运行中的绑定服务。"
            : "将停止：" + string.Join("、", impacts) + "；独立 stdio MCP 需到外部客户端停用或重启，本应用不能代为停止。";
        var resolved = PathGuard.ResolveWikisDir(WikisDirInput, AppContext.BaseDirectory) ?? WikisDirInput;
        // D11：只有目标目录确实不存在时才说明“会在应用后创建”。
        var targetExists = Directory.Exists(resolved);
        var confirmText =
            $"当前路径：{_config.WikisDir}\n新路径：{resolved}\n\n只更换读取位置，不搬迁、合并或删除原数据。{impactText}"
            + (targetExists ? "" : "\n目标目录不存在时会在应用后创建。");
        if (ConfirmApplyPath != null && !await ConfirmApplyPath(confirmText)) return;

        // 确认框期间任务可能已启动，提交前再核一次。
        if (!GuardAgentIdle()) return;

        IsApplying = true;
        try
        {
            if (wasShare) _share.Stop();
            if (wasMcp && !_mcp.SetMode(_mcp.BoundKb, McpMode.Off, out var stopError))
            {
                SetStatus("应用失败：绑定 MCP 停止失败（" + stopError + "）；原位置仍生效", false);
                _toast.ShowError(PathStatus);
                return;
            }

            if (!_config.SetWikisDir(WikisDirInput, out var error))
            {
                var stopped = wasShare || wasMcp ? "；相关服务已停止，可手动重开" : "";
                SetStatus("应用失败：" + error + "；原位置仍生效" + stopped, false);
                _toast.ShowError(PathStatus);
                return;
            }

            WikisDir = _config.WikisDir;
            WikisDirInput = _config.WikisDir;
            SetStatus("目录可用 · 已应用", false);
            _main.ResetForNewRoot();
            _toast.ShowSuccess("知识库位置已更换：" + WikisDir + "；原目录文件未搬迁或删除。");
        }
        finally
        {
            IsApplying = false;
        }
    }

    private bool GuardAgentIdle()
    {
        if (!_agent.IsRunning) return true;
        SetStatus(AgentBusyText, false);
        return false;
    }

    [RelayCommand]
    private void ResetDefault() => WikisDirInput = _config.DefaultWikisDir;

    [RelayCommand]
    private void SetTheme(string theme)
    {
        var target = theme == "Light" ? "Light" : "Dark";
        if (target == CurrentTheme) return;
        AppThemeService.Apply(target);
        CurrentTheme = target;
        SaveThemePreference();
    }

    [RelayCommand]
    private void RetrySaveTheme() => SaveThemePreference();

    private void SaveThemePreference()
    {
        _config.SetTheme(CurrentTheme, out var error);
        // 06§8：保存失败用页内 Warning InfoBar + “重试保存”，不弹 Toast。
        ThemePersistError = string.IsNullOrEmpty(error) ? "" : "主题已应用，本次偏好未能保存：" + error;
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        UpdateStatus = "正在检查…";
        try
        {
            var result = await _update.CheckResultAsync();
            UpdateStatus = result.Success
                ? (string.IsNullOrEmpty(result.LatestVersion) ? result.Message : $"{result.Message}（最新 v{result.LatestVersion}）")
                : result.Message;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private void OpenDownloadPage() => _update.OpenDownloadPage();

    [RelayCommand]
    private async Task OpenHelpAsync() => await _main.OpenHelpAsync("quick-start", "settings");

    public void MarkVisited() => _main.RememberPage("settings");
}
