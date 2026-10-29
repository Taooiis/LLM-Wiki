using System.Windows;

using Wpf.Ui;
using Wpf.Ui.Controls;



namespace LLM_Wiki.Services
{
    /// <summary>
    /// 托停通知（Toast）服务：分享开启、导入完成等。
    /// 使用 Wpf.Ui Snackbar 实现底部轻提示。
    /// </summary>
    public class ToastService
    {
        private readonly ISnackbarService _snackbarService;

        public ToastService(ISnackbarService snackbarService)
        {
            _snackbarService = snackbarService;
        }

        /// <summary>
        /// 分享开启：显示端口 + 局域网地址。
        /// </summary>
        public void ShowShareStarted(int port)
        {
            _snackbarService.Show("分享已开启", $"端口 {port}，局域网访问", ControlAppearance.Success, null, TimeSpan.FromSeconds(5));
        }

        public void ShowShareStopped()
        {
            _snackbarService.Show("分享已停止", "局域网分享服务已关闭", ControlAppearance.Info, null, TimeSpan.FromSeconds(2.6));
        }

        public void ShowImportComplete(string kbName, int fileCount)
        {
            _snackbarService.Show("导入完成", $"知识库：{kbName}\n文件：{fileCount} 个", ControlAppearance.Success, null, TimeSpan.FromSeconds(2.6));
        }

        public void ShowAgentChanged(string kbName, string filePath)
        {
            _snackbarService.Show("文件已变更", $"知识库：{kbName}\n文件：{System.IO.Path.GetFileName(filePath)}", ControlAppearance.Info, null, TimeSpan.FromSeconds(2.6));
        }

        public void Show(string title, string message)
        {
            _snackbarService.Show(title, message, ControlAppearance.Info, null, TimeSpan.FromSeconds(2.6));
        }

        /// <summary>
        /// 显示自定义 toast（用于更新提示等长时间显示场景）。
        /// </summary>
        public void Show(string title, string message, ControlAppearance appearance, TimeSpan timeout)
        {
            _snackbarService.Show(title, message, appearance, null, timeout);
        }

        public void ShowSuccess(string message)
        {
            _snackbarService.Show("成功", message, ControlAppearance.Success, null, TimeSpan.FromSeconds(2.6));
        }

        public void ShowError(string message)
        {
            _snackbarService.Show("错误", message, ControlAppearance.Danger, null, TimeSpan.FromSeconds(2.6));
        }

        public void ShowWarning(string message)
        {
            _snackbarService.Show("警告", message, ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
        }
    }
}