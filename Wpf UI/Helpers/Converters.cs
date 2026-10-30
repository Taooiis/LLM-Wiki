using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LLM_Wiki.Helpers
{
    /// <summary>数值 > 0 → true。用于待处理数高亮等条件样式。</summary>
    public class GreaterThanZeroConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count > 0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>数值 > 0 → Visible，否则 Collapsed。</summary>
    public class GreaterThanZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>数值 == 0 → Visible，否则 Collapsed。用于空状态提示。</summary>
    public class CountToEmptyVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>bool 取反 → Visibility（false 时 Visible）。</summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>true → Visible，false → Collapsed。</summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>bool 取反：用于"IsBusy 时禁用"这类 IsEnabled 绑定。</summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && !b;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && !b;
    }

    /// <summary>待处理数 → 辅助文案：>0 提示重新 Ingest，=0 提示已同步（设计文档 01§3.3）。</summary>
    public class PendingHintConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count > 0 ? "需要重新 Ingest" : "全部已同步";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 文件/目录标识 → Fluent 图标（设计 02§3.2、03§3）：
    /// 传 "dir" 显示文件夹；传文件名按扩展名给文档图标（.md / .txt），其余回落通用文档。
    /// </summary>
    public class FileNodeIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s)
            {
                if (s == "dir") return Wpf.Ui.Controls.SymbolRegular.Folder24;
                if (s.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return Wpf.Ui.Controls.SymbolRegular.Document24;
                if (s.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return Wpf.Ui.Controls.SymbolRegular.DocumentText24;
            }
            return Wpf.Ui.Controls.SymbolRegular.Document24;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
