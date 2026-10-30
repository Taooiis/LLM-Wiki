using System.Windows;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

namespace LLM_Wiki.Views.Pages;

public partial class OrganizePage
{
    public OrganizeViewModel ViewModel { get; }
    private readonly ConfirmationDialog _confirmDlg;

    public OrganizePage(OrganizeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        _confirmDlg = (ConfirmationDialog)Resources["ConfirmDlg"];
        ViewModel.MarkVisited();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsScopeSelected) ViewModel.SelectAllSourcesCommand.Execute(null);
        else ViewModel.SelectAllPendingCommand.Execute(null);
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsScopeSelected) ViewModel.ClearSourceSelectionCommand.Execute(null);
        else ViewModel.ClearPendingSelectionCommand.Execute(null);
    }
    private void OnAcknowledgeRulesClick(object sender, RoutedEventArgs e) => ViewModel.AcknowledgeRulesCommand.Execute(null);
    private void OnIgnoreRulesClick(object sender, RoutedEventArgs e) => ViewModel.IgnoreRulesCommand.Execute(null);

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = sender as System.Windows.FrameworkElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var plain = new System.Windows.Controls.MenuItem { Header = "导出任务包（不含资料）", IsEnabled = ViewModel.HasTask };
        plain.Click += (_, _) => ViewModel.ExportTaskCommand.Execute("plain");
        menu.Items.Add(plain);
        var withSources = new System.Windows.Controls.MenuItem { Header = "导出任务包（含本次资料与规则）", IsEnabled = ViewModel.HasTask };
        withSources.Click += (_, _) => ViewModel.ExportTaskCommand.Execute("with-sources");
        menu.Items.Add(withSources);
        menu.Items.Add(new System.Windows.Controls.Separator());
        var sources = new System.Windows.Controls.MenuItem { Header = "去资料页" };
        sources.Click += (_, _) => ViewModel.GoSourcesCommand.Execute(null);
        menu.Items.Add(sources);
        menu.IsOpen = true;
    }

    private async void OnForceKillClick(object sender, RoutedEventArgs e)
    {
        // D12 强制结束本次进程：520 宽、危险主按钮、初焦“继续等待”。
        // 提交后由 ForceKill 再核 PID、启动时间与程序身份归属；核验失败只提示、不结束进程。
        var detail = $"工具：Codex CLI\n知识库：{ViewModel.SelectedKb}\n{ViewModel.AgentRunStatus}\n\n"
                     + "只结束能够确认属于本次任务的进程；已经写入的文件不会自动回滚。";
        var confirmed = await _confirmDlg.ShowAsync(
            "再核进程归属后强制结束本次进程。",
            "强制结束本次进程",
            "强制结束本次进程",
            "继续等待",
            danger: true,
            dialogWidth: 520,
            detail: detail);
        if (confirmed) ViewModel.ForceKillCommand.Execute(null);
    }
}
