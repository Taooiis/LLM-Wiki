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

    /// <summary>阅读请求序号：库切换/快速点击后按序号丢弃过期读取结果（01§4 SelectKnowledge）。</summary>
    private int _loadSequence;

    private int _lastPendingCount;

    public KnowledgeViewModel(
        MainWindowViewModel main,
        KnowledgeService knowledge,
        SourceService sources,
        ScanService scanner,
        ReviewService reviews,
        UnsavedChangesCoordinator unsaved,
        SourcePreviewService preview,
        ToastService toast)
    {
        _main = main;
        _knowledge = knowledge;
        _sources = sources;
        _scanner = scanner;
        _reviews = reviews;
        _unsaved = unsaved;
        _toast = toast;

        Workspace = new DocumentWorkspaceViewModel(sources, knowledge, preview, toast);
        _unsaved.Register("knowledge-workspace", Workspace);
        _unsaved.RegisterSaver("knowledge-workspace", Workspace.TrySaveAsync);
        _unsaved.RegisterDiscarder("knowledge-workspace", Workspace.DiscardDraft);
        Workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.OpenRequested += OnOpenRequested;
        LoadKb(_main.CurrentKb);
    }

    public DocumentWorkspaceViewModel Workspace { get; }
    public ObservableCollection<KnowledgeTreeNode> TreeItems { get; } = [];
    public ObservableCollection<LinkChip> Links { get; } = [];
    public ObservableCollection<SourceChip> SourceRefs { get; } = [];
    public ObservableCollection<HeadingChip> HeadingItems { get; } = [];

    /// <summary>D15：解除人工保护前由页面确认。</summary>
    public Func<string, Task<bool>>? ConfirmClearProtection { get; set; }

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
    [ObservableProperty] private bool _isLoading;

    /// <summary>知识空态按钮：库有资料才显示“去整理”，无资料只显示“去资料页”（01§5.1）。</summary>
    [ObservableProperty] private bool _emptyHasSources;

    /// <summary>规则版本提醒（01§8：标题下持久 InfoBar，确认或稍后处理）。</summary>
    [ObservableProperty] private bool _ruleNeedsReview;
    [ObservableProperty] private string _ruleMessage = "";

    /// <summary>待整理提醒条“稍后处理”：只折叠提醒，不清待整理变更。</summary>
    [ObservableProperty] private bool _pendingBarDismissed;

    public bool HasKb => _main.HasKb;
    public bool HasCurrentPage => !string.IsNullOrWhiteSpace(CurrentRelPath);
    public bool HasLinks => Links.Count > 0;
    public bool HasSourceRefs => SourceRefs.Count > 0;
    public bool HasHeadings => HeadingItems.Count > 0;
    public string HeaderTitle => string.IsNullOrWhiteSpace(PageTitle) ? "知识" : PageTitle;
    public bool ShowPendingBar => PendingCount > 0 && !PendingBarDismissed;
    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);
    public bool ShowRuleBar => RuleNeedsReview;
    public bool ShowEmptyGoSources => HasKb && !EmptyHasSources;
    public bool ShowEmptyGoOrganize => HasKb && EmptyHasSources;
    public bool IsManualEditing => Workspace.IsKnowledgeRevision;
    public bool HasManualProtection => HasCurrentPage && _reviews.IsManualProtected(SelectedKb, CurrentRelPath, Workspace.CurrentVersion);
    public string ReadOnlyBadge => IsManualEditing ? "手动修订中" : "只读";
    public string PendingText => PendingCount > 0 ? $"有 {PendingCount} 项资料变更尚未整理" : "暂无待整理变更";

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentKb)) LoadKb(_main.CurrentKb);
        if (e.PropertyName == nameof(MainWindowViewModel.PendingCount))
        {
            PendingCount = _main.PendingCount;
            // 新变更到达时恢复提醒；数量未变的普通刷新不打扰已“稍后处理”的折叠
            if (PendingCount > _lastPendingCount) PendingBarDismissed = false;
            _lastPendingCount = PendingCount;
            OnPropertyChanged(nameof(PendingText));
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentWorkspaceViewModel.PreviewContent))
        {
            ParsePageReferences();
            return;
        }
        if (e.PropertyName is nameof(DocumentWorkspaceViewModel.Mode) or nameof(DocumentWorkspaceViewModel.Role))
        {
            OnPropertyChanged(nameof(IsManualEditing));
            OnPropertyChanged(nameof(ReadOnlyBadge));
            OnPropertyChanged(nameof(HasManualProtection));
        }
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
        HeadingItems.Clear();
        TreeItems.Clear();
        _roots.Clear();
        _loadSequence++;
        IsLoading = false;
        EmptyHasSources = false;
        RuleNeedsReview = false;
        RuleMessage = "";
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

        // 规则更新提醒（01§8：标题下持久 InfoBar，确认或稍后处理，不自动认定规则已完成）
        try
        {
            var rule = _reviews.GetRuleStatus(kb);
            RuleNeedsReview = rule.NeedsReview;
            RuleMessage = rule.NeedsReview
                ? $"规则（purpose.md / schema.md）已更新到 {rule.CurrentVersion[..Math.Min(8, rule.CurrentVersion.Length)]}，请核对现有知识是否仍符合新规则。"
                : "";
        }
        catch
        {
            RuleNeedsReview = false;
            RuleMessage = "";
        }

        var state = _knowledge.GetState(kb);
        EmptyHasSources = state.HasSources;
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
        // 01§4 Load/RefreshTree：保留展开——重建前收集 IsExpanded 节点的 RelPath，重建后回填
        var expanded = new HashSet<string>(
            Flatten(_roots).Where(n => n.IsExpanded).Select(n => ChangeService.Normalize(n.RelPath)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var node in _knowledge.Tree(kb).OrderBy(n => n.RelPath.Equals("wiki/index.md", StringComparison.OrdinalIgnoreCase) ? 0 : n.IsFolder ? 1 : 2).ThenBy(n => n.IsFolder ? n.Name switch { "concepts" => 0, "entities" => 1, "sources" => 2, "queries" => 3, "synthesis" => 4, "comparisons" => 5, _ => 6 } : 0))
        {
            _roots.Add(new KnowledgeTreeNode(node));
        }
        if (expanded.Count > 0)
        {
            foreach (var node in Flatten(_roots))
            {
                if (expanded.Contains(ChangeService.Normalize(node.RelPath))) node.IsExpanded = true;
            }
        }
        RebuildTree();
    }

    partial void OnSearchKeywordChanged(string value) => RebuildTree();

    partial void OnCurrentRelPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasCurrentPage));
        OnPropertyChanged(nameof(HasManualProtection));
    }

    partial void OnPageTitleChanged(string value) => OnPropertyChanged(nameof(HeaderTitle));
    partial void OnPendingCountChanged(int value) => OnPropertyChanged(nameof(ShowPendingBar));
    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasLoadError));
    partial void OnRuleNeedsReviewChanged(bool value) => OnPropertyChanged(nameof(ShowRuleBar));
    partial void OnPendingBarDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowPendingBar));

    partial void OnEmptyHasSourcesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyGoSources));
        OnPropertyChanged(nameof(ShowEmptyGoOrganize));
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
        var kb = SelectedKb;
        var seq = ++_loadSequence;
        IsLoading = true;
        if (!keepError) LoadError = "";
        // 01§4 SelectKnowledge：异步读明确路径；库切换/快速点击后按请求序号丢弃过期响应
        var read = await Task.Run(() =>
        {
            var ok = _knowledge.TryRead(kb, rel, out var content, out var hash, out var error);
            return (ok, content, hash, error);
        });
        if (seq != _loadSequence || kb != SelectedKb) return;
        IsLoading = false;
        if (read.ok)
            Workspace.OpenKnowledgePage(kb, rel);
        else
            Workspace.OpenReadOnlyInfo(kb, rel, read.error);
        CurrentRelPath = ChangeService.Normalize(rel);
        ParsePageReferences();
        MarkTreeNodeSelected(CurrentRelPath);
        OnPropertyChanged(nameof(HasCurrentPage));
        OnPropertyChanged(nameof(HasManualProtection));
    }

    private static readonly System.Text.RegularExpressions.Regex H1Regex =
        new(@"^#\s+(.+?)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string CleanHeading(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"[*`\[\]]", "").Trim();

    private void ParsePageReferences()
    {
        Links.Clear();
        SourceRefs.Clear();
        HeadingItems.Clear();
        var content = Workspace.PreviewContent ?? "";

        var h1 = H1Regex.Match(content);
        var hasH1 = h1.Success && CleanHeading(h1.Groups[1].Value).Length > 0;
        PageTitle = hasH1 ? CleanHeading(h1.Groups[1].Value) : Path.GetFileNameWithoutExtension(CurrentRelPath);
        Workspace.SuppressFirstH1 = hasH1;

        var index = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     content, @"^(#{1,4})\s+(.+?)\s*$",
                     System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            var text = CleanHeading(match.Groups[2].Value);
            if (text.Length == 0) continue;
            var level = match.Groups[1].Value.Length;
            HeadingItems.Add(new HeadingChip(text, level, index++));
        }

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
        OnPropertyChanged(nameof(HasHeadings));
        OnPropertyChanged(nameof(HasManualProtection));
    }

    [RelayCommand]
    public async Task OpenLinkAsync(LinkChip? link)
    {
        if (link == null || link.IsMissing) return; // 目标缺失已就地置灰标注，不再弹全局 toast（01§8）
        if (link.HasCandidates && string.IsNullOrWhiteSpace(link.Resolved))
        {
            _toast.ShowWarning("同名目标有多个：" + string.Join("、", link.Candidates));
            return;
        }
        await OpenPageAsync(link.Resolved);
    }

    [RelayCommand]
    public void OpenSource(SourceChip? chip)
    {
        if (chip == null) return;
        if (string.IsNullOrWhiteSpace(chip.RelPath)) return; // 缺失来源 chip 已就地置灰禁用，不再弹全局 toast（01§8）
        _main.OpenResult(new FileSearchResult(chip.Name, chip.RelPath, "资料", "资料"));
    }

    private void MarkTreeNodeSelected(string rel)
    {
        var target = ChangeService.Normalize(rel);
        foreach (var node in Flatten(_roots))
        {
            if (ChangeService.Normalize(node.RelPath).Equals(target, StringComparison.OrdinalIgnoreCase)) node.IsSelected = true;
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
    private async Task ClearManualProtectionAsync()
    {
        if (!HasCurrentPage || string.IsNullOrWhiteSpace(SelectedKb)) return;
        var version = Workspace.CurrentVersion;
        if (ConfirmClearProtection != null && !await ConfirmClearProtection(CurrentRelPath)) return;
        if (!_reviews.IsManualProtected(SelectedKb, CurrentRelPath, version))
        {
            _toast.ShowWarning("文件已有新变化，请重新核对后再解除保护。");
            OnPropertyChanged(nameof(HasManualProtection));
            return;
        }
        _reviews.ClearManualProtection(SelectedKb, CurrentRelPath);
        _toast.ShowSuccess("已解除当前版本保护；后续 AI 写入仍需满足路径与版本校验。");
        OnPropertyChanged(nameof(HasManualProtection));
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
        OnPropertyChanged(nameof(IsManualEditing));
        OnPropertyChanged(nameof(ReadOnlyBadge));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb) || !_main.HasKb) return;
        // 01§4 Load/RefreshTree 可执行条件：草稿保护已通过——先过 D02 保护再扫描刷新，保护手动修订草稿
        if (!await _unsaved.CanLeaveAsync()) return;
        _scanner.Scan(SelectedKb, recordChanges: true);
        var current = CurrentRelPath;
        LoadKb(SelectedKb);
        if (!string.IsNullOrWhiteSpace(current)) _ = OpenPageAsync(current, keepError: true);
    }

    /// <summary>加载失败 InfoBar 的“重试”：重新执行加载流程（01§5.2 读取失败展示错误+重试）。</summary>
    [RelayCommand]
    private async Task RetryLoadAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb) || !_main.HasKb) return;
        if (!await _unsaved.CanLeaveAsync()) return;
        LoadKb(SelectedKb);
    }

    /// <summary>树过滤无结果空态：清空筛选关键字（01§5 无结果过滤显示清空入口）。</summary>
    [RelayCommand]
    private void ClearTreeFilter() => SearchKeyword = "";

    /// <summary>规则更新 InfoBar：确认已按新规则核对现有知识（推进已确认版本）。</summary>
    [RelayCommand]
    private void AcknowledgeRules()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        _reviews.AcknowledgeRules(SelectedKb);
        RuleNeedsReview = false;
        RuleMessage = "";
        _toast.ShowSuccess("已确认按新规则核对现有知识。");
    }

    /// <summary>规则更新 InfoBar：稍后处理（只忽略当前规则版本，不推进确认，也不改知识）。</summary>
    [RelayCommand]
    private void IgnoreRules()
    {
        if (string.IsNullOrWhiteSpace(SelectedKb)) return;
        _reviews.IgnoreCurrentRules(SelectedKb);
        RuleNeedsReview = false;
        RuleMessage = "";
    }

    /// <summary>待整理提醒条“稍后处理”：只折叠提醒，不清除待整理变更（01§8）。</summary>
    [RelayCommand]
    private void DismissPendingBar() => PendingBarDismissed = true;

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
        DisplayName = node.RelPath.Equals("wiki/index.md", StringComparison.OrdinalIgnoreCase) ? "知识首页" : node.DisplayName;
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

    /// <summary>index.md 树图标用 Home（01§2）；转换器在禁止修改文件内，故在页面模板按此标记处理。</summary>
    public bool IsIndexPage => !IsFolder && RelPath.Equals("wiki/index.md", StringComparison.OrdinalIgnoreCase);
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

public sealed record LinkChip(string Name, string Resolved, IReadOnlyList<string> Candidates)
{
    /// <summary>同名歧义走候选菜单；未解析且无候选即目标不存在——就地置灰标注，不再弹全局 toast（01§8）。</summary>
    public bool HasCandidates => Candidates.Count > 0;

    public bool IsMissing => string.IsNullOrWhiteSpace(Resolved) && !HasCandidates;

    public string DisplayName => IsMissing ? $"{Name}（目标不存在）" : $"↗ {Name}";

    public string ChipToolTip => HasCandidates
        ? $"同名候选 {Candidates.Count} 个；点击查看"
        : IsMissing ? "目标不存在：知识页尚未整理或已被移动" : $"打开 {Name}";
}

public sealed record SourceChip(string Name, string RelPath)
{
    public bool IsMissing => string.IsNullOrWhiteSpace(RelPath);

    public string DisplayName => IsMissing ? $"{Name}（目标不存在）" : $"来源：{Name}";

    public string ChipToolTip => IsMissing ? "来源已移除：无法定位此来源文件" : "打开来源文件";
}
public sealed record HeadingChip(string Text, int Level, int Index)
{
    public string Indent => new string(' ', Math.Max(0, Level - 1) * 2);
}
