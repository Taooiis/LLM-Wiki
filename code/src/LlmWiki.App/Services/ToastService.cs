using System;
using System.Drawing;
using System.Windows.Forms;

namespace LlmWiki.App.Services;

/// <summary>
/// 托停通知（Toast）：分享开启、导入完成、Agent 文件变更。
/// 通过 WinForms NotifyIcon 的 ShowBalloonTip 实现原生 Windows 通知。
/// App 在启动时通过 SetNotifyIcon 传入实例。
/// </summary>
public static class ToastService
{
    private static NotifyIcon? _trayIcon;

    /// <summary>在 App 启动后传入 NotifyIcon 实例，供显示通知。</summary>
    internal static void SetNotifyIcon(NotifyIcon icon) => _trayIcon = icon;

    private static void ShowBalloon(string title, string message, int timeoutMs = 5000)
    {
        try
        {
            _trayIcon?.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLogger.Write("WARN", "通知显示失败: " + ex.Message);
        }
    }

    /// <summary>分享开启时的通知</summary>
    public static void ShowShareStarted(int port)
    {
        ShowBalloon("LLM Wiki 分享已启用",
            $"本地：http://localhost:{port}/\n手机：llmwiki.local:{port}");
    }

    /// <summary>分享关闭时的通知</summary>
    public static void ShowShareStopped()
    {
        ShowBalloon("LLM Wiki 分享已关闭", "分享服务已停止");
    }

    /// <summary>导入完成的通知</summary>
    public static void ShowImportComplete(string kbName, int fileCount)
    {
        ShowBalloon("LLM Wiki 导入完成",
            $"知识库：{kbName}\n文件：{fileCount} 个");
    }

    /// <summary>Agent 文件变更通知</summary>
    public static void ShowAgentChanged(string kbName, string filePath)
    {
        ShowBalloon("LLM Wiki 文件已变更",
            $"知识库：{kbName}\n文件：{System.IO.Path.GetFileName(filePath)}");
    }

    /// <summary>通用通知</summary>
    public static void Show(string title, string message)
    {
        ShowBalloon(title, message);
    }
}
