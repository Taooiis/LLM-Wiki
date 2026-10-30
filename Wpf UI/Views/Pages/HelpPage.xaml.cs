using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.Views.Pages;

public partial class HelpPage
{
    public HelpPage(MainWindowViewModel main)
    {
        InitializeComponent();
        main.RememberPage("help");
    }
}
