using System.IO;
using System.Windows;
using Microsoft.Win32;

using LLM_Wiki.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class ConfigPage : INavigableView<ConfigViewModel>
    {
        public ConfigViewModel ViewModel { get; }

        public ConfigPage(ConfigViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        private void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择知识库存储目录",
                InitialDirectory = string.IsNullOrWhiteSpace(ViewModel.WikisDirInput)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : ViewModel.WikisDirInput
            };

            if (dlg.ShowDialog() == true)
            {
                ViewModel.WikisDirInput = dlg.FolderName;
                ViewModel.ValidateWikisDir();
            }
        }

        public void OnNavigatedTo(string kb)
        {
            ViewModel.LoadKb(kb);
        }
    }
}
