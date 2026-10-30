using System.Windows;

using Wpf.Ui.Appearance;

namespace LLM_Wiki.Services;

/// <summary>
/// 主题应用：切换 WPF-UI 基础主题，并替换本应用的深/浅 Token 字典。
/// 只切 WPF-UI 主题不够：背景、文字和状态色是自建 key，不换字典会出现深浅混搭。
/// </summary>
public static class AppThemeService
{
    private static readonly Uri DarkUri = new("pack://application:,,,/Resources/Theme.Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightUri = new("pack://application:,,,/Resources/Theme.Light.xaml", UriKind.Absolute);

    private static string _current = "Dark";

    /// <summary>应用主题（非 Light 一律按深色处理）。幂等，可重复调用。</summary>
    public static void Apply(string theme)
    {
        var isLight = theme == "Light";
        var key = isLight ? "Light" : "Dark";
        if (_current == key) return;

        ApplicationThemeManager.Apply(isLight ? ApplicationTheme.Light : ApplicationTheme.Dark);

        var app = Application.Current;
        if (app == null)
        {
            _current = key;
            return;
        }

        // 先移除已有主题字典，再追加目标字典；追加在末尾才能覆盖 WPF-UI 的同名 key
        var merged = app.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var source = merged[i].Source?.OriginalString;
            if (source != null
                && (source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                merged.RemoveAt(i);
            }
        }

        merged.Add(new ResourceDictionary { Source = isLight ? LightUri : DarkUri });
        _current = key;
    }
}
