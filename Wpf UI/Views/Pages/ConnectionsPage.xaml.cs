using System.Windows;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

namespace LLM_Wiki.Views.Pages;

public partial class ConnectionsPage
{
    public ConnectionsViewModel ViewModel { get; }
    private readonly ConfirmationDialog _confirmDlg;

    public ConnectionsPage(ConnectionsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        _confirmDlg = (ConfirmationDialog)Resources["ConfirmDlg"];
        ViewModel.ConfirmEnableMcpWrite = async () => await _confirmDlg.ShowAsync(
            "允许此连接修改本库 wiki/ 下的知识文件；不能修改原始资料、库规则或删除文件。已成功提交的写入不能撤销。",
            "开启 MCP 写入",
            "允许写入");
        ViewModel.MarkVisited();
    }

    private void OnReplaceShareRange(object sender, RoutedEventArgs e) => ViewModel.ReplaceShareRangeCommand.Execute(null);
    private void OnGoOrganize(object sender, RoutedEventArgs e) => ViewModel.GoOrganizeCommand.Execute(null);
}
