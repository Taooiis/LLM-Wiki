using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

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

            ViewModel.PropertyChanged += ViewModel_PropertyChanged;

            InitializeComponent();

            // 页面晚于 VM 构造：SelectedKb 的首次赋值已发生过，主动补载当前库的文件列表
            if (!string.IsNullOrWhiteSpace(ViewModel.SelectedKb))
            {
                ViewModel.LoadFiles(ViewModel.SelectedKb);
            }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedKb))
            {
                _snackbarService.Show("提示", "请先选中要删除的知识库", ControlAppearance.Info, null, TimeSpan.FromSeconds(2.6));
                return;
            }

            var confirm = (ConfirmationDialog)Resources["ConfirmDlg"];
            if (await confirm.ShowAsync($"确定要删除知识库 \"{ViewModel.SelectedKb}\" 吗？\n此操作不可恢复。", "确认删除"))
            {
                ViewModel.DeleteKbCommand.Execute(ViewModel.SelectedKb);
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DashboardViewModel.SelectedKb))
            {
                if (!string.IsNullOrWhiteSpace(ViewModel.SelectedKb))
                {
                    ViewModel.LoadFiles(ViewModel.SelectedKb);
                }
            }
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ListView list || list.SelectedItem is not string file)
            {
                return;
            }

            ViewModel.LoadFileContent(ViewModel.SelectedKb, file);
        }
    }
}