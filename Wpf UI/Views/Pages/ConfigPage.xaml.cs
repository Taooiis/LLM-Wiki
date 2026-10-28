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

        public void OnNavigatedTo(string kb)
        {
            ViewModel.LoadKb(kb);
        }
    }
}
