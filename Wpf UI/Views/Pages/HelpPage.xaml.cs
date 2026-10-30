using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;

namespace LLM_Wiki.Views.Pages;

public partial class HelpPage
{
    public HelpViewModel ViewModel { get; }

    public HelpPage(HelpViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.RefreshFromNavigation();
            ViewModel.MarkVisited();
        };
    }

    private async void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pageKey }) await ViewModel.NavigateAsync(pageKey);
    }

    private async void OnReturnClick(object sender, RoutedEventArgs e) => await ViewModel.ReturnAsync();

    private void OnClearSearchClick(object sender, RoutedEventArgs e) => ViewModel.ClearSearchCommand.Execute(null);

    private void OnPagePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            TopicFilterBox.Focus();
            e.Handled = true;
        }
    }
}
