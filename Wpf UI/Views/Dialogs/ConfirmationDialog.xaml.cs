using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Dialogs
{
    /// <summary>
    /// 通用确认对话框（两按钮：取消 → 明确动作，00§9.2）。
    /// 初焦在取消；Esc/标题关闭 = 取消；危险动作可用红色主按钮。
    /// </summary>
    public partial class ConfirmationDialog : ContentDialog
    {
        private bool _initialFocusOnCancel;

        public ConfirmationDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => TryFocusCancelButton();
        }

        /// <summary>
        /// 显示确认对话框并返回结果。
        /// </summary>
        /// <param name="content">提示内容</param>
        /// <param name="title">标题</param>
        /// <param name="primaryButtonText">明确动作文案（删除等危险操作传“删除文件”）</param>
        /// <param name="cancelButtonText">取消侧文案（默认“取消”；D12 传“继续等待”等）</param>
        /// <param name="danger">危险动作：主按钮用危险样式，初焦仍在取消</param>
        /// <param name="dialogWidth">框宽（DIP）：表单 480/520，冲突/批量 640，复杂 760</param>
        /// <param name="detail">补充说明（小字，可空）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>点击明确动作返回 true，取消/Esc/关闭返回 false</returns>
        public async Task<bool> ShowAsync(
            string content,
            string title = "提示",
            string primaryButtonText = "确认",
            string cancelButtonText = "取消",
            bool danger = false,
            double dialogWidth = 480,
            string? detail = null,
            CancellationToken cancellationToken = default)
        {
            // 资源中声明的对话框不在可视树内，需显式挂到窗口的 ContentDialogHost（WPF-UI 4.3）。
            if (DialogHostEx == null)
            {
                var window = Window.GetWindow(this) ?? Application.Current?.MainWindow;
                if (window != null) DialogHostEx = ContentDialogHost.GetForWindow(window);
            }

            Title = title;
            PrimaryButtonText = primaryButtonText;
            SecondaryButtonText = cancelButtonText;
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary;
            DialogWidth = dialogWidth;
            if (ContentText != null) ContentText.Text = content;
            if (DetailText != null)
            {
                DetailText.Text = detail ?? "";
                DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            }

            // Opened 后显式设初焦到取消按钮（00§9.9：不依赖库默认 Primary 焦点）。
            _initialFocusOnCancel = true;
            var result = await base.ShowAsync(cancellationToken);
            return result == ContentDialogResult.Primary;
        }

        private void TryFocusCancelButton()
        {
            if (!_initialFocusOnCancel || !IsLoaded) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Template.FindName("SecondaryButton", this) is System.Windows.Controls.Button cancel && cancel.Visibility == Visibility.Visible)
                {
                    cancel.Focus();
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
