using LLM_Wiki.ViewModels.Pages;

using Wpf.Ui.Abstractions.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class GuidePage : INavigableView<GuideViewModel>
    {
        public GuideViewModel ViewModel { get; }

        public GuidePage(GuideViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        public void OnNavigatedTo(string kb)
        {
            // 指南页不依赖知识库
        }
    }
}
