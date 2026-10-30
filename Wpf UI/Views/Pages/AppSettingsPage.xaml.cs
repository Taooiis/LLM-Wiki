using LLM_Wiki.ViewModels.Pages;

namespace LLM_Wiki.Views.Pages;

public partial class AppSettingsPage
{
    public AppSettingsViewModel ViewModel { get; }

    public AppSettingsPage(AppSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        ViewModel.MarkVisited();
    }
}
