using System.Windows;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;
using LLM_Wiki.Views.Windows;

namespace LLM_Wiki.Views.Pages;

public partial class KbSettingsPage
{
    public KbSettingsViewModel ViewModel { get; }
    private readonly UnsavedChangesDialog _unsavedDlg;
    private readonly ConfirmationDialog _confirmDlg;

    public KbSettingsPage(KbSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        _unsavedDlg = (UnsavedChangesDialog)Resources["UnsavedDlg"];
        _confirmDlg = (ConfirmationDialog)Resources["ConfirmDlg"];
        ViewModel.ConfirmUnsaved = async () => await _unsavedDlg.ShowChoiceAsync(CancellationToken.None);
        ViewModel.MarkVisited();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDelete) return;
        var confirmed = await _confirmDlg.ShowAsync(
            $"将永久删除知识库“{ViewModel.SelectedKb}”及其全部资料、知识、任务状态和回收站，无法恢复。",
            "永久删除知识库",
            "永久删除此知识库");
        if (!confirmed) return;
        if (Window.GetWindow(this) is MainWindow window)
            await window.ViewModel.DeleteCurrentKbAsync();
    }
}
