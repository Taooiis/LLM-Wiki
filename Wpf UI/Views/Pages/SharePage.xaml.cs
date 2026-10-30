using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class SharePage : INavigableView<ShareViewModel>
    {
        public ShareViewModel ViewModel { get; }

        private readonly ConfirmationDialog _mcpWriteDialog;

        public SharePage(ShareViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();

            _mcpWriteDialog = (ConfirmationDialog)Resources["McpWriteDlg"];
            ViewModel.ConfirmEnableMcpWrite = () => _mcpWriteDialog.ShowAsync(
                "Agent 将可以修改 wiki/ 下的文件，修改会立即落盘。确定开启写入档吗？",
                "允许 Agent 写入",
                "开启写入档");
        }
    }
}
