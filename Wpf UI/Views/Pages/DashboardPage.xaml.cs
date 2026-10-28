using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class DashboardPage : INavigableView<DashboardViewModel>
    {
        public DashboardViewModel ViewModel { get; }

        public DashboardPage(DashboardViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        private void KbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not string kb)
            {
                return;
            }

            ViewModel.LoadFiles(kb);
        }
    }
}
