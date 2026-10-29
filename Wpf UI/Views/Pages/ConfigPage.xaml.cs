using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class ConfigPage : INavigableView<ConfigViewModel>
    {
        public ConfigViewModel ViewModel { get; }
        private readonly FolderPickerDialog _folderPicker;

        public ConfigPage(ConfigViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();

            _folderPicker = (FolderPickerDialog)Resources["FolderPickerDlg"];
        }

        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            _folderPicker.InitialDirectory = string.IsNullOrWhiteSpace(ViewModel.WikisDirInput)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LLM Wiki", "wikis")
                : ViewModel.WikisDirInput;

            var result = await _folderPicker.ShowAsync(CancellationToken.None);
            if (result == ContentDialogResult.Primary)
            {
                ViewModel.WikisDirInput = _folderPicker.SelectedPath;
                ViewModel.ValidateWikisDir();
            }
        }

        public void OnNavigatedTo(string kb)
        {
            ViewModel.LoadKb(kb);
        }
    }
}
