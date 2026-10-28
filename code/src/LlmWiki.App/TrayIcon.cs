using System.Drawing;
using System.Windows.Forms;

namespace LlmWiki.App;

/// <summary>
/// 托盘图标：关窗口后程序仍在后台跑（分享服务不中断），双击图标可唤回面板。
/// 用 .NET 内置的 WinForms NotifyIcon，不引第三方包。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private bool _balloonShown;

    /// <param name="show">显示主面板</param>
    /// <param name="exit">真正退出程序（停掉分享服务）</param>
    public TrayIcon(Action show, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示面板", null, (_, _) => show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "LLM Wiki 知识库",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    /// <summary>Expose underlying NotifyIcon for toast notifications.</summary>
    public NotifyIcon Icon => _icon;

    /// <summary>第一次缩到托盘时提示一次，避免用户以为程序被关掉了。</summary>
    public void NotifyHidden()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(
            3000,
            "LLM Wiki 仍在后台运行",
            "只读分享服务继续可用。双击托盘图标可重新打开面板。",
            ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
