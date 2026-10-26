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
    private BridgeDispatcher _dispatcher = null!;

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

        // 2. 注册消息接收（协议见 Bridge/BridgeProtocol.cs）
        BridgeWebView.CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            var msg = BridgeProtocol.ParseRequest(e.WebMessageAsJson);
            if (msg == null) return;

            var result = await _dispatcher.HandleAsync(msg.Cmd, msg.Args);
            BridgeWebView.CoreWebView2.PostWebMessageAsString(
                BridgeProtocol.CreateResponse(msg.RequestId, result));
        };

        // 3. 初始化后端分发器（服务按当前存储路径现取，改路径立即生效）
        var cfg = new ConfigService(AppContext.BaseDirectory);
        _dispatcher = new BridgeDispatcher(cfg);

        BridgeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        BridgeWebView.CoreWebView2.Navigate("https://app.local/index.html");
    }
}
