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
/// 错误状态持久（设计 04§11.2）：ShareError/McpError 取自服务 LastError，
/// 只在下一次成功启动或停止时清除，刷新不吞掉失败。
/// </summary>
public partial class ConnectionsViewModel : ObservableObject
{
    /// <summary>浏览分享默认端口；用户可改，0 = 由系统分配空闲端口（设计 04§8）。</summary>
    public const int DefaultSharePort = 8765;

    private readonly MainWindowViewModel _main;
    private readonly ShareService _share;
    private readonly McpService _mcp;
    private readonly PromptComposer _prompts;
    private readonly LocalAgentService _agent;
    private readonly ToastService _toast;

    public ConnectionsViewModel(
        MainWindowViewModel main,
        ShareService share,
        McpService mcp,
        PromptComposer prompts,
        LocalAgentService agent,
        ToastService toast)
    {
        _main = main;
        _share = share;
        _mcp = mcp;
        _prompts = prompts;
        _agent = agent;
        _toast = toast;
        _main.PropertyChanged += OnMainPropertyChanged;
        _mcp.Changed += OnMcpChanged;
        _share.Changed += OnShareChanged;
        _agent.Changed += OnAgentChanged;
        RefreshShare();
        RefreshMcp();
        BoundaryPrompt = string.IsNullOrWhiteSpace(_main.CurrentKb) ? "" : _prompts.ComposeBoundary(_main.CurrentKb, _main.CurrentKbRoot);
        LoadAgentProfile();
    }

    public Func<Task<bool>>? ConfirmEnableMcpWrite { get; set; }
    public Func<string, Task<bool>>? ConfirmRegeneratePin { get; set; }
    public Func<string, Task<bool>>? ConfirmRegenerateToken { get; set; }
    public Func<string, Task<bool>>? ConfirmReplaceShareRange { get; set; }
    public Func<Task<bool>>? ConfirmReplaceMcpRange { get; set; }
    public Func<string, Task<bool>>? ConfirmEnableLan { get; set; }

    [ObservableProperty] private int _activeTab;
    [ObservableProperty] private string _selectedKb = "";
    [ObservableProperty] private bool _shareRunning;
    [ObservableProperty] private string _shareBoundKb = "";
    [ObservableProperty] private string _shareStatus = "已关闭";
    [ObservableProperty] private string _pin = "";
    [ObservableProperty] private string _shareAddress = "";
    [ObservableProperty] private string _shareError = "";
    [ObservableProperty] private ObservableCollection<string> _lanAddresses = [];
    [ObservableProperty] private double? _sharePortInput = DefaultSharePort;
    [ObservableProperty] private string _sharePortError = "";
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
    [ObservableProperty] private bool _mcpBusy;

    public bool HasKb => _main.HasKb;
    public bool ShareBoundElsewhere => ShareRunning && !ShareBoundKb.Equals(SelectedKb, StringComparison.OrdinalIgnoreCase);
    public bool McpBoundElsewhere => McpRunning && !_mcp.BoundKb.Equals(SelectedKb, StringComparison.OrdinalIgnoreCase);

    /// <summary>端口输入是否合法（0-65535；0 = 系统分配）。为空或越界时启动按钮禁用（字段反馈）。</summary>
    public bool SharePortValid => SharePortInput is >= 0 and <= 65535;

    public string SharePortHint => ShareRunning
        ? _share.Port.ToString()
        : "0 = 由系统分配空闲端口；启动后显示实际端口。";
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

    partial void OnSelectedKbChanged(string value)
    {
        OnPropertyChanged(nameof(HasKb));
        NotifyServiceCommands();
    }

    partial void OnShareRunningChanged(bool value) => NotifyServiceCommands();
    partial void OnMcpRunningChanged(bool value) => NotifyServiceCommands();
    partial void OnMcpBusyChanged(bool value) => NotifyServiceCommands();

    partial void OnSharePortInputChanged(double? value)
    {
        OnPropertyChanged(nameof(SharePortValid));
        OnPropertyChanged(nameof(SharePortHint));
        StartShareCommand.NotifyCanExecuteChanged();
        // 字段反馈（00§9.1）：端口非法就地提示，不弹框。
        SharePortError = SharePortValid ? "" : "端口需为 0-65535 的整数（0 = 由系统分配）";
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.CurrentKb)) return;
        SelectedKb = _main.CurrentKb;
        RefreshShare();
        RefreshMcp();
        BoundaryPrompt = string.IsNullOrWhiteSpace(SelectedKb) ? "" : _prompts.ComposeBoundary(SelectedKb, _main.CurrentKbRoot);
        OnPropertyChanged(nameof(HasKb));
        NotifyServiceCommands();
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
        // 04§11.2：失败状态不持久清空——错误只在下次成功启动或停止时消失。
        ShareError = _share.LastError;
        ShareStatus = ShareRunning
            ? $"正在分享：{ShareBoundKb}"
            : string.IsNullOrEmpty(_share.LastError) ? "已关闭" : "启动失败";
        LanAddresses = new ObservableCollection<string>(_share.LanAddresses());
        ShareAddress = _share.Running ? _share.AccessUrl(
            LanAddresses.FirstOrDefault() ?? _share.LocalAddress()) : "";
        OnPropertyChanged(nameof(ShareBoundElsewhere));
        OnPropertyChanged(nameof(ShareLibraryText));
        OnPropertyChanged(nameof(SharePortHint));
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
        // 04§11.2：错误统一取自服务 LastError——服务成功启动/停止时会清空，失败时保留。
        McpError = _mcp.LastError;
        McpStatus = !McpRunning
            ? (string.IsNullOrEmpty(_mcp.LastError) ? "关闭" : "启动失败")
            : McpWriteEnabled ? "运行中 · 写入" : "运行中 · 只读";
        OnPropertyChanged(nameof(McpWriteButtonText));
        OnPropertyChanged(nameof(McpClientConfigDisplay));
        OnPropertyChanged(nameof(McpLanText));
        OnPropertyChanged(nameof(McpStatusHint));
        OnPropertyChanged(nameof(McpBoundElsewhere));
        OnPropertyChanged(nameof(ShareBoundElsewhere));
    }

    /// <summary>相关属性变化后刷新全部启停/凭据命令的可用状态（00§10：所有按钮都有 CanExecute）。</summary>
    private void NotifyServiceCommands()
    {
        StartShareCommand.NotifyCanExecuteChanged();
        StopShareCommand.NotifyCanExecuteChanged();
        ReplaceShareRangeCommand.NotifyCanExecuteChanged();
        RegeneratePinCommand.NotifyCanExecuteChanged();
        CopyShareAddressCommand.NotifyCanExecuteChanged();
        CopyPinCommand.NotifyCanExecuteChanged();
        OpenShareAddressCommand.NotifyCanExecuteChanged();
        StartMcpReadOnlyCommand.NotifyCanExecuteChanged();
        StopMcpCommand.NotifyCanExecuteChanged();
        ReplaceMcpRangeCommand.NotifyCanExecuteChanged();
        ToggleMcpWriteCommand.NotifyCanExecuteChanged();
        ToggleMcpLanCommand.NotifyCanExecuteChanged();
        RegenerateReadTokenCommand.NotifyCanExecuteChanged();
        RegenerateWriteTokenCommand.NotifyCanExecuteChanged();
        CopyReadTokenCommand.NotifyCanExecuteChanged();
        CopyWriteTokenCommand.NotifyCanExecuteChanged();
        ToggleTokensCommand.NotifyCanExecuteChanged();
        CopyMcpConfigCommand.NotifyCanExecuteChanged();
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

    // ---------- CanExecute ----------

    private bool CanStartShare() => HasKb && !ShareRunning && SharePortValid;
    private bool CanStopShare() => ShareRunning;
    private bool CanReplaceShareRange() => HasKb && ShareRunning;
    private bool CanRegeneratePin() => ShareRunning;
    private bool CanUseShareAddress() => ShareRunning;
    private bool CanStartMcpReadOnly() => HasKb && !McpRunning && !McpBusy;
    private bool CanStopMcp() => McpRunning && !McpBusy;
    private bool CanReplaceMcpRange() => McpBoundElsewhere && !McpBusy;
    private bool CanToggleMcpWrite() => McpRunning && !McpBusy;
    private bool CanToggleMcpLan() => !McpBusy;
    private bool CanRotateTokens() => McpRunning;
    private bool CanUseTokens() => McpRunning;
    private bool CanCopyMcpConfig() => !string.IsNullOrEmpty(McpClientConfig);

    // ---------- 浏览分享 ----------

    [RelayCommand(CanExecute = nameof(CanStartShare))]
    private void StartShare()
    {
        if (!HasKb)
        {
            _toast.ShowWarning("请先选择或创建知识库");
            return;
        }
        var port = (int)(SharePortInput ?? 0);
        if (_share.Start(_main.CurrentKb, port, out var error))
        {
            RefreshShare();
            _toast.ShowSuccess($"浏览分享已开启：{_main.CurrentKb}（端口 {_share.Port}）");
        }
        else
        {
            // 失败状态持久（04§11.2）：RefreshShare 保留 LastError，服务卡显示真实状态。
            RefreshShare();
            _toast.ShowError("浏览分享启动失败：" + error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopShare))]
    private void StopShare()
    {
        _share.Stop();
        RefreshShare();
        _toast.ShowSuccess("浏览分享已停止，旧访问码失效");
    }

    [RelayCommand(CanExecute = nameof(CanReplaceShareRange))]
    private async Task ReplaceShareRangeAsync()
    {
        if (!HasKb) return;
        if (ConfirmReplaceShareRange != null
            && !await ConfirmReplaceShareRange($"浏览分享\n原范围：{ShareBoundKb}\n新范围：{_main.CurrentKb}"))
            return;
        _share.Stop();
        var port = (int)(SharePortInput ?? 0);
        if (_share.Start(_main.CurrentKb, port, out var error))
        {
            RefreshShare();
            _toast.ShowSuccess("已停止旧范围并改为分享：" + _main.CurrentKb);
        }
        else
        {
            RefreshShare();
            _toast.ShowError("更换分享范围失败：" + error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRegeneratePin))]
    private async Task RegeneratePinAsync()
    {
        if (!ShareRunning) return;
        if (ConfirmRegeneratePin != null && !await ConfirmRegeneratePin(ShareBoundKb)) return;
        if (!_share.RegeneratePin(out var error))
        {
            _toast.ShowWarning(error);
            return;
        }
        RefreshShare();
        _toast.ShowSuccess("已更换访问码，旧访问码和已认证会话立即失效");
    }

    [RelayCommand(CanExecute = nameof(CanUseShareAddress))]
    private void CopyShareAddress()
    {
        if (string.IsNullOrEmpty(ShareAddress)) return;
        Copy(ShareAddress, "已复制访问地址");
    }

    [RelayCommand(CanExecute = nameof(CanUseShareAddress))]
    private void CopyPin()
    {
        if (string.IsNullOrEmpty(Pin)) return;
        Copy(Pin, "已复制访问码");
    }

    [RelayCommand(CanExecute = nameof(CanUseShareAddress))]
    private void OpenShareAddress()
    {
        if (string.IsNullOrEmpty(ShareAddress)) return;
        try { Process.Start(new ProcessStartInfo(ShareAddress) { UseShellExecute = true }); }
        catch (Exception e) { _toast.ShowError("打开浏览器失败：" + e.Message); }
    }

    // ---------- MCP ----------

    [RelayCommand(CanExecute = nameof(CanStartMcpReadOnly))]
    private async Task StartMcpReadOnlyAsync()
    {
        if (!HasKb)
        {
            _toast.ShowWarning("请先选择或创建知识库");
            return;
        }
        McpBusy = true;
        try
        {
            if (await _mcp.SetModeAsync(_main.CurrentKb, McpMode.ReadOnly))
            {
                RefreshMcp();
                _toast.ShowSuccess("MCP 连接已开启（只读）");
            }
            else
            {
                // 启动失败：开关停在关闭、错误持久显示（04§11.2），不假称恢复原服务。
                RefreshMcp();
                _toast.ShowError("MCP 启动失败：" + _mcp.LastError);
            }
        }
        finally
        {
            McpBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopMcp))]
    private async Task StopMcpAsync()
    {
        McpBusy = true;
        try
        {
            await _mcp.SetModeAsync(_main.CurrentKb, McpMode.Off);
            RefreshMcp();
            _toast.ShowSuccess("MCP 连接已关闭");
        }
        finally
        {
            McpBusy = false;
        }
    }

    /// <summary>D10：运行时更换 MCP 授权库，先确认再停止并以新库重启（04§11.1 L171）。</summary>
    [RelayCommand(CanExecute = nameof(CanReplaceMcpRange))]
    private async Task ReplaceMcpRangeAsync()
    {
        if (!HasKb) return;
        if (ConfirmReplaceMcpRange != null && !await ConfirmReplaceMcpRange()) return;
        McpBusy = true;
        try
        {
            if (await _mcp.SetModeAsync(_main.CurrentKb, McpMode.ReadOnly))
            {
                RefreshMcp();
                _toast.ShowSuccess("MCP 已改为绑定：" + _main.CurrentKb + "（只读）");
            }
            else
            {
                // 停止后新范围启动失败：显示已停止与真实错误，不假称恢复原服务。
                RefreshMcp();
                _toast.ShowError("更换绑定库失败：" + _mcp.LastError);
            }
        }
        finally
        {
            McpBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleMcpWrite))]
    private async Task ToggleMcpWriteAsync()
    {
        if (!_mcp.Running) return;
        if (McpWriteEnabled)
        {
            McpBusy = true;
            try
            {
                await _mcp.SetModeAsync(_mcp.BoundKb, McpMode.ReadOnly);
                RefreshMcp();
                _toast.ShowSuccess("已切回只读；已成功提交的写入不能撤销");
            }
            finally
            {
                McpBusy = false;
            }
            return;
        }
        var confirmed = ConfirmEnableMcpWrite != null && await ConfirmEnableMcpWrite();
        if (!confirmed)
        {
            RefreshMcp(); // 开关回到原真实档位
            return;
        }
        McpBusy = true;
        try
        {
            if (await _mcp.SetModeAsync(_mcp.BoundKb, McpMode.ReadWrite))
            {
                RefreshMcp();
                _toast.ShowWarning("写入已开启：连接可修改本库 wiki/ 下的知识文件；重启应用后恢复只读");
            }
            else
            {
                RefreshMcp();
                _toast.ShowError("开启写入失败：" + _mcp.LastError);
            }
        }
        finally
        {
            McpBusy = false;
        }
    }

    /// <summary>D16：首次开放/关闭 MCP 局域网。失败时服务回滚 AllowLan 原值，此处只展示真实状态。</summary>
    [RelayCommand(CanExecute = nameof(CanToggleMcpLan))]
    private async Task ToggleMcpLanAsync()
    {
        var target = !McpAllowLan;
        if (target && McpRunning && ConfirmEnableLan != null
            && !await ConfirmEnableLan(_mcp.BoundKb))
            return;
        McpBusy = true;
        try
        {
            if (await _mcp.SetAllowLanAsync(target))
            {
                RefreshMcp();
                _toast.ShowSuccess(target ? "已允许局域网访问 MCP（需在可信网络中）" : "已恢复仅本机监听");
            }
            else
            {
                RefreshMcp(); // AllowLan 已由服务回滚为原值，McpError 保留真实原因
                _toast.ShowError("切换监听失败：" + _mcp.LastError);
            }
        }
        finally
        {
            McpBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseTokens))]
    private void ToggleTokens()
    {
        TokensVisible = !TokensVisible;
        OnPropertyChanged(nameof(TokenVisibilityText));
        RefreshMcp();
    }

    [RelayCommand(CanExecute = nameof(CanUseTokens))]
    private void CopyReadToken()
    {
        if (string.IsNullOrEmpty(_mcp.Token)) return;
        Copy(_mcp.Token, "已复制读取令牌");
    }

    [RelayCommand(CanExecute = nameof(CanUseTokens))]
    private void CopyWriteToken()
    {
        if (string.IsNullOrEmpty(_mcp.WriteToken)) return;
        Copy(_mcp.WriteToken, "已复制写入令牌");
    }

    [RelayCommand(CanExecute = nameof(CanRotateTokens))]
    private async Task RegenerateReadTokenAsync()
    {
        if (ConfirmRegenerateToken != null
            && !await ConfirmRegenerateToken($"读取令牌 · 授权库 {_mcp.BoundKb}（只读）"))
            return;
        _mcp.RegenerateToken();
        RefreshMcp();
        _toast.ShowWarning("已重新生成读取令牌，旧读取令牌立即失效");
    }

    [RelayCommand(CanExecute = nameof(CanRotateTokens))]
    private async Task RegenerateWriteTokenAsync()
    {
        if (ConfirmRegenerateToken != null
            && !await ConfirmRegenerateToken($"写入令牌 · 授权库 {_mcp.BoundKb}（wiki 文本）"))
            return;
        _mcp.RegenerateWriteToken();
        RefreshMcp();
        _toast.ShowWarning("已重新生成写入令牌，旧写入令牌立即失效");
    }

    [RelayCommand(CanExecute = nameof(CanCopyMcpConfig))]
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

    [RelayCommand(CanExecute = nameof(CanCopyBoundary))]
    private void CopyBoundary()
    {
        if (string.IsNullOrWhiteSpace(BoundaryPrompt)) return;
        Copy(BoundaryPrompt, "已复制库说明");
    }

    private bool CanCopyBoundary() => !string.IsNullOrWhiteSpace(BoundaryPrompt);

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
