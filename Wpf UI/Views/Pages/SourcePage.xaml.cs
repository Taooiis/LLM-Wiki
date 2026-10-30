using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels;
using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;
using LLM_Wiki.Views.Windows;

namespace LLM_Wiki.Views.Pages;

public partial class SourcePage
{
    public SourceViewModel ViewModel { get; }
    private readonly UnsavedChangesDialog _unsavedDlg;

    public SourcePage(SourceViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        _unsavedDlg = (UnsavedChangesDialog)Resources["UnsavedDlg"];
        ViewModel.Workspace.ConfirmUnsaved = async () => await _unsavedDlg.ShowChoiceAsync(CancellationToken.None);
        ViewModel.ResolveConflict = async names =>
        {
            var dialog = new ImportConflictWindow(names) { Owner = Window.GetWindow(this) };
            return dialog.ShowDialog() == true ? dialog.Choice : SourceConflictPolicy.Ask;
        };
        ViewModel.MarkVisited();
    }

    private async void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is SourceRow row) await ViewModel.SelectRowAsync(row);
    }

    private void OnNewClick(object sender, RoutedEventArgs e) => ViewModel.NewSourceCommand.Execute(null);

    private void OnAddFilesClick(object sender, RoutedEventArgs e) => ViewModel.AddFilesCommand.Execute(null);

    private void OnTrashClick(object sender, RoutedEventArgs e)
    {
        var window = new TrashWindow(ViewModel) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        ViewModel.Reload();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => ViewModel.OpenKbFolderCommand.Execute(null);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.RefreshCommand.Execute(null);

    private void OnOpenOriginalClick(object sender, RoutedEventArgs e) => ViewModel.Workspace.OpenWithDefaultProgramCommand.Execute(null);

    private async void OnCreateKbClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) await window.ShowCreateKbAsync();
    }
}
