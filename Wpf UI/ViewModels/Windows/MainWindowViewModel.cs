using System.Collections.ObjectModel;
using System.IO;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using LLM_Wiki.Services;
using LLM_Wiki.Share;
using LLM_Wiki.Views.Pages;

using Wpf.Ui;
using Wpf.Ui.Controls;

namespace LLM_Wiki.ViewModels.Windows;

/// <summary>
/// 主窗口：当前知识库上下文、三主导航、库切换、全局文件名查找与状态栏。
/// 所有状态来自真实服务；切库前统一处理未保存草稿。
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly KbService _kbService;
    private readonly ChangeService _changes;
    private readonly ScanService _scanner;
    private readonly SourceService _sources;
    private readonly KnowledgeService _knowledge;
    private readonly UnsavedChangesCoordinator _unsaved;
    private readonly ShareService _share;
    private readonly McpService _mcp;
    private readonly LocalAgentService _agent;
    private readonly ToastService _toast;
    private readonly INavigationService _navigation;

    public MainWindowViewModel(
        ConfigService config,
        KbService kbs,
        ChangeService changes,
        ScanService scanner,
        SourceService sources,
        KnowledgeService knowledge,
        UnsavedChangesCoordinator unsaved,
        ShareService share,
        McpService mcp,
        LocalAgentService agent,
        ToastService toast,
        INavigationService navigation)
    {
        _config = config;
        _kbService = kbs;
        _changes = changes;
        _scanner = scanner;
        _sources = sources;
        _knowledge = knowledge;
        _unsaved = unsaved;
        _share = share;
        _mcp = mcp;
        _agent = agent;
        _toast = toast;
        _navigation = navigation;
        _share.Changed += OnShareServiceChanged;
        _agent.Changed += OnShareServiceChanged;

        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        AppVersion = version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
        Kbs = [];
        BuildNav();
        Initialize();
    }

    [ObservableProperty] private string _applicationTitle = "LLM Wiki";
    [ObservableProperty] private string _currentKb = "";
    [ObservableProperty] private string _kbPickerText = "未选择知识库";
    [ObservableProperty] private ObservableCollection<string> _kbs = [];
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private string _statusStorage = "";
    [ObservableProperty] private string _statusDirectory = "目录正常";
    [ObservableProperty] private string _statusShare = "浏览分享：关闭";
    [ObservableProperty] private string _statusAi = "AI：未连接";
    [ObservableProperty] private string _storageError = "";
    [ObservableProperty] private string _statusPending = "";
    [ObservableProperty] private string _appVersion = "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private ObservableCollection<FileSearchResult> _searchResults = [];
    [ObservableProperty] private string _searchStatus = "";

    [ObservableProperty] private ObservableCollection<object> _menuItems = [];
    [ObservableProperty] private ObservableCollection<object> _footerMenuItems = [];

    [ObservableProperty] private string _helpTopicId = "";
    [ObservableProperty] private string _helpReturnPageKey = "";

    /// <summary>页面请求打开某个相对路径（全局查找结果）。</summary>
    public event Action<FileSearchResult>? OpenRequested;

    /// <summary>资料页“整理所选”带入的相对路径；整理页消费后清空。</summary>
    public IReadOnlyList<string>? PendingOrganizeSelection { get; set; }

    public event Action? OrganizeSelectionRequested;

    public void RequestOrganizeSelection() => OrganizeSelectionRequested?.Invoke();

    public bool HasKb => !string.IsNullOrWhiteSpace(CurrentKb)
                         && Directory.Exists(Path.Combine(_config.WikisDir, CurrentKb));
    public bool HasStorageError => !string.IsNullOrEmpty(StorageError);
    public bool HasNoKb => !HasKb;
    public string CurrentKbRoot => HasKb ? Path.Combine(_config.WikisDir, CurrentKb) : "";

    private void BuildNav()
    {
        MenuItems =
        [
            new NavigationViewItem
            {
                Content = "知识",
                Icon = new SymbolIcon { Symbol = SymbolRegular.BookOpen24 },
                TargetPageType = typeof(KnowledgePage),
            },
            new NavigationViewItem
            {
                Content = "资料",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Document24 },
                TargetPageType = typeof(SourcePage),
            },
            new NavigationViewItem
            {
                Content = "整理",
                Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSync24 },
                TargetPageType = typeof(OrganizePage),
            },
            new NavigationViewItemSeparator(),
            new NavigationViewItem
            {
                Content = "连接与分享",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Share24 },
                TargetPageType = typeof(ConnectionsPage),
            },
        ];

        FooterMenuItems =
        [

            new NavigationViewItem
            {
                Content = "知识库设置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Folder24 },
                TargetPageType = typeof(KbSettingsPage),
            },
            new NavigationViewItem
            {
                Content = "应用设置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(AppSettingsPage),
            },
            new NavigationViewItem
            {
                Content = "使用帮助",
                Icon = new SymbolIcon { Symbol = SymbolRegular.QuestionCircle24 },
                TargetPageType = typeof(HelpPage),
            },
        ];
    }

    private void Initialize()
    {
        ReloadKbs();
        var target = _config.LastKb;
        if (string.IsNullOrWhiteSpace(target) || (!Kbs.Contains(target) && string.IsNullOrEmpty(StorageError)))
            target = Kbs.FirstOrDefault() ?? "";
        if (!string.IsNullOrWhiteSpace(target)) SetCurrentKb(target);
        else RefreshStatusBar();
    }

    public void ReloadKbs()
    {
        var (list, error) = _kbService.ListKbsWithError();
        Kbs = new ObservableCollection<string>(list);
        StorageError = error;
        OnPropertyChanged(nameof(HasStorageError));
        if (!string.IsNullOrEmpty(error))
        {
            StatusDirectory = "目录不可访问";
            StatusStorage = _config.WikisDir;
        }
    }

    /// <summary>根目录切换成功后：重建库列表，不复用旧库同名身份；清空跨页暂存选择（06§4）。</summary>
    public void ResetForNewRoot()
    {
        ReloadKbs();
        PendingOrganizeSelection = null;
        var previous = CurrentKb;
        CurrentKb = "";
        SetCurrentKb(Kbs.FirstOrDefault() ?? "");
        if (string.Equals(previous, CurrentKb, StringComparison.Ordinal)) UpdateKbState();
    }

    public void SetCurrentKb(string kb)
    {
        if (string.Equals(CurrentKb, kb, StringComparison.Ordinal)) return;
        CurrentKb = kb;
        _config.SetLastKb(kb);
        UpdateKbState();
    }

    public async Task<bool> SwitchKbAsync(string kb)
    {
        if (string.Equals(CurrentKb, kb, StringComparison.Ordinal)) return true;
        if (!await _unsaved.CanLeaveAsync()) return false;
        SetCurrentKb(kb);
        return true;
    }

    private void UpdateKbState()
    {
        KbPickerText = string.IsNullOrWhiteSpace(CurrentKb) ? "未选择知识库" : CurrentKb;
        OnPropertyChanged(nameof(HasKb));
        OnPropertyChanged(nameof(HasNoKb));
        OnPropertyChanged(nameof(CurrentKbRoot));

        if (HasKb)
        {
            try { _scanner.Scan(CurrentKb, recordChanges: true); }
            catch { }
        }
        RefreshStatusBar();
    }

    private void OnShareServiceChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) RefreshStatusBar();
        else dispatcher.BeginInvoke(RefreshStatusBar);
    }

    partial void OnCurrentKbChanged(string value)
    {
        KbPickerText = string.IsNullOrWhiteSpace(value) ? "未选择知识库" : value;
        OnPropertyChanged(nameof(HasKb));
        OnPropertyChanged(nameof(HasNoKb));
        OnPropertyChanged(nameof(CurrentKbRoot));
    }

    public void RefreshStatusBar()
    {
        PendingCount = string.IsNullOrWhiteSpace(CurrentKb) ? 0 : _changes.Load(CurrentKb).Count;
        StatusPending = PendingCount > 0 ? $"{PendingCount} 项待整理" : "暂无待整理";
        StatusStorage = _config.WikisDir;
        StatusDirectory = !string.IsNullOrEmpty(StorageError) ? "目录不可访问"
            : HasKb ? "目录正常"
            : string.IsNullOrWhiteSpace(CurrentKb) ? "未选择知识库" : "目录不可访问";
        StatusShare = _share.Running ? $"浏览分享：{_share.BoundKb}" : "浏览分享：关闭";
        StatusAi = _agent.IsRunning ? "本机任务：运行中" : "本机任务：空闲";
        UpdateNavBadge();
    }

    private void UpdateNavBadge()
    {
        foreach (var item in MenuItems.OfType<NavigationViewItem>())
        {
            if (item.TargetPageType != typeof(OrganizePage)) continue;
            item.Content = PendingCount > 0 ? $"整理  ({PendingCount})" : "整理";
        }
    }

    public async Task<bool> CreateKbAsync(string name, string scene, string? purpose = null)
    {
        try
        {
            IsBusy = true;
            BusyText = "正在创建知识库…";
            _kbService.CreateKb(name, scene, purpose);
            ReloadKbs();
            if (!await SwitchKbAsync(name))
            {
                SetCurrentKb(name);
            }
            _config.SetLastPage(name, "sources");
            _navigation.Navigate(typeof(SourcePage));
            _toast.ShowSuccess("知识库已创建：" + name);
            return true;
        }
        catch (KbExistsException)
        {
            _toast.ShowWarning("这个知识库已存在，可从切换器打开已有知识库");
            return false;
        }
        catch (Exception e)
        {
            _toast.ShowError("创建知识库失败：" + e.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    public async Task<bool> DeleteCurrentKbAsync()
    {
        if (!HasKb) return false;
        if (_agent.IsRunning)
        {
            _toast.ShowWarning("本机任务仍在运行；请先到整理页结束或取消任务，再删除知识库。");
            return false;
        }
        if (!await _unsaved.CanLeaveAsync()) return false;
        var name = CurrentKb;
        if (_share.Running && _share.BoundKb.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            _share.Stop();
        }
        if (_mcp.Running && _mcp.BoundKb.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            if (!_mcp.SetMode(_mcp.BoundKb, McpMode.Off, out var mcpError))
            {
                _toast.ShowError("绑定服务停止失败，未删除知识库：" + mcpError);
                return false;
            }
        }
        try
        {
            _kbService.DeleteKb(name);
            ReloadKbs();
            var next = Kbs.FirstOrDefault() ?? "";
            CurrentKb = "";
            SetCurrentKb(next);
            _navigation.Navigate(typeof(KnowledgePage));
            _toast.ShowSuccess("知识库已永久删除：" + name);
            return true;
        }
        catch (Exception e)
        {
            _toast.ShowError("删除知识库失败：" + e.Message);
            return false;
        }
    }

    /// <summary>帮助/页内链接跳转：先处理全局未保存草稿。</summary>
    public async Task<bool> NavigateWithGuardAsync(string pageKey)
    {
        if (!await _unsaved.CanLeaveAsync()) return false;
        NavigateTo(pageKey);
        return true;
    }

    public async Task OpenHelpAsync(string topicId, string? returnPageKey = null)
    {
        if (!await _unsaved.CanLeaveAsync()) return;
        HelpTopicId = topicId;
        HelpReturnPageKey = returnPageKey ?? "";
        _navigation.Navigate(typeof(HelpPage));
    }

    public void ClearHelpRequest()
    {
        HelpTopicId = "";
    }

    public void ClearHelpContext()
    {
        HelpTopicId = "";
        HelpReturnPageKey = "";
    }

    public void NavigateTo(string pageKey)
    {
        var type = pageKey switch
        {
            "sources" => typeof(SourcePage),
            "organize" => typeof(OrganizePage),
            "connections" => typeof(ConnectionsPage),
            "kbsettings" => typeof(KbSettingsPage),
            "settings" => typeof(AppSettingsPage),
            "help" => typeof(HelpPage),
            _ => typeof(KnowledgePage),
        };
        _navigation.Navigate(type);
    }

    public Type StartupPageType => PageTypeFor(_config.LastPageFor(CurrentKb));

    private static Type PageTypeFor(string pageKey) => pageKey switch
    {
        "sources" => typeof(SourcePage),
        "organize" => typeof(OrganizePage),
        "connections" => typeof(ConnectionsPage),
        "kbsettings" => typeof(KbSettingsPage),
        "settings" => typeof(AppSettingsPage),
        "help" => typeof(HelpPage),
        _ => typeof(KnowledgePage),
    };

    public void RememberPage(string pageKey)
    {
        if (!string.IsNullOrWhiteSpace(CurrentKb)) _config.SetLastPage(CurrentKb, pageKey);
    }

    #region 全局文件名查找（只查当前库文件名，不做正文检索）

    [RelayCommand]
    private void OpenSearch()
    {
        if (!HasKb)
        {
            _toast.ShowWarning("请先选择或创建知识库");
            return;
        }
        SearchQuery = "";
        SearchResults = [];
        SearchStatus = "";
        IsSearchOpen = true;
    }

    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    partial void OnSearchQueryChanged(string value) => RunSearch();

    private void RunSearch()
    {
        SearchResults = [];
        if (!HasKb)
        {
            SearchStatus = "请先选择知识库";
            return;
        }
        var query = (SearchQuery ?? "").Trim();
        if (query.Length == 0)
        {
            SearchStatus = "输入文件名片段，只查找当前库的文件名";
            return;
        }

        var results = new List<FileSearchResult>();
        foreach (var source in _sources.List(CurrentKb))
        {
            if (source.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new FileSearchResult(source.Name, source.RelPath, "资料", "资料 · " + source.TypeText));
            }
        }
        foreach (var node in Flatten(_knowledge.Tree(CurrentKb)))
        {
            if (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new FileSearchResult(node.Name, node.RelPath, "知识", "知识 · " + node.DisplayName));
            }
        }
        SearchResults = new ObservableCollection<FileSearchResult>(results.Take(50));
        SearchStatus = results.Count == 0 ? "没有匹配文件" : $"找到 {results.Count} 项";
    }

    private static IEnumerable<KnowledgeNode> Flatten(IEnumerable<KnowledgeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder) yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    public void OpenResult(FileSearchResult result)
    {
        IsSearchOpen = false;
        OpenRequested?.Invoke(result);
        NavigateTo(result.Kind == "资料" ? "sources" : "knowledge");
    }

    #endregion

    /// <summary>关闭窗口前的统一保护；返回 false 表示留在应用。</summary>
    public async Task<bool> CanCloseAsync() => await _unsaved.CanLeaveAsync();

    /// <summary>D13 退出保护：主窗注入确认框；参数=是否存在未保存草稿。</summary>
    public Func<bool, Task<ExitChoice>>? ConfirmExit { get; set; }

    /// <summary>
    /// D13 退出保护（00§9.4）：只有草稿走 D02；只有本机任务显示“任务仍在运行”；
    /// 二者同时存在用一个 640 宽合并框。导航保护仍走 CanCloseAsync，不触发 D13。
    /// </summary>
    public async Task<bool> CanExitAsync()
    {
        var drafts = _unsaved.HasAny;
        var running = _agent.IsRunning;
        if (!running && !drafts) return true;
        if (!running) return await _unsaved.CanLeaveAsync();

        var choice = ConfirmExit == null ? ExitChoice.Stay : await ConfirmExit(drafts);
        switch (choice)
        {
            case ExitChoice.CancelTaskAndExit:
            case ExitChoice.DiscardAndCancelTaskAndExit:
                if (drafts && choice == ExitChoice.DiscardAndCancelTaskAndExit) _unsaved.DiscardAll();
                return await StopAgentForExitAsync();
            case ExitChoice.SaveAndCancelTaskAndExit:
                // 保存失败不继续取消任务，留在应用。
                if (drafts && !await _unsaved.SaveAllAsync()) return false;
                return await StopAgentForExitAsync();
            case ExitChoice.Stay:
            default:
                return false;
        }
    }

    private async Task<bool> StopAgentForExitAsync()
    {
        _agent.RequestCancel();
        if (_agent.IsRunning)
        {
            await Task.Delay(300);
            if (_agent.IsRunning && !_agent.ForceKill(out var killError))
            {
                _toast.ShowError("无法结束本机任务：" + killError + "；已保留草稿，请手动结束后再退出。");
                return false;
            }
        }
        return true;
    }
}

/// <summary>D13 合并退出框的决策（00§9.4）。</summary>
public enum ExitChoice
{
    Stay,
    CancelTaskAndExit,
    SaveAndCancelTaskAndExit,
    DiscardAndCancelTaskAndExit,
}

public sealed record FileSearchResult(string Name, string RelPath, string Kind, string GroupText)
{
    public string PathText => RelPath;
}
