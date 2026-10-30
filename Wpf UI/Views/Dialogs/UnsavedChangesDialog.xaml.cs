using System.Collections.Generic;
using System.Windows;

using LLM_Wiki.ViewModels;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Dialogs
{
    /// <summary>
    /// 统一未保存确认（D02，00§9.4）：标题“还有未保存的更改”，列真实文件名与相对路径。
    /// 三按钮语义映射：Secondary=放弃更改，Close=留在此处（Esc 等价，不丢内容），Primary=保存并继续。
    /// </summary>
    public partial class UnsavedChangesDialog : ContentDialog
    {
        public UnsavedChangesDialog()
        {
            InitializeComponent();
        }

        private bool _configured;

        /// <summary>显示并映射结果；Esc/关闭/遮罩 = 留在此处。</summary>
        /// <param name="files">待保存文件（相对路径）；多文件时主按钮文案为“保存全部并继续”。</param>
        public async Task<UnsavedChoice> ShowChoiceAsync(IReadOnlyList<string>? files = null, CancellationToken cancellationToken = default)
        {
            // 资源中声明的对话框不在可视树内，需显式挂到窗口的 ContentDialogHost（WPF-UI 4.3）。
            if (DialogHostEx == null && Application.Current?.MainWindow is { } window)
            {
                DialogHostEx = ContentDialogHost.GetForWindow(window);
            }

            if (!_configured)
            {
                Title = "还有未保存的更改";
                LeadTextBlock.Text = "离开前，请处理以下文件的修改。";
                if (files is { Count: > 0 })
                {
                    FilesText.Text = string.Join("\n", files);
                    PrimaryButtonText = files.Count > 1 ? "保存全部并继续" : "保存并继续";
                }
                else
                {
                    FilesText.Text = "";
                    PrimaryButtonText = "保存并继续";
                }
                SecondaryButtonText = "放弃更改";
                CloseButtonText = "留在此处";
            }

            try
            {
                var result = await base.ShowAsync(cancellationToken);
                return result switch
                {
                    ContentDialogResult.Primary => UnsavedChoice.SaveAndLeave,
                    ContentDialogResult.Secondary => UnsavedChoice.Discard,
                    _ => UnsavedChoice.Continue,
                };
            }
            finally
            {
                _configured = false;
            }
        }

        /// <summary>
        /// D13 退出保护（00§9.4）：自定义标题/说明与三按钮文案。
        /// 槽位语义不变：Secondary=轻动作（放弃草稿并取消任务后退出 / 取消任务后退出），
        /// Close=留在应用（Esc、初焦），Primary=保存并取消任务后退出（可空=两按钮）。
        /// </summary>
        public void ConfigureExit(string title, string lead, string? detail, string secondary, string primary, string close)
        {
            _configured = true;
            Title = title;
            LeadTextBlock.Text = lead;
            FilesText.Text = detail ?? "";
            SecondaryButtonText = secondary;
            PrimaryButtonText = primary;
            CloseButtonText = close;
        }
    }
}
