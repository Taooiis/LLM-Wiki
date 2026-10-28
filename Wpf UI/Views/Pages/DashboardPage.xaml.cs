using System.ComponentModel;

using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;

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