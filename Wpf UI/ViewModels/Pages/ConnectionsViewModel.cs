using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.Share;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages;

/// <summary>
/// 连接与分享：浏览分享（只读、PIN）与 AI 工具（本机执行、MCP、手动说明）两个独立页签。
/// 两类服务各自绑定单库、独立启停；切换桌面当前库不改变服务范围。
/// </summary>
public partial class ConnectionsViewModel : ObservableObject
{
    private const int SharePort = 8765;

    private readonly MainWindowViewModel _main;
    private readonly ShareService _share;
    private readonly McpService _mcp;
    private readonly PromptComposer _prompts;
    private readonly ToastService _toast;

    public ConnectionsViewModel(
        MainWindowViewModel main,
        ShareService share,
        McpService mcp,
        PromptComposer prompts,
        ToastService toast)
    {
        _main = main;
        _share = share;
        _mcp = mcp;
        _prompts = prompts;
        _toast = toast;
        _main.PropertyChanged += OnMainPropertyChanged;
        _mcp.Changed += OnMcpChanged;
        _share.Changed += OnShareChanged;
        RefreshShare();
        RefreshMcp();
        BoundaryPrompt = string.IsNullOrWhiteSpace(_main.CurrentKb) ? "" : _prompts.ComposeBoundary(_main.CurrentKb, _main.CurrentKbRoot);
    }

    public Func<Task<bool>>? ConfirmEnableMcpWrite { get; set; }

    [ObservableProperty] private int _activeTab;
    [ObservableProperty] private string _selectedKb = "";
    [ObservableProperty] private bool _shareRunning;
    [ObservableProperty] private string _shareBoundKb = "";
    [ObservableProperty] private string _shareStatus = "已关闭";
    [ObservableProperty] private string _pin = "";
    [ObservableProperty] private string _shareAddress = "";
    [ObservableProperty] private string _shareError = "";
    [ObservableProperty] private ObservableCollection<string> _lanAddresses = [];
    [ObservableProperty] private string _mcpStatus = "关闭";
    [ObservableProperty] private bool _mcpRunning;
    [ObservableProperty] private bool _mcpWriteEnabled;
    [ObservableProperty] private bool _mcpAllowLan;
    [ObservableProperty] private string _mcpEndpoint = "";
    [ObservableProperty] private ObservableCollection<string> _mcpLanEndpoints = [];
    [ObservableProperty] private string _mcpReadTokenDisplay = "";
    [ObservableProperty] private string _mcpWriteTokenDisplay = "";
    [ObservableProperty] private bool _tokensVisible;
    [ObservableProperty] private string _mcpClientConfig = "";
    [ObservableProperty] private string _mcpError = "";
    [ObservableProperty] private string _boundaryPrompt = "";
    [ObservableProperty] private bool _boundaryVisible;
    [ObservableProperty] private ObservableCollection<McpCallRecord> _recentCalls = [];

    public bool HasKb => _main.HasKb;
    public bool ShareBoundElsewhere => ShareRunning && !ShareBoundKb.Equals(SelectedKb, StringComparison.OrdinalIgnoreCase);
    public bool McpBoundElsewhere => McpRunning && !_mcp.BoundKb.Equals(SelectedKb, StringComparison.OrdinalIgnoreCase);

    public string ShareLibraryText => ShareRunning
        ? $"分享知识库：{ShareBoundKb} / 资料、知识和可下载原件"
        : "让可信局域网中的其他设备查看当前知识库。分享范围绑定启动时的库，切换当前库不会改变。";
    public string McpStatusHint => McpRunning
        ? $"绑定知识库：{_mcp.BoundKb} · {(McpWriteEnabled ? "写入" : "只读")}"
        : "默认关闭、默认只读。服务开启不等于任务已发送，连接成功不等于整理完成。";
    public string TokenVisibilityText => TokensVisible ? "隐藏令牌" : "显示令牌";
    public string McpWriteButtonText => McpWriteEnabled ? "切回只读" : "开启写入";
    public string McpLanText => McpAllowLan ? "当前：允许局域网访问" : "当前：仅本机回环";
    public string McpClientConfigDisplay => MaskTokens(McpClientConfig);

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.CurrentKb)) return;
        SelectedKb = _main.CurrentKb;
        RefreshShare();
        RefreshMcp();
        BoundaryPrompt = string.IsNullOrWhiteSpace(SelectedKb) ? "" : _prompts.ComposeBoundary(SelectedKb, _main.CurrentKbRoot);
        OnPropertyChanged(nameof(HasKb));
    }

    private void OnShareChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) RefreshShare();
        else dispatcher.BeginInvoke(RefreshShare);
    }

    private void OnMcpChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) RefreshMcp();
        else dispatcher.BeginInvoke(RefreshMcp);
    }

    public void MarkVisited() => _main.RememberPage("connections");

    public void RefreshShare()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshShare);
            return;
        }
        SelectedKb = _main.CurrentKb;
        ShareRunning = _share.Running;
        ShareBoundKb = _share.BoundKb;
        Pin = _share.Pin;
        ShareError = "";
        ShareStatus = ShareRunning ? $"正在分享：{ShareBoundKb}" : "已关闭";
        LanAddresses = new ObservableCollection<string>(_share.LanAddresses());
        ShareAddress = _share.Running ? _share.AccessUrl(
            LanAddresses.FirstOrDefault() ?? _share.LocalAddress()) : "";
        OnPropertyChanged(nameof(ShareBoundElsewhere));
        OnPropertyChanged(nameof(ShareLibraryText));
    }

    public void RefreshMcp()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshMcp);
            return;
        }
        McpRunning = _mcp.Running;
        McpWriteEnabled = _mcp.Running && _mcp.Policy.WriteEnabled;
        McpAllowLan = _mcp.AllowLan;
        McpEndpoint = _mcp.Running ? _mcp.LocalEndpoint : "";
        McpLanEndpoints = new ObservableCollection<string>(_mcp.LanEndpoints());
        McpClientConfig = _mcp.ClientConfigJson();
        McpReadTokenDisplay = TokensVisible ? _mcp.Token : Mask(_mcp.Token);
        McpWriteTokenDisplay = TokensVisible ? _mcp.WriteToken : Mask(_mcp.WriteToken);
        RecentCalls = new ObservableCollection<McpCallRecord>(_mcp.RecentCalls);
        McpError = _mcp.LastError;
        McpStatus = !McpRunning ? "关闭" : McpWriteEnabled ? "运行中 · 写入" : "运行中 · 只读";
        OnPropertyChanged(nameof(McpWriteButtonText));
        OnPropertyChanged(nameof(McpClientConfigDisplay));
        OnPropertyChanged(nameof(McpLanText));
        OnPropertyChanged(nameof(McpStatusHint));
        OnPropertyChanged(nameof(McpBoundElsewhere));
        OnPropertyChanged(nameof(ShareBoundElsewhere));
    }

    private static string Mask(string token)
        => token.Length <= 8 ? "••••••••" : token[..4] + "••••••••" + token[^4..];

    private string MaskTokens(string config)
    {
        if (string.IsNullOrEmpty(config)) return "";
        if (!string.IsNullOrEmpty(_mcp.Token)) config = config.Replace(_mcp.Token, Mask(_mcp.Token));
        if (!string.IsNullOrEmpty(_mcp.WriteToken)) config = config.Replace(_mcp.WriteToken, Mask(_mcp.WriteToken));
        return config;
    }

    [RelayCommand]
    private void StartShare()
    {
        if (!_main.HasKb)
        {
            _toast.ShowWarning("请先选择或创建知识库");
            return;
        }
        if (_share.Start(_main.CurrentKb, SharePort, out var error))
        {
            RefreshShare();
            _toast.ShowSuccess("浏览分享已开启：" + _main.CurrentKb);
        }
        else
        {
            ShareError = error;
            ShareStatus = "启动失败";
            RefreshShare();
            _toast.ShowError("浏览分享启动失败：" + error);
        }
    }

    [RelayCommand]
    private void StopShare()
    {
        _share.Stop();
        RefreshShare();
        _toast.ShowSuccess("浏览分享已停止，旧访问码失效");
    }

    [RelayCommand]
    private void ReplaceShareRange()
    {
        if (!_main.HasKb) return;
        _share.Stop();
        if (_share.Start(_main.CurrentKb, SharePort, out var error))
        {
            RefreshShare();
            _toast.ShowSuccess("已停止旧范围并改为分享：" + _main.CurrentKb);
        }
        else
        {
            ShareError = error;
            RefreshShare();
            _toast.ShowError("更换分享范围失败：" + error);
        }
    }

    [RelayCommand]
    private void RegeneratePin()
    {
        if (!_share.RegeneratePin(out var error))
        {
            _toast.ShowWarning(error);
            return;
        }
        RefreshShare();
        _toast.ShowSuccess("已更换访问码，旧访问码和已认证会话立即失效");
    }

    [RelayCommand]
    private void CopyShareAddress()
    {
        if (string.IsNullOrEmpty(ShareAddress)) return;
        Copy(ShareAddress, "已复制访问地址");
    }

    [RelayCommand]
    private void CopyPin()
    {
        if (string.IsNullOrEmpty(Pin)) return;
        Copy(Pin, "已复制访问码");
    }

    [RelayCommand]
    private void OpenShareAddress()
    {
        if (string.IsNullOrEmpty(ShareAddress)) return;
        try { Process.Start(new ProcessStartInfo(ShareAddress) { UseShellExecute = true }); }
        catch (Exception e) { _toast.ShowError("打开浏览器失败：" + e.Message); }
    }

    [RelayCommand]
    private void StartMcpReadOnly()
    {
        if (!_main.HasKb)
        {
            _toast.ShowWarning("请先选择或创建知识库");
            return;
        }
        if (_mcp.SetMode(_main.CurrentKb, McpMode.ReadOnly, out var error))
        {
            RefreshMcp();
            _toast.ShowSuccess("MCP 连接已开启（只读）");
        }
        else
        {
            McpError = error;
            RefreshMcp();
            _toast.ShowError("MCP 启动失败：" + error);
        }
    }

    [RelayCommand]
    private void StopMcp()
    {
        _mcp.SetMode(_main.CurrentKb, McpMode.Off, out _);
        RefreshMcp();
        _toast.ShowSuccess("MCP 连接已关闭");
    }

    [RelayCommand]
    private void ReplaceMcpRange()
    {
        if (!_main.HasKb) return;
        if (_mcp.SetMode(_main.CurrentKb, McpMode.ReadOnly, out var error))
        {
            RefreshMcp();
            _toast.ShowSuccess("MCP 已改为绑定：" + _main.CurrentKb + "（只读）");
        }
        else _toast.ShowError("更换绑定库失败：" + error);
    }

    [RelayCommand]
    private async Task ToggleMcpWriteAsync()
    {
        if (!_mcp.Running) return;
        if (McpWriteEnabled)
        {
            _mcp.SetMode(_mcp.BoundKb, McpMode.ReadOnly, out _);
            RefreshMcp();
            _toast.ShowSuccess("已切回只读；已成功提交的写入不能撤销");
            return;
        }
        var confirmed = ConfirmEnableMcpWrite != null && await ConfirmEnableMcpWrite();
        if (!confirmed)
        {
            McpWriteEnabled = false;
            RefreshMcp();
            return;
        }
        if (_mcp.SetMode(_mcp.BoundKb, McpMode.ReadWrite, out var error))
        {
            RefreshMcp();
            _toast.ShowWarning("写入已开启：连接可修改本库 wiki/ 下的知识文件；重启应用后恢复只读");
        }
        else
        {
            McpError = error;
            RefreshMcp();
            _toast.ShowError("开启写入失败：" + error);
        }
    }

    [RelayCommand]
    private void ToggleMcpLan()
    {
        var target = !McpAllowLan;
        if (_mcp.SetAllowLan(target, out var error))
        {
            RefreshMcp();
            _toast.ShowSuccess(target ? "已允许局域网访问 MCP（需在可信网络中）" : "已恢复仅本机监听");
        }
        else
        {
            McpError = error;
            RefreshMcp();
            _toast.ShowError("切换监听失败：" + error);
        }
    }

    [RelayCommand]
    private void ToggleTokens()
    {
        TokensVisible = !TokensVisible;
        OnPropertyChanged(nameof(TokenVisibilityText));
        RefreshMcp();
    }

    [RelayCommand]
    private void CopyReadToken()
    {
        if (string.IsNullOrEmpty(_mcp.Token)) return;
        Copy(_mcp.Token, "已复制读取令牌");
    }

    [RelayCommand]
    private void CopyWriteToken()
    {
        if (string.IsNullOrEmpty(_mcp.WriteToken)) return;
        Copy(_mcp.WriteToken, "已复制写入令牌");
    }

    [RelayCommand]
    private void RegenerateReadToken()
    {
        _mcp.RegenerateToken();
        RefreshMcp();
        _toast.ShowWarning("已生成新读取令牌，旧令牌立即失效");
    }

    [RelayCommand]
    private void RegenerateWriteToken()
    {
        _mcp.RegenerateWriteToken();
        RefreshMcp();
        _toast.ShowWarning("已生成新写入令牌，旧令牌立即失效");
    }

    [RelayCommand]
    private void CopyMcpConfig()
    {
        if (string.IsNullOrEmpty(McpClientConfig)) return;
        Copy(McpClientConfig, "已复制客户端配置");
    }

    [RelayCommand]
    private void ToggleBoundary()
    {
        BoundaryVisible = !BoundaryVisible;
        if (BoundaryVisible && string.IsNullOrWhiteSpace(BoundaryPrompt) && !string.IsNullOrWhiteSpace(SelectedKb))
        {
            BoundaryPrompt = _prompts.ComposeBoundary(SelectedKb, _main.CurrentKbRoot);
        }
    }

    [RelayCommand]
    private void CopyBoundary()
    {
        if (string.IsNullOrWhiteSpace(BoundaryPrompt)) return;
        Copy(BoundaryPrompt, "已复制库说明");
    }

    [RelayCommand]
    private void OpenStdioFolder()
    {
        var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "LlmWiki.McpStdio.exe");
        var dir = System.IO.Path.GetDirectoryName(exe);
        if (dir == null || !System.IO.Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand]
    private void GoOrganize() => _main.NavigateTo("organize");

    private void Copy(string text, string message)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            _toast.ShowSuccess(message);
        }
        catch { _toast.ShowError("复制剪贴板失败"); }
    }
}
