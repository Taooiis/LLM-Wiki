using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.ViewModels;
using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class SourcePage : INavigableView<SourceViewModel>
    {
        public SourceViewModel ViewModel { get; }

        private readonly ISnackbarService _snackbarService;
        private readonly UnsavedChangesDialog _unsavedDlg;

        public SourcePage(SourceViewModel viewModel, ISnackbarService snackbarService)
        {
            ViewModel = viewModel;
            _snackbarService = snackbarService;
            DataContext = this;

            InitializeComponent();

            _unsavedDlg = (UnsavedChangesDialog)Resources["UnsavedDlg"];

            // 工作区的未保存确认回调（指南 §11.4，不用 MessageBox）
            ViewModel.Workspace.ConfirmUnsaved = async () =>
                await _unsavedDlg.ShowChoiceAsync(System.Threading.CancellationToken.None);
        }

        /// <summary>选中文件：经未保存保护后加载到工作区预览。</summary>
        private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not string file)
            {
                return;
            }

            await ViewModel.SelectFileAsync(file);
        }

        /// <summary>新建文档：右侧工作区直接进入编辑（不再弹窗，指南 §6.4）。</summary>
        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedKb))
            {
                ShowToast("请先选择知识库", false);
                return;
            }

            ViewModel.Workspace.EnterNewMode(ViewModel.SelectedKb);
        }

        private void OnUploadClick(object sender, RoutedEventArgs e)
        {
            ViewModel.UploadFileCommand.Execute(null);
        }

        /// <summary>删除当前选中的源文档（带确认）。</summary>
        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedKb) || string.IsNullOrWhiteSpace(ViewModel.SelectedFile))
            {
                ShowToast("请先选中要删除的文档", false);
                return;
            }

            var confirm = (ConfirmationDialog)Resources["ConfirmDlg"];
            if (await confirm.ShowAsync($"确定要删除 \"{ViewModel.SelectedFile}\" 吗？\n此操作不可恢复。", "确认删除"))
            {
                ViewModel.DeleteFileCommand.Execute(ViewModel.SelectedFile);
            }
        }

        private void ShowToast(string message, bool success)
        {
            var appearance = success
                ? ControlAppearance.Success
                : ControlAppearance.Danger;
            _snackbarService.Show(success ? "成功" : "错误", message, appearance, null, TimeSpan.FromSeconds(2.6));
        }
    }
}
