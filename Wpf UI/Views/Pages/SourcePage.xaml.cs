using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class SourcePage : INavigableView<SourceViewModel>
    {
        public SourceViewModel ViewModel { get; }

        public SourcePage(SourceViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not string file)
            {
                return;
            }

            ViewModel.LoadFileContent(ViewModel.SelectedKb, file);
        }

        public void OnNavigatedTo(string kb)
        {
            ViewModel.LoadKb(kb);
        }
    }
}
