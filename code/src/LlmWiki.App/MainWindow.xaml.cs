using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace LlmWiki.App;

/// <summary>
/// 主窗口：WPF 壳只负责承载 WebView2 控件，面板 UI 全部在 wwwroot 下。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitWebViewAsync();
    }

    // 初始化 WebView2：本地 wwwroot 映射为 https://app.local 虚拟主机后加载面板
    private async Task InitWebViewAsync()
    {
        // 依赖检测：缺 WebView2 运行时给明确提示（spec 04 §4）
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

        // 把磁盘目录挂成虚拟主机（面板内用相对路径取资源，无跨域问题）
        BridgeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        BridgeWebView.CoreWebView2.Navigate("https://app.local/index.html");
    }
}
