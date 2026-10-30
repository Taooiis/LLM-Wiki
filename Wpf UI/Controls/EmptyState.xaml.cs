using System.Windows;
using System.Windows.Controls;

namespace LLM_Wiki.Controls
{
    /// <summary>统一空状态占位：图标 / 标题 / 说明 / 可选操作按钮区。</summary>
    public partial class EmptyState : UserControl
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
            nameof(Icon), typeof(string), typeof(EmptyState), new PropertyMetadata("📁"));

        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata("暂无内容"));

        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(EmptyState), new PropertyMetadata(""));

        public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
            nameof(ActionContent), typeof(object), typeof(EmptyState), new PropertyMetadata(null));

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public object ActionContent
        {
            get => GetValue(ActionContentProperty);
            set => SetValue(ActionContentProperty, value);
        }

        public EmptyState()
        {
            InitializeComponent();
        }
    }
}
