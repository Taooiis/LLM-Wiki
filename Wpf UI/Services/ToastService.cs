using System.Windows;

namespace LLM_Wiki.Services;

/// <summary>
/// 托停通知（Toast）：分享开启、导入完成。
/// WPF 版：用 MessageBox 代替 WinForms NotifyIcon。
/// </summary>
public static class ToastService
{
    public static void ShowShareStarted(int port)
    {
        // 静默：分享状态在 UI 页面显示，不弹窗打扰
    }

    public static void ShowShareStopped()
    {
        // 静默
    }

    public static void ShowImportComplete(string kbName, int fileCount)
    {
        MessageBox.Show($"知识库：{kbName}\n文件：{fileCount} 个", "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void ShowAgentChanged(string kbName, string filePath)
    {
        MessageBox.Show($"知识库：{kbName}\n文件：{System.IO.Path.GetFileName(filePath)}", "文件已变更", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void Show(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
