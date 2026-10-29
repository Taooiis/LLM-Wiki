using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Wpf.Ui.Controls;

namespace LLM_Wiki.Views.Dialogs
{
    /// <summary>
    /// 自定义文件夹选择对话框。
    /// 功能：常用位置直达 / 面包屑 / 地址输入 / 磁盘列表 / 文件夹浏览。
    /// </summary>
    public partial class FolderPickerDialog : ContentDialog
    {
        /// <summary>
        /// 选定的完整路径。
        /// </summary>
        public string SelectedPath
        {
            get => (string)GetValue(SelectedPathProperty);
            set => SetValue(SelectedPathProperty, value);
        }

        public static readonly DependencyProperty SelectedPathProperty =
            DependencyProperty.Register(nameof(SelectedPath), typeof(string), typeof(FolderPickerDialog), new PropertyMetadata(""));

        /// <summary>
        /// 当前浏览目录。
        /// </summary>
        public string CurrentPath
        {
            get => (string)GetValue(CurrentPathProperty);
            set => SetValue(CurrentPathProperty, value);
        }

        public static readonly DependencyProperty CurrentPathProperty =
            DependencyProperty.Register(nameof(CurrentPath), typeof(string), typeof(FolderPickerDialog),
                new PropertyMetadata("", OnCurrentPathChanged));

        /// <summary>
        /// 地址栏文本。
        /// </summary>
        public string Address
        {
            get => (string)GetValue(AddressProperty);
            set => SetValue(AddressProperty, value);
        }

        public static readonly DependencyProperty AddressProperty =
            DependencyProperty.Register(nameof(Address), typeof(string), typeof(FolderPickerDialog),
                new PropertyMetadata(""));

        /// <summary>
        /// 磁盘列表。
        /// </summary>
        public ObservableCollection<DriveItem> Drives { get; } = new();

        /// <summary>
        /// 文件夹列表。
        /// </summary>
        public ObservableCollection<FolderItem> Folders { get; } = new();

        /// <summary>
        /// 初始目录。
        /// </summary>
        public string InitialDirectory
        {
            get => (string)GetValue(InitialDirectoryProperty);
            set => SetValue(InitialDirectoryProperty, value);
        }

        public static readonly DependencyProperty InitialDirectoryProperty =
            DependencyProperty.Register(nameof(InitialDirectory), typeof(string), typeof(FolderPickerDialog),
                new PropertyMetadata("", OnInitialDirectoryChanged));

        private bool _loaded;

        public FolderPickerDialog()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += OnLoaded;
        }

        private static void OnInitialDirectoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FolderPickerDialog dlg)
            {
                if (!string.IsNullOrEmpty((string)e.NewValue))
                {
                    dlg.NavigateTo((string)e.NewValue);
                }
            }
        }

        private static void OnCurrentPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FolderPickerDialog dlg)
            {
                dlg.Address = (string)e.NewValue;
                if (dlg._loaded)
                {
                    dlg.LoadFolders();
                }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;

            // 如果设置了初始目录，导航到它
            if (!string.IsNullOrWhiteSpace(InitialDirectory) && string.IsNullOrWhiteSpace(CurrentPath))
            {
                NavigateTo(InitialDirectory);
            }
            // 如果没有设置当前路径，则使用 "我的文档"
            else if (string.IsNullOrWhiteSpace(CurrentPath))
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(docs))
                {
                    NavigateTo(docs);
                }
            }

            LoadDrives();
        }

        /// <summary>
        /// 加载所有磁盘。
        /// </summary>
        private void LoadDrives()
        {
            Drives.Clear();
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    Drives.Add(new DriveItem(drive.Name, drive.VolumeLabel, drive.TotalSize));
                }
            }
            catch
            {
                // 忽略加载磁盘列表时的错误
            }
        }

        /// <summary>
        /// 导航到指定目录。
        /// </summary>
        public void NavigateTo(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                CurrentPath = Path.GetFullPath(path);
            }
            catch
            {
                // 路径非法时静默处理
            }
        }

        /// <summary>
        /// 加载当前目录下的所有子文件夹。
        /// 支持本地路径 + UNC 网络路径。
        /// </summary>
        private void LoadFolders()
        {
            Folders.Clear();

            try
            {
                // UNC 路径特殊处理：\\ComputerName 或 \\ComputerName\Share
                if (CurrentPath.StartsWith(@"\\"))
                {
                    var parts = CurrentPath.TrimEnd('\\').Split('\\');
                    // \\ComputerName —— 显示计算机的共享列表
                    if (parts.Length == 2)
                    {
                        var computer = parts[1];
                        try
                        {
                            foreach (var share in new DirectoryInfo(CurrentPath).GetDirectories())
                            {
                                Folders.Add(new FolderItem(share.Name, share.FullName, isParent: false));
                            }
                        }
                        catch
                        {
                            // 无权限访问共享列表，显示空
                        }
                        return;
                    }
                    // \\ComputerName\Share —— 父级是 \\ComputerName
                    var parent = currentPathParent(CurrentPath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Folders.Add(new FolderItem("..", parent, isParent: true));
                    }
                    foreach (var dir in Directory.GetDirectories(CurrentPath))
                    {
                        var info = new DirectoryInfo(dir);
                        Folders.Add(new FolderItem(info.Name, info.FullName, isParent: false));
                    }
                    return;
                }

                // 本地路径普通处理
                if (!Directory.Exists(CurrentPath))
                {
                    return;
                }

                // 上一级
                var localParent = Directory.GetParent(CurrentPath);
                if (localParent != null)
                {
                    Folders.Add(new FolderItem("..", localParent.FullName, isParent: true));
                }

                // 子文件夹
                foreach (var dir in Directory.GetDirectories(CurrentPath))
                {
                    var info = new DirectoryInfo(dir);
                    Folders.Add(new FolderItem(info.Name, info.FullName, isParent: false));
                }
            }
            catch
            {
                // 忽略权限等错误
            }
        }

        /// <summary>
        /// UNC 路径的上一级目录。
        /// \\A\B\C → \\A\B
        /// \\A\B → \\A
        /// \\A → 根，无父级
        /// </summary>
        private static string? currentPathParent(string uncPath)
        {
            var parts = uncPath.TrimEnd('\\').Split('\\');
            if (parts.Length <= 2) return null; // \\ComputerName 没有上级
            return string.Join("\\", parts.Take(parts.Length - 1));
        }

        /// <summary>
        /// 定位到当前路径的父目录（支持本地 + UNC）。
        /// </summary>
        private void NavigateUp()
        {
            if (string.IsNullOrWhiteSpace(CurrentPath))
            {
                return;
            }

            try
            {
                string? parent;
                if (CurrentPath.StartsWith(@"\\"))
                {
                    // UNC 路径：\\Comp\Share\Sub → \\Comp\Share； \\Comp\Share → \\Comp； \\Comp → 根
                    parent = currentPathParent(CurrentPath);
                }
                else
                {
                    parent = Directory.GetParent(CurrentPath)?.FullName;
                }

                if (!string.IsNullOrEmpty(parent))
                {
                    NavigateTo(parent);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 处理常用位置按钮点击。
        /// </summary>
        private void OnCommonLocationClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement btn)
            {
                return;
            }

            string path = btn.Tag?.ToString() ?? "";
            switch (path)
            {
                case "Desktop":
                    path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    break;
                case "Documents":
                    path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    break;
                case "AppDir":
                    path = AppContext.BaseDirectory;
                    break;
                default:
                    path = btn.Tag?.ToString() ?? "";
                    break;
            }

            if (!string.IsNullOrEmpty(path))
            {
                NavigateTo(path);
            }
        }

        /// <summary>
        /// 处理地址栏"前往"按钮点击。
        /// </summary>
        private void OnNavigateToAddress(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(Address))
            {
                NavigateTo(Address);
            }
        }

        /// <summary>
        /// 处理网络按钮点击：枚举局域网计算机。
        /// </summary>
        private void OnNetworkClick(object sender, RoutedEventArgs e)
        {
            // 枚举网络计算机：Directory.GetDirectories("\\") 返回 \\ComputerName 列表
            Folders.Clear();

            try
            {
                var computers = Directory.GetDirectories(@"\\");
                foreach (var comp in computers)
                {
                    // GetDirectories 返回的格式类似 \\ComputerName\
                    var name = new DirectoryInfo(comp).Name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        Folders.Add(new FolderItem(name, comp.TrimEnd('\\'), isParent: false, isNetwork: true));
                    }
                }
            }
            catch (Exception ex)
            {
                // 网络浏览失败（可能未加入工作群域或无权限），显示空列表
                Folders.Add(new FolderItem($"网络浏览失败：{ex.Message}", "", isParent: false, isNetwork: false));
            }
        }

        /// <summary>
        /// 处理磁盘列表选择。
        /// </summary>
        private void Drive_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ListView list || list.SelectedItem is not DriveItem drive)
            {
                return;
            }

            NavigateTo(drive.Name);
            list.SelectedItem = null;
        }

        /// <summary>
        /// 处理文件夹列表选择。
        /// </summary>
        private void Folder_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ListView list || list.SelectedItem is not FolderItem folder)
            {
                return;
            }

            if (folder.IsParent)
            {
                NavigateUp();
            }
            else
            {
                NavigateTo(folder.Path);
            }

            list.SelectedItem = null;
        }

        /// <summary>
        /// 确定按钮：保存选定路径。
        /// </summary>
        protected override void OnButtonClick(ContentDialogButton button)
        {
            if (button == ContentDialogButton.Primary)
            {
                SelectedPath = CurrentPath;
                Hide(ContentDialogResult.Primary);
            }
            else
            {
                Hide(ContentDialogResult.Secondary);
            }
        }
    }

    /// <summary>
    /// 磁盘项。
    /// </summary>
    public class DriveItem
    {
        public DriveItem(string name, string label, long size)
        {
            Name = name;
            Label = label;
            Size = size;
        }

        public string Name { get; }
        public string Label { get; }
        public long Size { get; }

        public override string ToString() => $"{Name} ({Label})";
    }

    /// <summary>
    /// 文件夹项。
    /// </summary>
    public class FolderItem
    {
        public FolderItem(string name, string path, bool isParent = false, bool isNetwork = false)
        {
            Name = name;
            Path = path;
            IsParent = isParent;
            IsNetwork = isNetwork;
        }

        public string Name { get; }
        public string Path { get; }
        public bool IsParent { get; }
        public bool IsNetwork { get; }

        public override string ToString() => IsParent ? ".." : Name;
    }
}
