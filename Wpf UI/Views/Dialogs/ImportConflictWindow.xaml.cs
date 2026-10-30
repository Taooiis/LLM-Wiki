using System.Windows;

using LLM_Wiki.Services;

namespace LLM_Wiki.Views.Dialogs;

public partial class ImportConflictWindow : Window
{
    public ImportConflictWindow(IEnumerable<string> conflictNames)
    {
        InitializeComponent();
        foreach (var name in conflictNames) ConflictList.Items.Add(name);
    }

    public SourceConflictPolicy Choice { get; private set; } = SourceConflictPolicy.Ask;

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
