using System.IO;
using System.Threading.Tasks;
using System.Windows;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Pages
{
    public partial class ConfigPage : INavigableView<ConfigViewModel>
    {
        public ConfigViewModel ViewModel { get; }

        public ConfigPage(ConfigViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        private async void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            // 计算初始目录
            string initialDir = string.IsNullOrWhiteSpace(ViewModel.WikisDirInput)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LLM Wiki", "wikis")
                : ViewModel.WikisDirInput;

            if (!Directory.Exists(initialDir))
            {
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            // 使用 FolderPickerDialog
            var dialog = new FolderPickerDialog
            {
                InitialDirectory = initialDir
            };

            // 显示对话框
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                ViewModel.WikisDirInput = dialog.SelectedPath;
                ViewModel.ValidateWikisDir();
            }
        }

        /// <summary>手输路径时即时验证，保存按钮状态跟随输入。</summary>
        private void OnWikisDirInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ViewModel.ValidateWikisDir();
        }
    }
}