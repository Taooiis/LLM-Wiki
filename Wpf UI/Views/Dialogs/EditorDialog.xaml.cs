using System;
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
                new PropertyMetadata("", OnFilePathChanged));

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

        // 保存前的文档内容，用于 dirty 状态检查
        private string _savedContent = "";

        public EditorDialog()
        {
            InitializeComponent();
            DataContext = this;
            _savedContent = EditorContent;
        }

        /// <summary>
        /// 清除 dirty 状态，通常在文件写入成功后调用
        /// </summary>
        public void ClearDirty()
        {
            _savedContent = EditorContent;
            DirtyText.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 设置已加载的内容（用于初始化时同步 dirty 状态）
        /// </summary>
        public void SetLoadedContent(string content)
        {
            EditorContent = content;
            _savedContent = content;
            DirtyText.Visibility = Visibility.Collapsed;
        }

        private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // 弹窗顶部路径栏跟随 FilePath（Exist 模式显示完整相对路径）
            if (d is EditorDialog dlg)
            {
                dlg.FilePathText.Text = (string)e.NewValue;
            }
        }

        private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EditorDialog dlg)
                dlg.ApplyMode();
        }

        private static void OnEditorContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EditorDialog dlg)
            {
                // 避免 cursor 跳转 - 仅在 Text 不同时更新
                var newValue = (string)e.NewValue;
                if (dlg.EditorBox.Text != newValue)
                    dlg.EditorBox.Text = newValue;
            }
        }

        private void ApplyMode()
        {
            FileNameInput.Visibility = DialogMode == Mode.New ? Visibility.Visible : Visibility.Collapsed;
            Title = DialogMode == Mode.New ? "新建文档" : "编辑";
            PrimaryButtonText = DialogMode == Mode.New ? "创建" : "保存";
            SecondaryButtonText = "关闭";
        }

        private bool _closeArmed;

        protected override void OnButtonClick(ContentDialogButton button)
        {
            if (button == ContentDialogButton.Primary)
            {
                if (DialogMode == Mode.New)
                {
                    if (string.IsNullOrWhiteSpace(FileNameInput.Text))
                    {
                        MsgText.Text = "文件名不能为空";
                        MsgText.Foreground = ThemeBrush("DangerBrush");
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
                MsgText.Text = "";
                _closeArmed = false;
                Hide(ContentDialogResult.Primary);
            }
            else if (button == ContentDialogButton.Secondary || button == ContentDialogButton.Close)
            {
                // 未保存确认用同框子状态承接（00§9.8）：不弹独立 MessageBox，不再叠框。
                if (DirtyText.Visibility == Visibility.Visible && !_closeArmed)
                {
                    _closeArmed = true;
                    MsgText.Text = "有未保存的修改：再次点击“放弃更改并关闭”将丢弃本次未保存内容。";
                    MsgText.Foreground = ThemeBrush("WarningBrush");
                    SecondaryButtonText = "放弃更改并关闭";
                    return;
                }
                Hide(ContentDialogResult.Secondary);
            }
        }

        private static System.Windows.Media.Brush ThemeBrush(string key)
            => System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
               ?? System.Windows.Media.Brushes.Gray;

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
            // E6：dirty 标记 - 当内容与保存的内容不同时显示
            DirtyText.Visibility = EditorContent != _savedContent ? Visibility.Visible : Visibility.Collapsed;
            MsgText.Text = "";
        }
    }
}
