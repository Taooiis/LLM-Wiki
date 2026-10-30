using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    /// <summary>目录浏览页（设计 03）：左树 + 右复用 DocumentWorkspace；切文件/切库前未保存保护。</summary>
    public partial class TreeBrowserPage : INavigableView<TreeBrowserViewModel>
    {
        public TreeBrowserViewModel ViewModel { get; }

        private readonly UnsavedChangesDialog _unsavedDlg;

        public TreeBrowserPage(TreeBrowserViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();

            _unsavedDlg = (UnsavedChangesDialog)Resources["UnsavedDlg"];

            // 工作区未保存确认回调（07§2，与源文档页同一对话框）
            ViewModel.Workspace.ConfirmUnsaved = async () =>
                await _unsavedDlg.ShowChoiceAsync(System.Threading.CancellationToken.None);
        }

        /// <summary>
        /// 选中文件节点：经未保存保护后加载预览；
        /// 用户选择"继续编辑"时把选中视觉回退到原节点（03§4.2 不丢编辑）。
        /// </summary>
        private async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeItemViewModel item || item.IsFolder)
            {
                return;
            }

            var ok = await ViewModel.SelectNodeAsync(item);
            if (!ok && e.OldValue is TreeItemViewModel previous && !previous.IsFolder)
            {
                previous.IsSelected = true;
            }
        }
    }
}
