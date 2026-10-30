using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class TreeBrowserPage : INavigableView<TreeBrowserViewModel>
    {
        public TreeBrowserViewModel ViewModel { get; }

        public TreeBrowserPage(TreeBrowserViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeItemViewModel item)
            {
                return;
            }

            if (item.Type == "file")
            {
                ViewModel.SelectFile(item.RelPath);
            }
        }

    }
}
