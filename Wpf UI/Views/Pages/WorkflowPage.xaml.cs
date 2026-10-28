using LLM_Wiki.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class WorkflowPage : INavigableView<WorkflowViewModel>
    {
        public WorkflowViewModel ViewModel { get; }

        public WorkflowPage(WorkflowViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        public void OnNavigatedTo(string kb)
        {
            // WorkflowViewModel 通过 MainWindowViewModel.CurrentKb 自动响应
        }
    }
}
