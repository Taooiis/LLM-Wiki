using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LlmWiki.App;

/// <summary>
/// 原生输入框（替代网页 prompt()）。
/// 代码构建，不走 XAML：只有一个文本框 + 确定/取消，省掉一个 XAML 文件。
/// </summary>
public static class PromptWindow
{
    /// <summary>弹出输入框；返回 null 表示用户取消。owner 为 null 时按独立窗口弹出。</summary>
    public static string? Show(Window? owner, string message, string title, string defaultValue = "")
    {
        var input = new TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(0, 8, 0, 14),
            Padding = new Thickness(6, 4, 6, 4),
        };

        var ok = new Button { Content = "确定", Width = 76, IsDefault = true, Padding = new Thickness(0, 5, 0, 5) };
        var cancel = new Button { Content = "取消", Width = 76, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0, 5, 0, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = panel,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        if (owner != null) win.Owner = owner;

        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

        return win.ShowDialog() == true ? input.Text : null;
    }
}
