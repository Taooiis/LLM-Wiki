using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class SourcePage : INavigableView<SourceViewModel>
    {
        public SourceViewModel ViewModel { get; }

        private readonly EditorDialog _editorDlg;

        public SourcePage(SourceViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            _editorDlg = (EditorDialog)Resources["EditorDlg"];
            _editorDlg.Saved += OnEditorSaved;

            InitializeComponent();
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
            _editorDlg.EditorContent = ViewModel.DocumentContent;
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
                System.Windows.MessageBox.Show("保存失败：" + error, "保存", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            ViewModel.DocumentContent = content;
            PreviewText.Text = content;
            ViewModel.LoadChanges(ViewModel.SelectedKb);
            ShowToast("已保存：" + ViewModel.SelectedFile, true);
        }

        private void ShowToast(string message, bool success)
        {
            System.Windows.MessageBox.Show(message, success ? "成功" : "错误", System.Windows.MessageBoxButton.OK, success ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Error);
        }
    }
}
