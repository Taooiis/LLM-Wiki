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
        ViewModel.ConfirmAgentWrite = async () => await _confirmDlg.ShowAsync(
            "允许本机 Codex CLI 在当前库目录写入？这是操作系统文件权限，不能强制限制为只写 wiki/；外部工具仍会执行自己的审批。",
            "允许本机工具写入",
            "允许当前库目录写操作");
        ViewModel.ConfirmEnableMcpWrite = async () => await _confirmDlg.ShowAsync(
            "允许此连接修改本库 wiki/ 下的知识文件；不能修改原始资料、库规则或删除文件。已成功提交的写入不能撤销。",
            "开启 MCP 写入",
            "允许写入");
        ViewModel.ConfirmRegeneratePin = async kb => await _confirmDlg.ShowAsync(
            $"旧访问码和已认证的浏览会话将立即失效，需要重新输入新访问码。\n分享库：{kb}",
            "更换访问码？",
            "更换访问码");
        ViewModel.ConfirmRegenerateToken = async target => await _confirmDlg.ShowAsync(
            $"将只轮换以下凭据，另一类令牌和服务范围保持不变：\n{target}\n\n使用旧令牌的连接会立即失效，新值只在对应掩码字段显示。",
            "重新生成访问令牌？",
            "重新生成令牌");
        ViewModel.ConfirmReplaceShareRange = async range => await _confirmDlg.ShowAsync(
            $"{range}\n\n将先停止旧范围的浏览分享，旧连接、访问码与认证会话失效，然后按新库启动。其他独立服务不受影响。",
            "停止服务并更换分享范围？",
            "停止并更换",
            "保持原范围",
            danger: false,
            dialogWidth: 640);
        ViewModel.ConfirmReplaceMcpRange = async () => await _confirmDlg.ShowAsync(
            "更换 MCP 授权库将停止当前 MCP 服务并以新库重启。",
            "更换正在对外服务的库",
            "更换并重启服务",
            "保持原范围",
            danger: false,
            dialogWidth: 640,
            detail: "旧连接与已建立的会话将失效；不影响其他独立服务（如浏览分享）。");
        ViewModel.ConfirmEnableLan = async kb => await _confirmDlg.ShowAsync(
            $"授权库：{kb}\n监听范围将从仅本机回环改为局域网地址。仅限可信网络使用，客户端仍需令牌；不会连带开启写入。",
            "允许局域网访问此 MCP 服务？",
            "允许局域网访问");
        ViewModel.MarkVisited();
    }

    private void OnReplaceShareRange(object sender, RoutedEventArgs e) => ViewModel.ReplaceShareRangeCommand.Execute(null);
    private void OnGoOrganize(object sender, RoutedEventArgs e) => ViewModel.GoOrganizeCommand.Execute(null);
    private void OnAgentReadOnlyClick(object sender, RoutedEventArgs e) => ViewModel.SetAgentReadOnlyCommand.Execute(null);
    private async void OnAgentWriteClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.AgentAllowWrite) await ViewModel.RequestAgentWorkspaceWriteAsync();
    }
}
