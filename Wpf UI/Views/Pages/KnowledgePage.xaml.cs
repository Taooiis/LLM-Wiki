using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using LLM_Wiki.ViewModels;
using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;
using LLM_Wiki.Views.Windows;

namespace LLM_Wiki.Views.Pages;

public partial class KnowledgePage
{
    public KnowledgeViewModel ViewModel { get; }
    private readonly UnsavedChangesDialog _unsavedDlg;
    private readonly ConfirmationDialog _confirmDlg;

    public KnowledgePage(KnowledgeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        _unsavedDlg = (UnsavedChangesDialog)Resources["UnsavedDlg"];
        _confirmDlg = (ConfirmationDialog)Resources["ConfirmDlg"];
        ViewModel.Workspace.ConfirmUnsaved = async () => await _unsavedDlg.ShowChoiceAsync(string.IsNullOrEmpty(ViewModel.Workspace.CurrentRelativePath) ? null : new[] { ViewModel.Workspace.CurrentRelativePath }, CancellationToken.None);
        ViewModel.ConfirmClearProtection = async relPath =>
        {
            var version = ViewModel.Workspace.CurrentVersion;
            var summary = string.IsNullOrEmpty(version) ? "当前版本" : "版本 " + version[..Math.Min(8, version.Length)];
            return await _confirmDlg.ShowAsync(
                $"将解除“{System.IO.Path.GetFileName(relPath)}”当前人工修订版本的保护。\n相对路径：{relPath}\n{summary}\n\n后续 AI 写入仍需满足路径与版本校验，源资料不会改变；本操作只解绑当前版本。",
                "允许后续任务修改此知识页？",
                "解除当前版本保护");
        };
        // D04 磁盘版本冲突：“载入磁盘版本”丢弃草稿前需显式确认（初焦在“返回编辑”）
        ViewModel.Workspace.ConfirmLoadDiskVersion = () => _confirmDlg.ShowAsync(
            "磁盘上的知识文件已有新版本。载入磁盘版本会丢弃当前未保存的手动修订草稿。",
            "磁盘版本冲突",
            "载入磁盘版本",
            "返回编辑",
            danger: true,
            dialogWidth: 520);
        ViewModel.MarkVisited();
    }

    private async void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is KnowledgeTreeNode node && !node.IsFolder
            && !node.RelPath.Equals(ViewModel.CurrentRelPath, StringComparison.OrdinalIgnoreCase))
            await ViewModel.OpenPageAsync(node.RelPath);
    }

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not LinkChip chip) return;
        if (chip.Candidates.Count > 1)
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (var candidate in chip.Candidates)
            {
                var item = new MenuItem { Header = candidate };
                var target = candidate;
                item.Click += async (_, _) => await ViewModel.OpenPageAsync(target);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
            return;
        }
        _ = ViewModel.OpenLinkAsync(chip);
    }

    private void OnSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SourceChip chip)
            ViewModel.OpenSource(chip);
    }

    private void OnTocClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var heading in ViewModel.HeadingItems)
        {
            var item = new MenuItem { Header = heading.Text };
            var text = heading.Text;
            item.Click += (_, _) => WorkspaceView.ScrollToHeading(text);
            menu.Items.Add(item);
        }
        menu.IsOpen = menu.Items.Count > 0;
    }

    private void OnHeadingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (HeadingList.SelectedItem is HeadingChip heading)
            WorkspaceView.ScrollToHeading(heading.Text);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as FrameworkElement, Placement = PlacementMode.Bottom };
        var manual = new MenuItem { Header = "手动修订知识…", IsEnabled = ViewModel.HasCurrentPage && !ViewModel.IsManualEditing };
        manual.Click += (_, _) => ViewModel.BeginManualRevisionCommand.Execute(null);
        menu.Items.Add(manual);

        var clear = new MenuItem
        {
            Header = "解除本页人工修订保护…",
            IsEnabled = ViewModel.HasManualProtection,
        };
        if (!ViewModel.HasManualProtection)
            clear.ToolTip = "当前页没有可解除的人工修订保护";
        clear.Click += (_, _) => ViewModel.ClearManualProtectionCommand.Execute(null);
        menu.Items.Add(clear);

        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "复制当前路径" };
        copy.Click += (_, _) => ViewModel.CopyPathCommand.Execute(null);
        menu.Items.Add(copy);
        var folder = new MenuItem { Header = "打开知识库文件夹" };
        folder.Click += (_, _) => ViewModel.OpenKbFolderCommand.Execute(null);
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    private void OnPagePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            TreeFilterBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            // 00§6 Ctrl+S 保存编辑：页面级隧道事件，焦点不在编辑框时同样生效（SaveAsync 内部自校验编辑态/保存中）
            if (ViewModel.Workspace.Mode == WorkspaceMode.Editing)
            {
                _ = ViewModel.Workspace.SaveAsync();
                e.Handled = true;
            }
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            // 00§6 Esc 先处理未保存：编辑中交由既有 D02 保护流程（留在此处/保存/放弃），不直接丢草稿
            if (ViewModel.Workspace.Mode == WorkspaceMode.Editing)
            {
                _ = ViewModel.Workspace.CancelEditCommand.ExecuteAsync(null);
                e.Handled = true;
            }
        }
        else if (e.Key == System.Windows.Input.Key.F5)
        {
            ViewModel.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnGoSourcesClick(object sender, RoutedEventArgs e) => ViewModel.GoSourcesCommand.Execute(null);
    private void OnGoOrganizeClick(object sender, RoutedEventArgs e) => ViewModel.GoOrganizeCommand.Execute(null);

    private async void OnCreateKbClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) await window.ShowCreateKbAsync();
    }
}
