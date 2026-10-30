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
    private readonly ConfigService _config;
    private readonly MainWindowViewModel _main;
    private readonly UnsavedChangesCoordinator _unsaved;
    private readonly ShareService _share;
    private readonly McpService _mcp;
    private readonly UpdateService _update;
    private readonly ToastService _toast;
    private CancellationTokenSource? _validateCts;
    private int _validateVersion;

    public AppSettingsViewModel(
        ConfigService config,
        MainWindowViewModel main,
        UnsavedChangesCoordinator unsaved,
        ShareService share,
        McpService mcp,
        UpdateService update,
        ToastService toast)
    {
        _config = config;
        _main = main;
        _unsaved = unsaved;
        _share = share;
        _mcp = mcp;
        _update = update;
        _toast = toast;

        WikisDir = config.WikisDir;
        WikisDirInput = config.WikisDir;
        CurrentTheme = config.Theme;
        AppVersion = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0");
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

    partial void OnWikisDirInputChanged(string value) => _ = ValidatePathAsync();

    private async Task ValidatePathAsync()
    {
        _validateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _validateCts = cts;
        var version = ++_validateVersion;
        var input = (WikisDirInput ?? "").Trim();

        if (input.Length == 0)
        {
            SetStatus("请填写知识库根目录", false);
            return;
        }
        var resolved = PathGuard.ResolveWikisDir(input, AppContext.BaseDirectory);
        if (resolved == null)
        {
            SetStatus("路径格式不正确", false);
            return;
        }

        IsValidating = true;
        SetStatus("正在检查目录", false);
        await Task.Delay(250, cts.Token).ContinueWith(_ => { });
        if (cts.IsCancellationRequested || version != _validateVersion)
        {
            if (version == _validateVersion) IsValidating = false;
            return;
        }

        try
        {
            if (Directory.Exists(resolved))
            {
                if (ConfigService.CanUseDirectory(resolved, out var error)) SetStatus("目录可用", true);
                else SetStatus("无权限访问：" + error, false);
            }
            else
            {
                var parent = Directory.GetParent(resolved);
                if (parent == null)
                {
                    SetStatus("路径格式不正确", false);
                }
                else if (Directory.Exists(parent.FullName) && ConfigService.CanUseDirectory(parent.FullName, out _))
                {
                    SetStatus("应用后将创建目录", true);
                }
                else
                {
                    SetStatus("上级目录不存在或不可写", false);
                }
            }
        }
        finally
        {
            if (version == _validateVersion) IsValidating = false;
        }
    }

    private void SetStatus(string text, bool canApply)
    {
        PathStatus = text;
        CanApply = canApply && !string.Equals(WikisDirInput?.Trim(), _config.WikisDir, StringComparison.OrdinalIgnoreCase);
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
        if (!await _unsaved.CanLeaveAsync()) return;

        IsApplying = true;
        try
        {
            var wasShare = _share.Running;
            var wasMcp = _mcp.Running;
            if (wasShare) _share.Stop();
            if (wasMcp) _mcp.SetMode(_mcp.BoundKb, McpMode.Off, out _);

            if (!_config.SetWikisDir(WikisDirInput, out var error))
            {
                _toast.ShowError("应用路径失败：" + error);
                PathStatus = "应用失败：" + error;
                return;
            }

            WikisDir = _config.WikisDir;
            WikisDirInput = _config.WikisDir;
            PathStatus = "目录可用 · 已应用";
            CanApply = false;
            _main.ResetForNewRoot();
            _toast.ShowSuccess("知识库根目录已更新：" + WikisDir + "；原目录文件未搬迁或删除。");
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private void ResetDefault() => WikisDirInput = _config.DefaultWikisDir;

    [RelayCommand]
    private void SetTheme(string theme)
    {
        var target = theme == "Light" ? "Light" : "Dark";
        if (target == CurrentTheme) return;
        _config.SetTheme(target, out var error);
        if (!string.IsNullOrEmpty(error))
        {
            _toast.ShowError("保存主题失败：" + error);
            return;
        }
        AppThemeService.Apply(target);
        CurrentTheme = target;
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

    public void MarkVisited() => _main.RememberPage("settings");
}
