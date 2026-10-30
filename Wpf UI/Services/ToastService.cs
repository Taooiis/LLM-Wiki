using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using Wpf.Ui;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Services
{
    /// <summary>
    /// 托停通知（Toast/Snackbar）服务（00§9.6）：
    /// 成功 3 秒、信息 4 秒、警告/错误 8 秒；支持一个主要动作（如 N03 撤销、N04 重试）。
    /// </summary>
    public class ToastService
    {
        private readonly ISnackbarService _snackbarService;
        private SnackbarPresenter? _presenter;
        private string _lastNotice = "";
        private DateTime _lastNoticeAt;

        public ToastService(ISnackbarService snackbarService)
        {
            _snackbarService = snackbarService;
        }

        /// <summary>由主窗注入 SnackbarPresenter，用于给当前 Snackbar 挂动作按钮。</summary>
        public void SetPresenter(SnackbarPresenter presenter) => _presenter = presenter;

        /// <summary>
        /// 分享开启：显示端口 + 局域网地址。
        /// </summary>
        public void ShowShareStarted(int port)
        {
            Show("分享已开启", $"端口 {port}，局域网访问", ControlAppearance.Success, TimeSpan.FromSeconds(3));
        }

        public void ShowShareStopped()
        {
            Show("分享已停止", "局域网分享服务已关闭", ControlAppearance.Info, TimeSpan.FromSeconds(4));
        }

        public void ShowImportComplete(string kbName, int fileCount)
        {
            Show("导入完成", $"知识库：{kbName}\n文件：{fileCount} 个", ControlAppearance.Success, TimeSpan.FromSeconds(3));
        }

        public void ShowAgentChanged(string kbName, string filePath)
        {
            Show("文件已变更", $"知识库：{kbName}\n文件：{System.IO.Path.GetFileName(filePath)}", ControlAppearance.Info, TimeSpan.FromSeconds(4));
        }

        public void Show(string title, string message)
        {
            Show(title, message, ControlAppearance.Info, TimeSpan.FromSeconds(4));
        }

        /// <summary>
        /// 显示自定义 toast（用于更新提示等长时间显示场景）。
        /// </summary>
        public void Show(string title, string message, ControlAppearance appearance, TimeSpan timeout)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            { dispatcher.BeginInvoke(() => Show(title, message, appearance, timeout)); return; }
            var key = title + "\n" + message;
            if (key == _lastNotice && DateTime.UtcNow - _lastNoticeAt < TimeSpan.FromSeconds(2)) return;
            _lastNotice = key; _lastNoticeAt = DateTime.UtcNow;
            if (_presenter == null) { _snackbarService.Show(title, message, appearance, null, timeout); return; }
            new Snackbar(_presenter) { Title = title, Content = message, Appearance = appearance, Timeout = timeout }.Show();
        }

        /// <summary>
        /// 带一个主要动作的通知（00§9.6：最多一个主要动作及关闭）。
        /// WPF-UI 4.3.0 的 Snackbar 无 ActionButton API，按 §9.9 在 Content 内放“正文 + 一个动作按钮”。
        /// 例：N03“已移入回收站，可恢复”+撤销；N04“复制失败”+重试复制。
        /// </summary>
        public void ShowWithAction(
            string title,
            string message,
            ControlAppearance appearance,
            string actionLabel,
            ICommand actionCommand,
            object? actionParameter = null,
            TimeSpan? timeout = null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            { dispatcher.BeginInvoke(() => ShowWithAction(title, message, appearance, actionLabel, actionCommand, actionParameter, timeout)); return; }
            if (_presenter == null) { Show(title, message, appearance, timeout ?? TimeSpan.FromSeconds(8)); return; }
            var button = new Wpf.Ui.Controls.Button { Content = actionLabel, Appearance = ControlAppearance.Transparent, VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) =>
            {
                if (!actionCommand.CanExecute(actionParameter)) return;
                button.IsEnabled = false;
                actionCommand.Execute(actionParameter);
            };
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition());
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var body = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(body); Grid.SetColumn(button, 1); panel.Children.Add(button);
            new Snackbar(_presenter) { Title = title, Content = panel, Appearance = appearance, Timeout = timeout ?? TimeSpan.FromSeconds(8) }.Show();
        }

        public void ShowSuccess(string message)
        {
            Show("成功", message, ControlAppearance.Success, TimeSpan.FromSeconds(3));
        }

        public void ShowError(string message)
        {
            Show("错误", message, ControlAppearance.Danger, TimeSpan.FromSeconds(8));
        }

        public void ShowWarning(string message)
        {
            Show("警告", message, ControlAppearance.Caution, TimeSpan.FromSeconds(8));
        }

    }
}
