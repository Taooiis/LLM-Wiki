using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Dialogs
{
    /// <summary>
    /// 通用确认对话框（Y/N 确认）。
    /// </summary>
    public partial class ConfirmationDialog : ContentDialog
    {
        public ConfirmationDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 显示确认对话框并返回结果。
        /// </summary>
        /// <param name="content">提示内容</param>
        /// <param name="title">标题（默认 "提示"）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>点击确定返回 true，取消返回 false</returns>
        public async Task<bool> ShowAsync(string content, string title = "提示", CancellationToken cancellationToken = default)
        {
            Title = title;
            if (ContentText != null) ContentText.Text = content;
            var result = await base.ShowAsync(cancellationToken);
            return result == ContentDialogResult.Primary;
        }
    }
}