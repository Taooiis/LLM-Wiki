using System.Windows;

using LLM_Wiki.ViewModels;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Dialogs
{
    /// <summary>统一未保存确认（指南 §11.4）：不用 MessageBox，按钮语义明确。</summary>
    public partial class UnsavedChangesDialog : ContentDialog
    {
        public UnsavedChangesDialog()
        {
            InitializeComponent();
        }

        /// <summary>显示并映射结果；Esc/关闭 = 继续编辑。</summary>
        public async Task<UnsavedChoice> ShowChoiceAsync(CancellationToken cancellationToken)
        {
            // 资源中声明的对话框不在可视树内，需显式挂到窗口的 ContentDialogHost（WPF-UI 4.3）。
            if (DialogHostEx == null && Application.Current?.MainWindow is { } window)
            {
                DialogHostEx = ContentDialogHost.GetForWindow(window);
            }

            var result = await base.ShowAsync(cancellationToken);
            return result switch
            {
                ContentDialogResult.Primary => UnsavedChoice.SaveAndLeave,
                ContentDialogResult.Secondary => UnsavedChoice.Discard,
                _ => UnsavedChoice.Continue
            };
        }
    }
}
