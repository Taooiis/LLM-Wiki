using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class SourcePage : INavigableView<SourceViewModel>
    {
        public SourceViewModel ViewModel { get; }

        private readonly EditorDialog _editorDlg;
        private readonly ISnackbarService _snackbarService;

        public SourcePage(SourceViewModel viewModel, ISnackbarService snackbarService)
        {
            ViewModel = viewModel;
            _snackbarService = snackbarService;
            DataContext = this;

            InitializeComponent();

            _editorDlg = (EditorDialog)Resources["EditorDlg"];
            _editorDlg.Saved += OnEditorSaved;
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not string file)
            {
                return;
            }

            ViewModel.SelectedFile = file;
            ViewModel.LoadFileContent(ViewModel.SelectedKb, file);
            PreviewText.Text = ViewModel.DocumentContent;
            EditButton.IsEnabled = true;
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedKb) || string.IsNullOrWhiteSpace(ViewModel.SelectedFile))
            {
                return;
            }

            _editorDlg.DialogMode = EditorDialog.Mode.Exist;
            _editorDlg.FileName = ViewModel.SelectedFile;
            _editorDlg.FilePath = "raw/sources/" + ViewModel.SelectedFile;
            _editorDlg.SetLoadedContent(ViewModel.DocumentContent);
            _editorDlg.ShowAsync(System.Threading.CancellationToken.None);
        }

        private void OnUploadClick(object sender, RoutedEventArgs e)
        {
            ViewModel.UploadFileCommand.Execute(null);
        }

        /// <summary>新建文档：弹编辑器（New 模式），保存时由 OnEditorSaved 落盘。</summary>
        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedKb))
            {
                ShowToast("请先选择知识库", false);
                return;
            }

            _editorDlg.DialogMode = EditorDialog.Mode.New;
            _editorDlg.FileName = "";
            _editorDlg.FilePath = "raw/sources/";
            _editorDlg.SetLoadedContent("");
            _editorDlg.ShowAsync(System.Threading.CancellationToken.None);
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
                PreviewText.Text = "";
                EditButton.IsEnabled = false;
            }
        }

        private void OnEditorSaved(string content)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedKb))
            {
                return;
            }

            // New 模式：弹窗 OnButtonClick 已把 FilePath 设为 "raw/sources/文件名"（含补后缀）；
            // Exist 模式：以当前选中文件落盘
            var isNew = _editorDlg.DialogMode == EditorDialog.Mode.New;
            var rel = isNew ? _editorDlg.FilePath : "raw/sources/" + ViewModel.SelectedFile;
            if (string.IsNullOrWhiteSpace(rel) || rel == "raw/sources/")
            {
                ShowToast("文件名不能为空", false);
                return;
            }
            var name = System.IO.Path.GetFileName(rel);

            var error = "";
            var ok = ViewModel.SaveFile(ViewModel.SelectedKb, rel, content, out error);
            if (!ok)
            {
                ShowToast("保存失败：" + error, false);
                return;
            }

            ViewModel.LoadFiles(ViewModel.SelectedKb);
            ViewModel.DocumentContent = content;
            PreviewText.Text = content;

            if (isNew)
            {
                ViewModel.SelectedFile = name;
                ShowToast("已创建：" + name + "，去工作流页重新 Ingest 该文档", true);
            }
            else
            {
                ShowToast("已保存：" + name + "，去工作流页重新 Ingest 该文档", true);
            }

            ViewModel.LoadChanges(ViewModel.SelectedKb);
            // E4/E6：清除 dirty 标记
            _editorDlg.ClearDirty();
        }

        private void ShowToast(string message, bool success)
        {
            var appearance = success
                ? Wpf.Ui.Controls.ControlAppearance.Success
                : Wpf.Ui.Controls.ControlAppearance.Danger;
            _snackbarService.Show(success ? "成功" : "错误", message, appearance, null, TimeSpan.FromSeconds(2.6));
        }
    }
}