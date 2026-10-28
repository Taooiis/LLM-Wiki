using System.Windows;
using System.Windows.Input;

using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Dialogs
{
    public partial class EditorDialog : ContentDialog
    {
        public enum Mode { New, Exist }

        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register(nameof(DialogMode), typeof(Mode), typeof(EditorDialog),
                new PropertyMetadata(Mode.Exist, OnModeChanged));

        public static readonly DependencyProperty FileNameProperty =
            DependencyProperty.Register(nameof(FileName), typeof(string), typeof(EditorDialog),
                new PropertyMetadata(""));

        public static readonly DependencyProperty EditorContentProperty =
            DependencyProperty.Register(nameof(EditorContent), typeof(string), typeof(EditorDialog),
                new PropertyMetadata("", OnEditorContentChanged));

        public static readonly DependencyProperty FilePathProperty =
            DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(EditorDialog),
                new PropertyMetadata(""));

        public event Action<string>? Saved;

        public Mode DialogMode
        {
            get => (Mode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        public string FileName
        {
            get => (string)GetValue(FileNameProperty);
            set => SetValue(FileNameProperty, value);
        }

        public string EditorContent
        {
            get => (string)GetValue(EditorContentProperty);
            set => SetValue(EditorContentProperty, value);
        }

        public string FilePath
        {
            get => (string)GetValue(FilePathProperty);
            set => SetValue(FilePathProperty, value);
        }

        public EditorDialog()
        {
            InitializeComponent();
            DataContext = this;
        }

        private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EditorDialog dlg)
                dlg.ApplyMode();
        }

        private static void OnEditorContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EditorDialog dlg)
                dlg.EditorBox.Text = dlg.EditorContent;
        }

        private void ApplyMode()
        {
            FileNameInput.Visibility = DialogMode == Mode.New ? Visibility.Visible : Visibility.Collapsed;
            Title = DialogMode == Mode.New ? "新建文档" : "编辑";
            PrimaryButtonText = DialogMode == Mode.New ? "创建" : "保存";
            SecondaryButtonText = "关闭";
        }

        protected override void OnButtonClick(ContentDialogButton button)
        {
            if (button == ContentDialogButton.Primary)
            {
                if (DialogMode == Mode.New)
                {
                    if (string.IsNullOrWhiteSpace(FileNameInput.Text))
                    {
                        MsgText.Text = "文件名不能为空";
                        MsgText.Foreground = System.Windows.Media.Brushes.Red;
                        return;
                    }
                    var fn = FileNameInput.Text.Trim();
                    if (!fn.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase) &&
                        !fn.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase))
                    {
                        fn += ".md";
                    }
                    FilePath = "raw/sources/" + fn;
                }

                Saved?.Invoke(EditorContent);
                Hide(ContentDialogResult.Primary);
            }
            else if (button == ContentDialogButton.Secondary || button == ContentDialogButton.Close)
            {
                if (DirtyText.Visibility == Visibility.Visible &&
                    System.Windows.MessageBox.Show("有未保存的修改，确定关闭？", "关闭编辑器", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
                {
                    return;
                }
                Hide(ContentDialogResult.Secondary);
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                OnButtonClick(ContentDialogButton.Primary);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                OnButtonClick(ContentDialogButton.Secondary);
            }
        }

        private void OnEditorTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            EditorContent = EditorBox.Text;
            DirtyText.Visibility = Visibility.Visible;
            MsgText.Text = "";
        }
    }
}
