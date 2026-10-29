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

        public void ShowShareStarted(int port)
        {
            // 静默：分享状态在 UI 页面显示，不弹窗打扰
        }

        public void ShowShareStopped()
        {
            // 静默
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

        public void ShowSuccess(string message)
        {
            _snackbarService.Show("成功", message, ControlAppearance.Success, null, TimeSpan.FromSeconds(2.6));
        }

        public void ShowError(string message)
        {
            _snackbarService.Show("错误", message, ControlAppearance.Danger, null, TimeSpan.FromSeconds(2.6));
        }
    }
}