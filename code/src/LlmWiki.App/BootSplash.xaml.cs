using System.Windows;

namespace LlmWiki.App;

/// <summary>
/// 启动闪屏窗口：在 WebView2 初始化前显示，消除白屏
/// </summary>
public partial class BootSplash : Window
{
    public BootSplash()
    {
        InitializeComponent();
    }
}