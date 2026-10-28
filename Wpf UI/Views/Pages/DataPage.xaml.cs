using LLM_Wiki.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class DataPage : INavigableView<DataViewModel>
    {
        public DataViewModel ViewModel { get; }

        public DataPage(DataViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }
    }
}
