using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using LLM_Wiki.Services;

namespace LLM_Wiki.Views.Dialogs;

/// <summary>
/// D01 创建知识库（00§9.4）：失败留框保留输入；重名就地提示并提供“打开已有知识库”。
/// 创建由回调执行（ViewModel.CreateKbAsync），成功后由其切库并进入资料页。
/// </summary>
public partial class CreateKbDialog : Window
{
    private readonly HashSet<string> _existing;
    private readonly Func<string, string, string?, Task<bool>>? _createAsync;

    public CreateKbDialog(IEnumerable<string> existingNames, Func<string, string, string?, Task<bool>>? createAsync = null)
    {
        _existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        _createAsync = createAsync;
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string KbName { get; private set; } = "";
    public string Scene { get; private set; } = "通用";
    public string Purpose { get; private set; } = "";
    public bool OpenExisting { get; private set; }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Validate();
        CreateButton.IsEnabled = ErrorText.Text.Length == 0 && NameBox.Text.Trim().Length > 0;
        OpenExistingButton.Visibility = Visibility.Collapsed;
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
            ErrorText.Text = "这个知识库已存在。";
            OpenExistingButton.Visibility = Visibility.Visible;
            return;
        }
        ErrorText.Text = "";
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        Validate();
        if (NameBox.Text.Trim().Length == 0) return;
        if (_createAsync == null)
        {
            // 无回调（设计器/旧调用）：仅校验名称后关闭。
            if (ErrorText.Text.Length > 0) return;
            KbName = NameBox.Text.Trim();
            Scene = (SceneBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "通用";
            Purpose = PurposeBox.Text.Trim();
            DialogResult = true;
            return;
        }

        var name = NameBox.Text.Trim();
        if (_existing.Contains(name))
        {
            ErrorText.Text = "这个知识库已存在。";
            OpenExistingButton.Visibility = Visibility.Visible;
            return;
        }

        SetBusy(true);
        ErrorText.Text = "";
        var ok = await _createAsync(name, (SceneBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "通用", PurposeBox.Text.Trim());
        SetBusy(false);
        if (ok)
        {
            KbName = name;
            DialogResult = true; // 成功：CreateKbAsync 已选中新库并进入资料页。
            return;
        }

        // 失败留框（00§9.3）：保留输入；重名时给“打开已有知识库”。
        Validate();
        if (_existing.Contains(name))
        {
            ErrorText.Text = "这个知识库已存在。";
            OpenExistingButton.Visibility = Visibility.Visible;
        }
        else if (ErrorText.Text.Length == 0)
        {
            ErrorText.Text = "创建失败，请重试或修改名称。";
        }
    }

    private void OnOpenExisting(object sender, RoutedEventArgs e)
    {
        KbName = NameBox.Text.Trim();
        OpenExisting = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetBusy(bool busy)
    {
        CreateButton.IsEnabled = !busy;
        OpenExistingButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        CreateButton.Content = busy ? "创建中…" : "创建知识库";
    }
}
