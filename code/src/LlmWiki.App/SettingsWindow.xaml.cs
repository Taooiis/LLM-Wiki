using System.Windows;
using System.Windows.Controls;

namespace LlmWiki.App;

/// <summary>
/// 设置窗口：知识库 / 文件夹 / 分享 / 外观 设置
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择默认文件夹",
            InitialDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LlmWiki", "wikis")
        };
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // 保存设置到配置文件
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}