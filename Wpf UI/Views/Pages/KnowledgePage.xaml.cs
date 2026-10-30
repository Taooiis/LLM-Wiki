using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;
using LLM_Wiki.Views.Windows;

namespace LLM_Wiki.Views.Pages;

public partial class KnowledgePage
{
    public KnowledgeViewModel ViewModel { get; }
    private readonly UnsavedChangesDialog _unsavedDlg;

    public KnowledgePage(KnowledgeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        _unsavedDlg = (UnsavedChangesDialog)Resources["UnsavedDlg"];
        ViewModel.Workspace.ConfirmUnsaved = async () => await _unsavedDlg.ShowChoiceAsync(CancellationToken.None);
        ViewModel.MarkVisited();
    }

    private async void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is KnowledgeTreeNode node && !node.IsFolder
            && !node.RelPath.Equals(ViewModel.CurrentRelPath, StringComparison.OrdinalIgnoreCase))
            await ViewModel.OpenPageAsync(node.RelPath);
    }

    private async void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LinkChip chip)
            await ViewModel.OpenLinkAsync(chip);
    }

    private void OnSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SourceChip chip)
            ViewModel.OpenSource(chip);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = sender as System.Windows.FrameworkElement, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var manual = new System.Windows.Controls.MenuItem { Header = "手动修订知识…" };
        manual.Click += (_, _) => ViewModel.BeginManualRevisionCommand.Execute(null);
        menu.Items.Add(manual);
        var clear = new System.Windows.Controls.MenuItem { Header = "解除本页人工修订保护" };
        clear.Click += (_, _) => ViewModel.ClearManualProtectionCommand.Execute(null);
        menu.Items.Add(clear);
        var copy = new System.Windows.Controls.MenuItem { Header = "复制当前路径" };
        copy.Click += (_, _) => ViewModel.CopyPathCommand.Execute(null);
        menu.Items.Add(copy);
        menu.IsOpen = true;
    }

    private void OnGoSourcesClick(object sender, RoutedEventArgs e) => ViewModel.GoSourcesCommand.Execute(null);
    private void OnGoOrganizeClick(object sender, RoutedEventArgs e) => ViewModel.GoOrganizeCommand.Execute(null);

    private async void OnCreateKbClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) await window.ShowCreateKbAsync();
    }
}
