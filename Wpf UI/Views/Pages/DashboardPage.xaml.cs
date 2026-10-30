using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class DashboardPage : INavigableView<DashboardViewModel>
    {
        public DashboardViewModel ViewModel { get; }
        private readonly ISnackbarService _snackbarService;

        public DashboardPage(DashboardViewModel viewModel, ISnackbarService snackbarService)
        {
            ViewModel = viewModel;
            _snackbarService = snackbarService;
            DataContext = this;

            InitializeComponent();
        }

        /// <summary>知识库卡片删除按钮：确认后执行（设计文档 07§3：删除必须确认并明示不可恢复）。</summary>
        private async void OnDeleteKbClick(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.CommandParameter is not DashboardViewModel.KbItem item)
            {
                return;
            }

            var confirm = (ConfirmationDialog)Resources["ConfirmDlg"];
            if (await confirm.ShowAsync(
                    $"确定要删除知识库 \"{item.Name}\" 吗？\n该库全部文件将被删除，此操作不可恢复。",
                    "确认删除"))
            {
                ViewModel.DeleteKbCommand.Execute(item.Name);
            }
        }

        /// <summary>双击知识库卡片 → 进入源文档页（设计文档 01§4）。</summary>
        private void OnKbCardDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ViewModel.OpenSelectedKbCommand.Execute(null);
        }
    }
}
