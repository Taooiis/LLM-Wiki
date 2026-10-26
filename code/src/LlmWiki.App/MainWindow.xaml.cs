using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using LlmWiki.App.Bridge;
using LlmWiki.App.Services;

namespace LlmWiki.App;

/// <summary>
/// 主窗口：WPF 壳只负责承载 WebView2 控件，面板 UI 全部在 wwwroot 下。
/// </summary>
public partial class MainWindow : Window
{
    private BridgeDispatcher _dispatcher;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        string? available = CoreWebView2Environment.GetAvailableBrowserVersionString();
        if (string.IsNullOrEmpty(available))
        {
            MessageBox.Show("未检测到 WebView2 运行时：请安装 Microsoft Edge WebView2 后重试。",
                "LLM Wiki", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(wwwroot);

        var env = await CoreWebView2Environment.CreateAsync();
        await BridgeWebView.EnsureCoreWebView2Async(env);

        // 1. 注入 Bridge JS 垫片
        string js = await File.ReadAllTextAsync(Path.Combine(wwwroot, "bridge.js"));
        await BridgeWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(js);

        // 2. 注册消息接收
        BridgeWebView.CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            var msg = JsonSerializer.Deserialize<BridgeMessage>(e.WebMessageAsJson);
            if (msg == null) return;

            var result = await _dispatcher.HandleAsync(msg.Cmd, msg.Args);
            
            var resp = new { requestId = msg.RequestId, result = result };
            BridgeWebView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(resp));
        };

        // 3. 初始化后端分发器
        var cfg = new ConfigService(AppContext.BaseDirectory);
        var kb = new KbService(cfg.WikisDir);
        var fb = new FileBridge(cfg.WikisDir);
        var up = new UploadService(cfg.WikisDir);
        _dispatcher = new BridgeDispatcher(cfg, kb, fb, up);

        BridgeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        BridgeWebView.CoreWebView2.Navigate("https://app.local/index.html");
    }

    private record BridgeMessage(string Cmd, string Args, string RequestId);
}
