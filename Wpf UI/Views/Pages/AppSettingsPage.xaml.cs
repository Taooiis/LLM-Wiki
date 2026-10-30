using System.Windows;

using LLM_Wiki.ViewModels.Pages;
using LLM_Wiki.Views.Dialogs;

namespace LLM_Wiki.Views.Pages;

public partial class AppSettingsPage
{
    public AppSettingsViewModel ViewModel { get; }
    private readonly ConfirmationDialog _confirmDlg;

    public AppSettingsPage(AppSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        _confirmDlg = (ConfirmationDialog)Resources["ConfirmDlg"];
        // D11（06§8）：640 宽，按钮 保留当前路径 / 应用新路径，初焦在 保留当前路径。
        ViewModel.ConfirmApplyPath = async message => await _confirmDlg.ShowAsync(
            message,
            "应用新的知识库位置？",
            "应用新路径",
            "保留当前路径",
            danger: false,
            dialogWidth: 640);
        ViewModel.MarkVisited();
    }

    private void OnDarkThemeClick(object sender, RoutedEventArgs e) => ViewModel.SetThemeCommand.Execute("Dark");
    private void OnLightThemeClick(object sender, RoutedEventArgs e) => ViewModel.SetThemeCommand.Execute("Light");
}
