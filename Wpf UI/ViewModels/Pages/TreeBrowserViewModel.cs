using System.Collections.ObjectModel;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages
{
    /// <summary>
    /// 目录浏览页（设计 03）：左侧目录树 + 右侧复用 DocumentWorkspace。
    /// 树支持搜索（保留父级路径）、展开/折叠全部、刷新恢复展开与选择；
    /// 切文件/切库前经未保存保护（IUnsavedChangesGuard 统一询问）。
    /// </summary>
    public partial class TreeBrowserViewModel : ObservableObject
    {
        private readonly FileBridge _fileBridge;
        private readonly ToastService _toastService;
        private readonly MainWindowViewModel _mainViewModel;

        /// <summary>树内展开状态（RelPath → 已展开），页面会话内保留，刷新后尽量恢复（03§3）。</summary>
        private readonly HashSet<string> _expandedPaths = new(StringComparer.Ordinal);

        /// <summary>当前选中文件（相对路径），刷新后用于恢复选择。</summary>
        private string _selectedPath = "";

        /// <summary>搜索/程序展开期间的抑制标志：强制展开不写入会话展开状态。</summary>
        private bool _suppressExpandSync;

        /// <summary>切库回滚引起的再入标志（同 SourceViewModel 模式）。</summary>
        private bool _kbSwitchLocked;

        public TreeBrowserViewModel(FileBridge fileBridge, ToastService toastService, MainWindowViewModel mainViewModel)
        {
            _fileBridge = fileBridge;
            _toastService = toastService;
            _mainViewModel = mainViewModel;

            // 右侧工作区与源文档页共用同一组件（03§4）；目录浏览不提供新建
            Workspace = new DocumentWorkspaceViewModel(fileBridge, toastService);

            _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;

            // 页面 VM 懒解析：CurrentKb 首值可能早于本构造，订阅后拉一次现值
            LoadTree(_mainViewModel.CurrentKb);
        }

        /// <summary>右侧文档工作区（预览/编辑/只读）。</summary>
        public DocumentWorkspaceViewModel Workspace { get; }

        [ObservableProperty]
        private string _selectedKb = "";

        /// <summary>搜索目录或文件名；过滤后保留父级路径（03§3）。</summary>
        [ObservableProperty]
        private string _searchKeyword = "";

        /// <summary>目录不可访问时的错误说明（03§6：不用空树冒充空知识库）。</summary>
        [ObservableProperty]
        private string _loadError = "";

        /// <summary>错误态切换（XAML DataTrigger 用 bool，避免非空判断）。</summary>
        public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

        partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasLoadError));

        /// <summary>树可见节点总数（目录项计数，03§2 布局）。</summary>
        [ObservableProperty]
        private int _visibleCount;

        /// <summary>树根级可见节点（过滤在节点 VisibleChildren 上做，避免破坏原始结构）。</summary>
        public ObservableCollection<TreeItemViewModel> TreeItems { get; } = new();

        /// <summary>原始根节点（搜索过滤的还原基准）。</summary>
        private readonly List<TreeItemViewModel> _roots = new();

        private async void MainViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainWindowViewModel.CurrentKb))
            {
                return;
            }

            // 回滚引起的再入：直接加载目标库
            if (_kbSwitchLocked)
            {
                LoadTree(_mainViewModel.CurrentKb);
                return;
            }

            // 切库前未保存保护（03§4.2 / 07§2）；继续编辑则回滚全局库选择
            if (!await Workspace.CanLeaveAsync())
            {
                _kbSwitchLocked = true;
                _mainViewModel.CurrentKb = SelectedKb;
                _kbSwitchLocked = false;
                return;
            }

            LoadTree(_mainViewModel.CurrentKb);
        }

        /// <summary>加载/刷新目录树：保留会话展开状态与当前选择（03§6）。</summary>
        [RelayCommand]
        public void LoadTree(string kb)
        {
            SelectedKb = kb;
            TreeItems.Clear();
            _roots.Clear();

            Workspace.ResetToEmpty();

            if (string.IsNullOrWhiteSpace(kb))
            {
                LoadError = "";
                VisibleCount = 0;
                return;
            }

            // 目录不可访问 → 错误态 + 重试按钮，不用空树冒充（03§6）
            var rootDir = _fileBridge.KbRoot(kb);
            if (!Directory.Exists(rootDir))
            {
                LoadError = $"知识库目录不存在或不可访问：{rootDir}";
                VisibleCount = 0;
                return;
            }
            LoadError = "";

            foreach (var item in _fileBridge.Tree(kb))
            {
                var node = new TreeItemViewModel(item);
                HookExpandSync(node);
                _roots.Add(node);
            }

            RebuildVisibleTree();
            RestoreSelection();
        }

        /// <summary>展开状态同步：用户展开/收起时记入会话状态（过滤期间抑制）。</summary>
        private void HookExpandSync(TreeItemViewModel node)
        {
            node.ExpandedChanged += (n, expanded) =>
            {
                if (_suppressExpandSync)
                {
                    return;
                }
                if (expanded) _expandedPaths.Add(n.RelPath);
                else _expandedPaths.Remove(n.RelPath);
            };
            foreach (var child in node.Children)
            {
                HookExpandSync(child);
            }
        }

        /// <summary>按搜索词重建可见树：重置为全树后移除无匹配子树；有匹配的目录强制展开（03§3）。
        /// 搜索只影响树，不动右侧工作区（避免覆盖未保存编辑）。</summary>
        partial void OnSearchKeywordChanged(string value)
        {
            if (_roots.Count == 0)
            {
                return;
            }
            RebuildVisibleTree();
        }

        private void RebuildVisibleTree()
        {
            _suppressExpandSync = true;
            try
            {
                TreeItems.Clear();
                foreach (var root in _roots)
                {
                    root.ResetVisible();
                    TreeItems.Add(root);
                }

                var kw = (SearchKeyword ?? "").Trim();
                if (kw.Length > 0)
                {
                    FilterChildren(TreeItems, kw);
                }
                else
                {
                    // 无搜索：按会话展开状态还原
                    ApplyExpandedPaths(_roots);
                }
            }
            finally
            {
                _suppressExpandSync = false;
            }

            VisibleCount = CountNodes(TreeItems);
        }

        /// <summary>子树过滤：名称不含关键词且无匹配子孙的节点隐藏；返回保留数（父级路径保留）。</summary>
        private int FilterChildren(ObservableCollection<TreeItemViewModel> nodes, string kw)
        {
            var kept = 0;
            foreach (var node in nodes.ToList())
            {
                if (!node.IsFolder)
                {
                    if (!Matches(node, kw))
                    {
                        nodes.Remove(node);
                    }
                    else
                    {
                        kept++;
                    }
                    continue;
                }

                var dirMatch = Matches(node, kw);
                var childKept = FilterChildren(node.VisibleChildren, kw);
                if (!dirMatch && childKept == 0)
                {
                    nodes.Remove(node);
                    continue;
                }
                // 有匹配子孙或自身匹配 → 展开以便直接看到匹配项
                if (childKept > 0)
                {
                    node.IsExpanded = true;
                }
                kept++;
            }
            return kept;
        }

        private static bool Matches(TreeItemViewModel node, string keyword) =>
            node.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        /// <summary>按会话展开状态设置节点展开（刷新/退出搜索后恢复）。</summary>
        private void ApplyExpandedPaths(IEnumerable<TreeItemViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (!node.IsFolder)
                {
                    continue;
                }
                node.IsExpanded = _expandedPaths.Contains(node.RelPath);
                ApplyExpandedPaths(node.VisibleChildren);
            }
        }

        private static int CountNodes(IEnumerable<TreeItemViewModel> nodes)
        {
            var count = 0;
            foreach (var node in nodes)
            {
                count += 1 + CountNodes(node.VisibleChildren);
            }
            return count;
        }

        /// <summary>刷新后恢复当前选择：目标文件仍存在则重载预览，否则清空工作区（03§6）。</summary>
        private void RestoreSelection()
        {
            if (string.IsNullOrWhiteSpace(_selectedPath) || string.IsNullOrWhiteSpace(SelectedKb))
            {
                return;
            }

            var node = FindNode(_roots, _selectedPath);
            if (node == null)
            {
                _selectedPath = "";
                Workspace.ResetToEmpty();
                return;
            }

            node.IsSelected = true;
            ExpandAncestorsOf(node);
            Workspace.ShowPreview(SelectedKb, node.RelPath);
        }

        /// <summary>恢复选择时按路径前缀展开父级链，保证节点可见。</summary>
        private void ExpandAncestorsOf(TreeItemViewModel node)
        {
            var rel = node.RelPath;
            var idx = 0;
            while ((idx = rel.IndexOf('/', idx + 1)) > 0)
            {
                var dir = FindNode(_roots, rel[..idx]);
                if (dir != null)
                {
                    dir.IsExpanded = true;
                }
            }
        }

        private static IEnumerable<string> VisibleDirs(IEnumerable<TreeItemViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsFolder)
                {
                    yield return node.RelPath;
                    foreach (var child in VisibleDirs(node.VisibleChildren))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static TreeItemViewModel? FindNode(IEnumerable<TreeItemViewModel> nodes, string relPath)
        {
            foreach (var node in nodes)
            {
                if (string.Equals(node.RelPath.Replace('\\', '/'), relPath.Replace('\\', '/'), StringComparison.Ordinal))
                {
                    return node;
                }
                var child = FindNode(node.Children, relPath);
                if (child != null)
                {
                    return child;
                }
            }
            return null;
        }

        /// <summary>点击文件节点：经未保存保护后加载到工作区；拒绝切换返回 false（页面回退选中）。</summary>
        public async Task<bool> SelectNodeAsync(TreeItemViewModel node)
        {
            if (node.IsFolder || string.IsNullOrWhiteSpace(SelectedKb))
            {
                return false;
            }

            if (!await Workspace.CanLeaveAsync())
            {
                return false; // 继续编辑：不切换，工作区保持原文件
            }

            _selectedPath = node.RelPath;
            Workspace.ShowPreview(SelectedKb, node.RelPath);
            return true;
        }

        /// <summary>展开全部（当前可见树，03§3）。</summary>
        [RelayCommand]
        private void ExpandAll()
        {
            _suppressExpandSync = true;
            try
            {
                foreach (var dir in VisibleDirs(TreeItems))
                {
                    var node = FindNode(TreeItems, dir);
                    if (node != null)
                    {
                        node.IsExpanded = true;
                    }
                }
            }
            finally
            {
                _suppressExpandSync = false;
            }
        }

        /// <summary>折叠全部（当前可见树，03§3）。</summary>
        [RelayCommand]
        private void CollapseAll()
        {
            _suppressExpandSync = true;
            try
            {
                foreach (var dir in VisibleDirs(TreeItems))
                {
                    var node = FindNode(TreeItems, dir);
                    if (node != null)
                    {
                        node.IsExpanded = false;
                    }
                }
            }
            finally
            {
                _suppressExpandSync = false;
            }
            _expandedPaths.Clear();
        }

        /// <summary>刷新目录：先经未保存保护，再重载（保留展开状态与选择，03§6）。</summary>
        [RelayCommand]
        private async Task RefreshTree()
        {
            if (!await Workspace.CanLeaveAsync())
            {
                return; // 有未保存更改且用户继续编辑：不刷新
            }
            LoadTree(SelectedKb);
        }
    }

    /// <summary>
    /// 目录树节点 VM：构造时缓存子节点；VisibleChildren 供过滤（父级路径保留）；
    /// IsExpanded/IsSelected 双向绑定 TreeViewItem，展开变化经 ExpandedChanged 通知宿主记录。
    /// </summary>
    public partial class TreeItemViewModel : ObservableObject
    {
        public string Name { get; }
        public string Type { get; }
        /// <summary>相对库根的路径（如 wiki/index.md）；树构建时由 FileBridge 算好。</summary>
        public string RelPath { get; }
        public bool IsFolder => Type == "dir";

        /// <summary>图标标识：目录传 "dir"，文件传文件名（扩展名定图标，02§3.2/03§3）。</summary>
        public string IconKey => IsFolder ? "dir" : Name;

        [ObservableProperty]
        private bool _isExpanded;

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>展开/收起变化通知（宿主记录会话展开状态）。</summary>
        public event Action<TreeItemViewModel, bool>? ExpandedChanged;

        public ObservableCollection<TreeItemViewModel> Children { get; } = new();

        /// <summary>当前可见子节点（过滤时收缩，父级路径保留，03§3）。</summary>
        public ObservableCollection<TreeItemViewModel> VisibleChildren { get; } = new();

        public TreeItemViewModel(TreeItem item)
        {
            Name = item.Name;
            Type = item.Type;
            RelPath = (item.RelPath ?? item.Name).Replace('\\', '/');

            if (item.Children != null)
            {
                foreach (var child in item.Children)
                {
                    Children.Add(new TreeItemViewModel(child));
                }
            }
            ResetVisible();
        }

        /// <summary>可见子节点还原为全部子节点（退出过滤/刷新基准）。</summary>
        public void ResetVisible()
        {
            VisibleChildren.Clear();
            foreach (var child in Children)
            {
                VisibleChildren.Add(child);
            }
        }

        partial void OnIsExpandedChanged(bool value) => ExpandedChanged?.Invoke(this, value);
    }
}
