using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class DashboardPage : INavigableView<DashboardViewModel>
    {
        public DashboardViewModel ViewModel { get; }

        public DashboardPage(DashboardViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            ViewModel.PropertyChanged += ViewModel_PropertyChanged;

            InitializeComponent();
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var kb = KbList.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(kb))
            {
                await ShowConfirmationAndDelete(null);
                return;
            }

            var confirm = (ConfirmationDialog)Resources["ConfirmDlg"];
            if (await confirm.ShowAsync($"确定要删除知识库 \"{kb}\" 吗？\n\n此操作不可恢复，包括其中的所有文件。", "确认删除"))
            {
                ViewModel.DeleteKbCommand.Execute(kb);
            }
        }

        private async Task ShowConfirmationAndDelete(string? kb)
        {
            var confirm = (ConfirmationDialog)Resources["ConfirmDlg"];
            if (await confirm.ShowAsync("请先选择要删除的知识库", "提示"))
            {
                // 已选择
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // 同步 SelectedKb 模糊搜索
            if (e.PropertyName == nameof(DashboardViewModel.SelectedKb))
            {
                if (!string.IsNullOrWhiteSpace(ViewModel.SelectedKb))
                {
                    ViewModel.LoadFiles(ViewModel.SelectedKb);
                }
            }
        }

        private void KbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not string kb)
            {
                return;
            }

            ViewModel.SelectedKb = kb;
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not string file)
            {
                return;
            }

            ViewModel.LoadFileContent(ViewModel.SelectedKb, file);
        }
    }
}