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
        ViewModel.Workspace.ConfirmUnsaved = async () => await _unsavedDlg.ShowChoiceAsync(string.IsNullOrEmpty(ViewModel.Workspace.CurrentRelativePath) ? null : new[] { ViewModel.Workspace.CurrentRelativePath }, CancellationToken.None);
        ViewModel.ResolveConflict = async conflicts =>
        {
            var dialog = new ImportConflictWindow(conflicts) { Owner = Window.GetWindow(this) };
            return dialog.ShowDialog() == true ? dialog.Choice : SourceConflictPolicy.Ask;
        };
        ViewModel.MarkVisited();
    }

    private async void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is SourceRow row) await ViewModel.SelectRowAsync(row);
    }

    // 02§5：新建菜单 Markdown/纯文本，默认 md
    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = sender as FrameworkElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var markdown = new System.Windows.Controls.MenuItem { Header = "Markdown 文档（.md）" };
        markdown.Click += (_, _) => ViewModel.NewSourceCommand.Execute("md");
        menu.Items.Add(markdown);
        var plain = new System.Windows.Controls.MenuItem { Header = "纯文本（.txt）" };
        plain.Click += (_, _) => ViewModel.NewSourceCommand.Execute("txt");
        menu.Items.Add(plain);
        menu.IsOpen = true;
    }

    private void OnAddFilesClick(object sender, RoutedEventArgs e) => ViewModel.AddFilesCommand.Execute(null);

    private void OnTrashClick(object sender, RoutedEventArgs e)
    {
        var window = new TrashWindow(ViewModel) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        ViewModel.Reload();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => ViewModel.OpenKbFolderCommand.Execute(null);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.RefreshCommand.Execute(null);

    private void OnDismissImportClick(object sender, RoutedEventArgs e) => ViewModel.DismissImportResultsCommand.Execute(null);

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = sender as FrameworkElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var trash = new System.Windows.Controls.MenuItem { Header = "回收站…" };
        trash.Click += (_, _) => OnTrashClick(sender, e);
        menu.Items.Add(trash);
        var folder = new System.Windows.Controls.MenuItem { Header = "打开文件夹" };
        folder.Click += (_, _) => OnOpenFolderClick(sender, e);
        menu.Items.Add(folder);
        var refresh = new System.Windows.Controls.MenuItem { Header = "刷新并扫描源版本" };
        refresh.Click += (_, _) => OnRefreshClick(sender, e);
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        var clearFilter = new System.Windows.Controls.MenuItem { Header = "清空筛选" };
        clearFilter.Click += (_, _) =>
        {
            ViewModel.SearchKeyword = "";
            ViewModel.TypeFilter = "全部";
        };
        menu.Items.Add(clearFilter);
        menu.IsOpen = true;
    }

    private void OnOpenOriginalClick(object sender, RoutedEventArgs e) => ViewModel.Workspace.OpenWithDefaultProgramCommand.Execute(null);

    private void OnPagePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            FileFilterBox.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.F5)
        {
            ViewModel.RefreshCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            // Ctrl+S 保存（00§6）；无变化不写盘（CanSave 同口径：新建或已修改才触发）
            var workspace = ViewModel.Workspace;
            if (workspace.Mode == WorkspaceMode.Editing
                && workspace.SaveState != WorkspaceSaveState.Saving
                && (workspace.IsNewDocument || workspace.IsDirty))
            {
                _ = workspace.SaveAsync();
            }
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.N && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
            && System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            ViewModel.NewSourceCommand.Execute("md");
            e.Handled = true;
        }
    }

    private async void OnCreateKbClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) await window.ShowCreateKbAsync();
    }
}
