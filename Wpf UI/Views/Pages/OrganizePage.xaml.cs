using System.Windows;

using LLM_Wiki.ViewModels.Pages;

namespace LLM_Wiki.Views.Pages;

public partial class OrganizePage
{
    public OrganizeViewModel ViewModel { get; }

    public OrganizePage(OrganizeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        ViewModel.MarkVisited();
    }

    private void OnSelectAllPendingClick(object sender, RoutedEventArgs e) => ViewModel.SelectAllPendingCommand.Execute(null);
    private void OnClearPendingClick(object sender, RoutedEventArgs e) => ViewModel.ClearPendingSelectionCommand.Execute(null);
    private void OnSelectAllSourcesClick(object sender, RoutedEventArgs e) => ViewModel.SelectAllSourcesCommand.Execute(null);
    private void OnClearSourcesClick(object sender, RoutedEventArgs e) => ViewModel.ClearSourceSelectionCommand.Execute(null);
    private void OnAcknowledgeRulesClick(object sender, RoutedEventArgs e) => ViewModel.AcknowledgeRulesCommand.Execute(null);
    private void OnIgnoreRulesClick(object sender, RoutedEventArgs e) => ViewModel.IgnoreRulesCommand.Execute(null);

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = sender as System.Windows.FrameworkElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var plain = new System.Windows.Controls.MenuItem { Header = "导出任务包（不含资料）" };
        plain.Click += (_, _) => ViewModel.ExportTaskCommand.Execute("plain");
        menu.Items.Add(plain);
        var withSources = new System.Windows.Controls.MenuItem { Header = "导出任务包（含本次资料与规则）" };
        withSources.Click += (_, _) => ViewModel.ExportTaskCommand.Execute("with-sources");
        menu.Items.Add(withSources);
        menu.IsOpen = true;
    }
}
