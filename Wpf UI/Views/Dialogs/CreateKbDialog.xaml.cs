using System.Windows;
using System.Windows.Controls;

using LLM_Wiki.Services;

namespace LLM_Wiki.Views.Dialogs;

public partial class CreateKbDialog : Window
{
    private readonly HashSet<string> _existing;

    public CreateKbDialog(IEnumerable<string> existingNames)
    {
        _existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string KbName { get; private set; } = "";
    public string Scene { get; private set; } = "通用";
    public string Purpose { get; private set; } = "";

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Validate();
        CreateButton.IsEnabled = ErrorText.Text.Length == 0 && NameBox.Text.Trim().Length > 0;
    }

    private void Validate()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "";
            return;
        }
        if (!PathGuard.ValidKb(name))
        {
            ErrorText.Text = "名称只允许中英文、数字、下划线、中划线";
            return;
        }
        if (_existing.Contains(name))
        {
            ErrorText.Text = "这个知识库已存在，可从左上角切换器打开已有知识库。";
            return;
        }
        ErrorText.Text = "";
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        Validate();
        if (ErrorText.Text.Length > 0) return;
        KbName = NameBox.Text.Trim();
        Scene = (SceneBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "通用";
        Purpose = PurposeBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
