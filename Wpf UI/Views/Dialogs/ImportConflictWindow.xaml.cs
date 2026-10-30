using System.Windows;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Pages;

namespace LLM_Wiki.Views.Dialogs;

/// <summary>
/// D03 多选上传遇重名：只列真正冲突的项，显示现有/本次大小与修改时间；
/// 初焦“跳过同名继续”，覆盖用危险样式（02§9.1）。
/// </summary>
public partial class ImportConflictWindow : Window
{
    public ImportConflictWindow(IReadOnlyList<ImportConflictItem> conflicts)
    {
        InitializeComponent();
        ConflictList.ItemsSource = conflicts;
        var title = $"发现 {conflicts.Count} 个同名文件";
        Title = title;
        TitleText.Text = title;
    }

    public SourceConflictPolicy Choice { get; private set; } = SourceConflictPolicy.Ask;

    private void OnWindowLoaded(object sender, RoutedEventArgs e) => SkipButton.Focus();

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Choice = SourceConflictPolicy.Skip;
        DialogResult = true;
    }

    private void OnOverwrite(object sender, RoutedEventArgs e)
    {
        Choice = SourceConflictPolicy.Overwrite;
        DialogResult = true;
    }
}
