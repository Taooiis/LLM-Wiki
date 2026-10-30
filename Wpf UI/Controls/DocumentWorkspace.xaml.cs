using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using LLM_Wiki.ViewModels;

namespace LLM_Wiki.Controls
{
    /// <summary>文档工作区：预览/编辑/只读/空四态（指南 §6）。绑定 DocumentWorkspaceViewModel。</summary>
    public partial class DocumentWorkspace : UserControl
    {
        public DocumentWorkspace()
        {
            InitializeComponent();
        }

        /// <summary>空态快捷操作区（宿主页注入，如"上传文件/新建文档"，设计 02§4.1）。</summary>
        public object EmptyAction
        {
            get => GetValue(EmptyActionProperty);
            set => SetValue(EmptyActionProperty, value);
        }

        public static readonly DependencyProperty EmptyActionProperty = DependencyProperty.Register(
            nameof(EmptyAction), typeof(object), typeof(DocumentWorkspace), new PropertyMetadata(null));

        /// <summary>编辑区内 Ctrl+S → 保存（指南 §13）。</summary>
        private void EditorBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                if (DataContext is DocumentWorkspaceViewModel vm)
                {
                    _ = vm.SaveAsync();
                }
            }
        }
    }
}
