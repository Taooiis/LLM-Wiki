using LLM_Wiki.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class SharePage : INavigableView<ShareViewModel>
    {
        public ShareViewModel ViewModel { get; }

        public SharePage(ShareViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        public void OnNavigatedTo(string kb)
        {
            // ShareViewModel 不依赖当前 KB，无需处理
        }
    }
}
