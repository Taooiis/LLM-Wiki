using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.ViewModels.Windows;

namespace LLM_Wiki.ViewModels.Pages;

/// <summary>
/// 知识页：中文分类目录 + 只读阅读 + 来源/链接跳转 + 高级手动修订。
/// 默认打开 index.md，缺失回退 overview.md；初始模板骨架视为尚未整理。
/// </summary>
public partial class KnowledgeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly KnowledgeService _knowledge;
    private readonly SourceService _sources;
    private readonly ScanService _scanner;
    private readonly ReviewService _reviews;
    private readonly UnsavedChangesCoordinator _unsaved;
    private readonly ToastService _toast;

    private readonly List<KnowledgeTreeNode> _roots = [];
    private bool _suppressFilter;

    public KnowledgeViewModel(
        MainWindowViewModel main,
        KnowledgeService knowledge,
        SourceService sources,
        ScanService scanner,
        ReviewService reviews,
        UnsavedChangesCoordinator unsaved,
        ToastService toast)
    {
        _main = main;
        _knowledge = knowledge;
        _sources = sources;
        _scanner = scanner;
        _reviews = reviews;
        _unsaved = unsaved;
        _toast = toast;

        Workspace = new DocumentWorkspaceViewModel(sources, knowledge, toast);
        _unsaved.Register("knowledge-workspace", Workspace);
        Workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.OpenRequested += OnOpenRequested;
        LoadKb(_main.CurrentKb);
    }

    public DocumentWorkspaceViewModel Workspace { get; }
    public ObservableCollection<KnowledgeTreeNode> TreeItems { get; } = [];
    public ObservableCollection<LinkChip> Links { get; } = [];
    public ObservableCollection<SourceChip> SourceRefs { get; } = [];

    [ObservableProperty] private string _selectedKb = "";
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty] private string _currentRelPath = "";
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyDescription = "";
    [ObservableProperty] private bool _showEmpty;
    [ObservableProperty] private bool _showSkeletonHint;
    [ObservableProperty] private string _pageTitle = "";
    [ObservableProperty] private string _loadError = "";
    [ObservableProperty] private int _pendingCount;

    public bool HasKb => _main.HasKb;
    public bool HasCurrentPage => !string.IsNullOrWhiteSpace(CurrentRelPath);
    public bool HasLinks => Links.Count > 0;
    public bool HasSourceRefs => SourceRefs.Count > 0;
    public string PendingText => PendingCount > 0 ? $"有 {PendingCount} 项资料变更尚未整理" : "暂无待整理变更";

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb)) LoadKb(_main.CurrentKb);
        if (e.PropertyName == nameof(MainWindowViewModel.PendingCount))
        {
            PendingCount = _main.PendingCount;
            OnPropertyChanged(nameof(PendingText));
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentWorkspaceViewModel.PreviewContent)) return;
        ParsePageReferences();
    }

    private void OnOpenRequested(FileSearchResult result)
    {
        if (result.Kind != "知识") return;
        _ = OpenPageAsync(result.RelPath);
    }

    public void LoadKb(string kb)
    {
        SelectedKb = kb;
        LoadError = "";
        ShowEmpty = false;
        ShowSkeletonHint = false;
        CurrentRelPath = "";
        PageTitle = "";
        Links.Clear();
        SourceRefs.Clear();
        TreeItems.Clear();
        _roots.Clear();
        Workspace.ResetToEmpty();
        OnPropertyChanged(nameof(HasKb));
        PendingCount = string.IsNullOrWhiteSpace(kb) ? 0 : _main.PendingCount;

        if (string.IsNullOrWhiteSpace(kb) || !_main.HasKb)
        {
            ShowEmpty = true;
            EmptyTitle = "创建你的第一个知识库";
            EmptyDescription = "把资料和整理结果放在同一个地方。创建后从资料页添加文件。";
            return;
        }

        var root = _main.CurrentKbRoot;
        if (!Directory.Exists(Path.Combine(root, "wiki")))
        {
            LoadError = "知识目录不存在或不可访问：" + Path.Combine(root, "wiki");
            return;
        }

        var state = _knowledge.GetState(kb);
        if (state.Kind != KnowledgeStateKind.Content)
        {
            ShowEmpty = true;
            EmptyTitle = state.HasSources ? "还没有整理出的知识" : "资料已准备好。整理成知识后，就能在这里阅读。";
            EmptyDescription = state.HasSources
                ? "到整理页复制整理指令，在外部 AI 中执行，再回来核对结果。"
                : "先到资料页添加文件，整理后知识会出现在这里。";
            if (state.Kind == KnowledgeStateKind.Skeleton) ShowSkeletonHint = true;
        }

        LoadTree(kb);
        var defaultPage = _knowledge.DefaultPage(kb);
        if (!ShowEmpty && !string.IsNullOrWhiteSpace(defaultPage))
        {
            _ = OpenPageAsync(defaultPage, keepError: true);
        }
        PendingCount = _main.PendingCount;
        OnPropertyChanged(nameof(PendingText));
    }

    private void LoadTree(string kb)
    {
        foreach (var node in _knowledge.Tree(kb))
        {
            _roots.Add(new KnowledgeTreeNode(node));
        }
        RebuildTree();
    }

    partial void OnSearchKeywordChanged(string value) => RebuildTree();

    partial void OnCurrentRelPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasCurrentPage));
        PageTitle = string.IsNullOrWhiteSpace(value) ? "" : Path.GetFileNameWithoutExtension(value);
    }

    private void RebuildTree()
    {
        if (_suppressFilter) return;
        _suppressFilter = true;
        try
        {
            TreeItems.Clear();
            foreach (var root in _roots)
            {
                root.ResetVisible();
                TreeItems.Add(root);
            }
            var keyword = (SearchKeyword ?? "").Trim();
            if (keyword.Length > 0) Filter(TreeItems, keyword);
        }
        finally
        {
            _suppressFilter = false;
        }
    }

    private static int Filter(ObservableCollection<KnowledgeTreeNode> nodes, string keyword)
    {
        var kept = 0;
        foreach (var node in nodes.ToList())
        {
            if (!node.IsFolder)
            {
                if (!node.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) nodes.Remove(node);
                else kept++;
                continue;
            }
            var self = node.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            var child = Filter(node.VisibleChildren, keyword);
            if (!self && child == 0)
            {
                nodes.Remove(node);
                continue;
            }
            node.IsExpanded = child > 0;
            kept++;
        }
        return kept;
    }

    public async Task OpenPageAsync(string rel, bool keepError = false)
    {
        if (string.IsNullOrWhiteSpace(SelectedKb) || string.IsNullOrWhiteSpace(rel)) return;
        if (!await _unsaved.CanLeaveAsync()) return;
        Workspace.OpenKnowledgePage(SelectedKb, rel);
        CurrentRelPath = ChangeService.Normalize(rel);
        PageTitle = Path.GetFileNameWithoutExtension(rel);
        if (!keepError) LoadError = "";
        ParsePageReferences();
        MarkTreeNodeSelected(CurrentRelPath);
        OnPropertyChanged(nameof(HasCurrentPage));
    }

    private void ParsePageReferences()
    {
        Links.Clear();
        SourceRefs.Clear();
        var content = Workspace.PreviewContent ?? "";
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(content, @"\[\[([^\]]+)\]\]"))
        {
            var name = match.Groups[1].Value.Trim();
            if (name.Length == 0 || Links.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var target = _knowledge.ResolveWikiLink(SelectedKb, CurrentRelPath, name);
            Links.Add(new LinkChip(name, target.Resolved, target.Candidates));
        }
        foreach (var name in _knowledge.SourceNamesIn(content))
        {
            var found = _sources.List(SelectedKb).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            SourceRefs.Add(new SourceChip(name, found?.RelPath ?? ""));
        }
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(HasSourceRefs));
    }

    [RelayCommand]
    public async Task OpenLinkAsync(LinkChip? link)
    {
        if (link == null) return;
        if (link.Candidates.Count > 1)
        {
            _toast.ShowWarning("同名目标有多个：" + string.Join("、", link.Candidates));
            return;
        }
        if (string.IsNullOrEmpty(link.Resolved))
        {
            _toast.ShowWarning("目标页面不存在：" + link.Name);
            return;
        }
        await OpenPageAsync(link.Resolved);
    }

    [RelayCommand]
    public void OpenSource(SourceChip? chip)
    {
        if (chip == null) return;
        if (string.IsNullOrWhiteSpace(chip.RelPath))
        {
            _toast.ShowWarning("来源已移除：" + chip.Name);
            return;
        }
        _main.OpenResult(new FileSearchResult(chip.Name, chip.RelPath, "资料", "资料"));
    }

    private void MarkTreeNodeSelected(string rel)
    {
        foreach (var node in Flatten(_roots))
        {
            if (node.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase)) node.IsSelected = true;
            else if (node.IsSelected) node.IsSelected = false;
        }
    }

    private static IEnumerable<KnowledgeTreeNode> Flatten(IEnumerable<KnowledgeTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    [RelayCommand]
    private void ClearManualProtection()
    {
        if (!HasCurrentPage || string.IsNullOrWhiteSpace(SelectedKb)) return;
        _reviews.ClearManualProtection(SelectedKb, CurrentRelPath);
        _toast.ShowSuccess("已解除当前文件的版本保护；后续覆盖仍需按版本核对。");
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (string.IsNullOrWhiteSpace(CurrentRelPath)) return;
        try
        {
            System.Windows.Clipboard.SetText(CurrentRelPath);
            _toast.ShowSuccess("已复制路径：" + CurrentRelPath);
        }
        catch { _toast.ShowError("复制剪贴板失败"); }
    }

    [RelayCommand]
    private void OpenKbFolder()
    {
        var root = _main.CurrentKbRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true }); }
        catch (Exception e) { _toast.ShowError("打开文件夹失败：" + e.Message); }
    }

    /// <summary>高级入口：手动修订知识。默认工具栏没有编辑按钮。</summary>
    [RelayCommand]
    private void BeginManualRevision()
    {
        if (!HasCurrentPage)
        {
            _toast.ShowWarning("请先选择一个知识页");
            return;
        }
        if (!Workspace.BeginKnowledgeRevision())
        {
            _toast.ShowWarning("此页面不允许手动修订");
            return;
        }
        _toast.ShowWarning("此内容通常由 AI 维护。手动修改不会更新源资料，后续整理应先核对人工内容。");
    }

    [RelayCommand]
    private void Refresh()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb) || !_main.HasKb) return;
        _scanner.Scan(SelectedKb, recordChanges: true);
        var current = CurrentRelPath;
        LoadKb(SelectedKb);
        if (!string.IsNullOrWhiteSpace(current)) _ = OpenPageAsync(current, keepError: true);
        _toast.ShowSuccess("已刷新知识目录");
    }

    public void MarkVisited() => _main.RememberPage("knowledge");

    [RelayCommand]
    private void GoSources() => _main.NavigateTo("sources");
    [RelayCommand]
    private void GoOrganize() => _main.NavigateTo("organize");
}

public partial class KnowledgeTreeNode : ObservableObject
{
    public KnowledgeTreeNode(KnowledgeNode node)
    {
        Name = node.Name;
        DisplayName = node.DisplayName;
        RelPath = node.RelPath;
        IsFolder = node.IsFolder;
        Children = [.. node.Children.Select(c => new KnowledgeTreeNode(c))];
        ResetVisible();
    }

    public string Name { get; }
    public string DisplayName { get; }
    public string RelPath { get; }
    public bool IsFolder { get; }
    public string IconKey => IsFolder ? "dir" : Name;
    public ObservableCollection<KnowledgeTreeNode> Children { get; }
    public ObservableCollection<KnowledgeTreeNode> VisibleChildren { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public void ResetVisible()
    {
        VisibleChildren.Clear();
        foreach (var child in Children) VisibleChildren.Add(child);
    }
}

public sealed record LinkChip(string Name, string Resolved, IReadOnlyList<string> Candidates);
public sealed record SourceChip(string Name, string RelPath);
