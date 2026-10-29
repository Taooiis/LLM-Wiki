using System;
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

        private void OnEditorSaved(string content)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedKb) || string.IsNullOrWhiteSpace(ViewModel.SelectedFile))
            {
                return;
            }

            var error = "";
            var ok = ViewModel.SaveFile(ViewModel.SelectedKb, "raw/sources/" + ViewModel.SelectedFile, content, out error);
            if (!ok)
            {
                ShowToast("保存失败：" + error, false);
                return;
            }

            ViewModel.DocumentContent = content;
            PreviewText.Text = content;
            ViewModel.LoadChanges(ViewModel.SelectedKb);
            // E4/E6：清除 dirty 标记
            _editorDlg.ClearDirty();
            // E7：toast 提示
            ShowToast("已保存：" + ViewModel.SelectedFile, true);
        }

        private void OnUploadClick(object sender, RoutedEventArgs e)
        {
            ViewModel.UploadFileCommand.Execute(null);
        }

        private void ShowToast(string message, bool success)
        {
            var appearance = success ? Wpf.Ui.Controls.ControlAppearance.Success : Wpf.Ui.Controls.ControlAppearance.Danger;
            _snackbarService.Show(success ? "成功" : "错误", message, appearance, null, TimeSpan.FromSeconds(2.6));
        }
    }
}
