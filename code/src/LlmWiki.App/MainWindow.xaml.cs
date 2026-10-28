using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using LlmWiki.App.Bridge;
using LlmWiki.App.Services;

namespace LlmWiki.App;

/// <summary>
/// 主窗口：WPF 壳只负责承载 WebView2 控件，面板 UI 全部在 wwwroot 下。
/// 桌面独有能力（拖拽上传、原生对话框）在这里接管，不走网页的上传控件。
/// </summary>
public partial class MainWindow : Window
{
    private BridgeDispatcher _dispatcher = null!;
    private readonly UiStateStore _ui = new(AppContext.BaseDirectory);
    private bool _reallyExit;
    private System.Windows.Threading.DispatcherTimer? _tbTimer;

    /// <summary>窗口被收进托盘时触发（App 用它弹一次气泡提示）。</summary>
    public event Action? HiddenToTray;

    /// <summary>启动时命令行传入的文件路径（双击文件 / 发送到），WebView2 就绪后导入。</summary>
    public string[]? PendingFiles { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        RestoreGeometry();

        // 2.1 任务栏集成：监听分享状态更新进度条
        _tbTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(1) };
        _tbTimer.Tick += (_, _) => UpdateTaskbarProgress();
        _tbTimer.Start();

        // 2.3 缩略图按钮图标
        ThumbRefresh.ImageSource = CreateIcon("refresh");
        ThumbShareToggle.ImageSource = CreateIcon("share");

        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += OnClosing;
    }

    /// <summary>恢复上次的窗口尺寸/位置；显示器变了导致位置不可见时回到默认居中。</summary>
    private void RestoreGeometry()
    {
        var s = _ui.Load();

        if (s.Width is > 480 and < 20000) Width = s.Width.Value;
        if (s.Height is > 360 and < 20000) Height = s.Height.Value;

        if (s.Left is { } left && s.Top is { } top && IsPositionVisible(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (s.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>左上角至少落在虚拟屏幕内，避免拔掉副屏后窗口出现在看不见的地方。</summary>
    private static bool IsPositionVisible(double left, double top)
    {
        var maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120;
        var maxY = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;
        return left >= SystemParameters.VirtualScreenLeft && left <= maxX
            && top >= SystemParameters.VirtualScreenTop && top <= maxY;
    }

    /// <summary>存几何：最大化时存还原后的边框，否则下次恢复会得到全屏尺寸。</summary>
    private void SaveGeometry()
    {
        var b = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _ui.Update(s =>
        {
            s.Left = b.Left;
            s.Top = b.Top;
            s.Width = b.Width;
            s.Height = b.Height;
            s.Maximized = WindowState == WindowState.Maximized;
        });
    }

    /// <summary>
    /// 关窗口的语义分两种：
    /// 分享服务在跑 → 只是收进托盘，网页分享不能因为关面板就断；
    /// 没在分享 → 正常退出（后台常驻没有意义）。
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 几何每次都记：即使这次只是缩到托盘，下次打开也该在原位
        SaveGeometry();

        if (_reallyExit) return;
        if (_dispatcher?.Sharing != true) return;

        e.Cancel = true;
        Hide();
        HiddenToTray?.Invoke();
    }

    /// <summary>从托盘唤回面板。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>真正退出：置标记放行关窗，再结束应用（停掉分享服务与监听）。</summary>
    public void ReallyExit()
    {
        _reallyExit = true;
        Close();
        Application.Current.Shutdown();
    }

    /// <summary>2.1 任务栏集成：同步分享状态到任务栏进度条。</summary>
    private void UpdateTaskbarProgress()
    {
        if (TaskbarItemInfo is not { } tb) return;
        if (_dispatcher?.Sharing == true)
        {
            tb.ProgressState = TaskbarItemProgressState.Indeterminate;
            tb.Description = "LLM Wiki - 分享服务已启用";
            ThumbShareToggle.Description = "关闭分享";
            ThumbShareToggle.IsEnabled = true;
        }
        else
        {
            tb.ProgressState = TaskbarItemProgressState.None;
            tb.Description = null;
            ThumbShareToggle.Description = "开启分享";
            ThumbShareToggle.IsEnabled = true;
        }
    }

    /// <summary>2.3 缩略图按钮图标：简单矢量图标。</summary>
    private static ImageSource CreateIcon(string type)
    {
        var geo = new PathGeometry();
        // use GeometryDrawing which is a Drawing, works with DrawingImage
        var drawing = new GeometryDrawing
        {
            Brush = Brushes.White,
            Pen = new Pen(Brushes.White, 1.5),
            Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) // placeholder rect
        };

        switch (type)
        {
            case "refresh":
                drawing.Geometry = Geometry.Parse(
                    "M13.5 12.5L11 15L13.5 17.5L12 19L7 14L12 9L13.5 10.5L11 13H15V15H11");
                break;
            case "share":
                drawing.Geometry = Geometry.Parse("M8 5A3 3 0 1 8 11A3 3 0 0 0 8 5M4.5 14A2.5 2.5 0 1 1 4.5 19A2.5 2.5 0 0 1 4.5 14M11.5 14A2.5 2.5 0 1 1 11.5 19A2.5 2.5 0 0 1 11.5 14");
                drawing.Brush = Brushes.Transparent;
                break;
        }
        return new DrawingImage(drawing);
    }

    /// <summary>2.3 缩略图按钮：刷新</summary>
    private void OnThumbRefresh(object sender, EventArgs e)
    {
        _ = BridgeWebView.CoreWebView2?.ExecuteScriptAsync(
            "if (typeof loadTree === 'function') loadTree();");
    }

    /// <summary>2.3 缩略图按钮：切换分享</summary>
    private void OnThumbShareToggle(object sender, EventArgs e)
    {
        if (_dispatcher?.Sharing == true)
            _ = _dispatcher.HandleAsync("share-stop", "{}");
        else
            _ = _dispatcher.HandleAsync("share-start",
                new System.Text.Json.Nodes.JsonObject { ["port"] = 0 }.ToJsonString());
    }

    private async Task InitWebViewAsync()
    {
        string? available = CoreWebView2Environment.GetAvailableBrowserVersionString();
        if (string.IsNullOrEmpty(available))
        {
            MessageBox.Show("未检测到 WebView2 运行时：请安装 Microsoft Edge WebView2 后重试。",
                "LLM Wiki", MessageBoxButton.OK, MessageBoxImage.Warning);
            Application.Current.Shutdown();
            return;
        }

        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(wwwroot);

        var env = await CoreWebView2Environment.CreateAsync();
        await BridgeWebView.EnsureCoreWebView2Async(env);

        // 1. 注入 Bridge JS 垫片
        string js = await File.ReadAllTextAsync(Path.Combine(wwwroot, "bridge.js"));
        await BridgeWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(js);

        // 关掉浏览器自带快捷键：否则 Ctrl+S 会弹「保存网页」、Ctrl+N 会开新窗口。
        // 页面自己处理 Ctrl+S/N/E（桌面应用该有自己的键位）。
        BridgeWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // 2. 注册消息接收（协议见 Bridge/BridgeProtocol.cs）
        BridgeWebView.CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            var msg = BridgeProtocol.ParseRequest(e.WebMessageAsJson);
            if (msg == null) return;

            var result = await _dispatcher.HandleAsync(msg.Cmd, msg.Args);
            BridgeWebView.CoreWebView2.PostWebMessageAsString(
                BridgeProtocol.CreateResponse(msg.RequestId, result));
        };

        // 3. 初始化后端分发器（服务按当前存储路径现取，改路径立即生效）
        var cfg = new ConfigService(AppContext.BaseDirectory);
        _dispatcher = new BridgeDispatcher(cfg);
        // 界面记忆共享给前端（同一份 ui.json）
        _dispatcher.UiStore = _ui;

        // 原生对话框：替代网页 prompt()/confirm()（桌面应用该用系统控件）
        _dispatcher.ConfirmDialog = (message, title) =>
            MessageBox.Show(this, message, title.Length > 0 ? title : "LLM Wiki",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        _dispatcher.InputDialog = (message, title, defaultValue) =>
            PromptWindow.Show(this, message, title.Length > 0 ? title : "LLM Wiki", defaultValue);

        // 系统剪贴板：WebView2 里的 navigator.clipboard 在无焦点时会失败，交给宿主写更稳
        _dispatcher.ClipboardSet = text =>
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch { return false; }
        };

        // 标题跟随前端：document.title 由页面按「当前知识库 + 未保存标记」维护
        BridgeWebView.CoreWebView2.DocumentTitleChanged += (_, _) =>
        {
            var t = BridgeWebView.CoreWebView2.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(t)) Title = t;
        };

        // 原生系统文件夹选择器（WPF 桌面应用：直接弹系统对话框，可浏览本机与网络共享）
        _dispatcher.FolderPicker = () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择存储文件夹",
                InitialDirectory = cfg.WikisDir,
            };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        };
        // 原生多选文件对话框：拿到真实路径，C# 直接读流（不经过浏览器 FileReader）
        _dispatcher.FilePicker = () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要入库的文件",
                Filter = "支持的文档|*.md;*.txt;*.docx;*.xlsx|Markdown|*.md|文本|*.txt|Word|*.docx|Excel|*.xlsx",
                Multiselect = true,
                CheckFileExists = true,
            };
            return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
        };

        // 5. 实时变更监听：Agent 在外部改文件后，面板自己刷新
        var watcher = new FileWatcher();
        watcher.Changed += root => Dispatcher.InvokeAsync(() =>
        {
            if (BridgeWebView.CoreWebView2 is null) return;
            BridgeWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "fs-change",
                kb = Path.GetFileName(root.TrimEnd('\\', '/')),
            }));
        });
        _dispatcher.Watcher = watcher;
        Closed += (_, _) => watcher.Dispose();

        // 4. 拖拽上传：WebView2 默认自己吞掉外部拖放（JS 拿不到真实路径），
        //    关掉它让 WPF 接管，才能把本机/网络共享路径直接交给 C# 读流。
        BridgeWebView.AllowExternalDrop = false;
        BridgeWebView.AllowDrop = true;
        BridgeWebView.DragOver += OnFileDragOver;
        BridgeWebView.DragLeave += OnFileDragLeave;
        BridgeWebView.Drop += OnFileDrop;
        DropOverlayHint.Text = "仅支持 .md / .txt / .docx / .xlsx；文件夹只取一层";

        BridgeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        BridgeWebView.CoreWebView2.Navigate("https://app.local/index.html");

        // 启动时命令行传入的文件：双击关联 / 发送到，导入到当前知识库
        if (PendingFiles?.Length > 0)
            await ImportFilesFromCommandLine(PendingFiles);
    }

    /// <summary>拖入文件时显示提示层；其它类型（文字等）不接管。</summary>
    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>松手：取真实路径 → 上传到前端当前选中的知识库 → 交回前端刷新。</summary>
    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropOverlay.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        try
        {
            // 知识库由前端下拉框持有：现读一次，保证和用户看到的一致
            var kb = await ReadActiveKbAsync();
            if (string.IsNullOrEmpty(kb))
            {
                await BridgeWebView.ExecuteScriptAsync("toast('先选择知识库再拖入文件','err')");
                return;
            }

            var args = new JsonObject
            {
                ["kb"] = kb,
                ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode?)p)]),
            };
            // 分发器直接返回 JSON 对象字符串，可原样作为 JS 实参
            var raw = await _dispatcher.HandleAsync("upload-path", args.ToJsonString());
            await BridgeWebView.ExecuteScriptAsync($"window.onDropResult && window.onDropResult({raw})");
        }
        catch (Exception ex)
        {
            await BridgeWebView.ExecuteScriptAsync(
                $"toast({JsonSerializer.Serialize("拖拽上传失败：" + ex.Message)},'err')");
        }
    }

    /// <summary>读前端当前选中的知识库名（ExecuteScriptAsync 返回的是 JSON 字面量）。</summary>
    private async Task<string> ReadActiveKbAsync()
    {
        var raw = await BridgeWebView.ExecuteScriptAsync("document.getElementById('kbSel').value");
        try { return JsonSerializer.Deserialize<string>(raw) ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// 命令行 / 管道传入的文件：等待 WebView2 就绪 → 读当前知识库 → 调用 upload-path 上传 → 刷新前端。
    /// 复用拖拽上传的同一个路径，不走 base64。
    /// </summary>
    public async Task ImportFilesFromCommandLine(string[] paths)
    {
        if (!await WaitForWebViewReady()) return;
        try
        {
            var kb = await ReadActiveKbAsync();
            if (string.IsNullOrEmpty(kb))
            {
                await BridgeWebView.ExecuteScriptAsync("toast('先选择知识库再打开文件','err')");
                return;
            }

            var args = new JsonObject
            {
                ["kb"] = kb,
                ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode?)p)]),
            };
            var raw = await _dispatcher.HandleAsync("upload-path", args.ToJsonString());
            await BridgeWebView.ExecuteScriptAsync($"window.onDropResult && window.onDropResult({raw})");

            // 2.2 Toast 通知：导入完成
            var resp = JsonSerializer.Deserialize<JsonObject>(raw);
            if (resp["ok"]?.GetValue<bool>() == true)
                ToastService.ShowImportComplete(kb, paths.Length);
        }
        catch (Exception ex)
        {
            await BridgeWebView.ExecuteScriptAsync(
                $"toast({JsonSerializer.Serialize("导入失败：" + ex.Message)},'err')");
        }
    }

    /// <summary>等待 WebView2 Core 初始化完成（拖拽/命令行导入都依赖此）。</summary>
    private async Task<bool> WaitForWebViewReady(int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (BridgeWebView.CoreWebView2 != null) return true;
            await Task.Delay(100);
        }
        return false;
    }

    // ---------- 菜单栏事件 ----------
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择文件夹",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LlmWiki", "wikis")
        };
        dlg.ShowDialog(this);
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开文件",
            Filter = "支持的文件|*.md;*.txt;*.docx;*.xlsx|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            _ = ImportFilesFromCommandLine([dlg.FileName]);
    }

    private void OnCut(object sender, RoutedEventArgs e) => Clipboard.SetText(""); // 占位：实际在网页里操作
    private void OnCopy(object sender, RoutedEventArgs e) => Clipboard.SetText("");
    private void OnPaste(object sender, RoutedEventArgs e) => Clipboard.SetText("");
    private void OnRefresh(object sender, RoutedEventArgs e) => BridgeWebView.CoreWebView2?.ExecuteScriptAsync("if (typeof loadTree === 'function') loadTree();");
    private void OnExit(object sender, RoutedEventArgs e)
    {
        if (_dispatcher?.Sharing == true)
        {
            if (MessageBox.Show(this, "分享服务正在运行，确定退出吗？", "LLM Wiki",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.Cancel)
                return;
        }
        ReallyExit();
    }
    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "LLM Wiki 知识库管理\n版本：开发中\n\n© 2026 作者：LLM Wiki 项目",
            "关于 LLM Wiki", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow { Owner = this };
        dlg.ShowDialog();
    }

    // ---------- 工具栏按钮 ----------
    private void OnShareToggle(object sender, RoutedEventArgs e)
    {
        if (_dispatcher?.Sharing == true)
            _ = _dispatcher.HandleAsync("share-stop", "{}");
        else
            _ = _dispatcher.HandleAsync("share-start", new JsonObject { ["port"] = 0 }.ToJsonString());
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        BridgeWebView?.CoreWebView2?.ExecuteScriptAsync(
            "document.querySelector('input[role=\"search\"]')?.focus();");
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        BridgeWebView?.CoreWebView2?.ExecuteScriptAsync("toggleTheme();");
    }

    // ---------- 项目加载 ----------
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _ = loadKbList();
        _ = Updates();
    }

    // ---------- 状态栏更新 ----------
    private async Task Updates()
    {
        while (true)
        {
            try
            {
                // 更新知识库
                if (BridgeWebView?.CoreWebView2 != null)
                {
                    var kb = await ReadActiveKbAsync();
                    StatusKb?.Text = kb.Split(Path.DirectorySeparatorChar).LastOrDefault() ?? kb;
                    
                    // 更新文件数
                    var r = await _dispatcher.HandleAsync("tree", new JsonObject { ["kb"] = kb }.ToJsonString());
                    var data = JsonSerializer.Deserialize<JsonObject>(r);
                    var nodes = data?["tree"] as JsonArray;
                    var count = CountFiles(nodes);
                    StatusFileCount?.Text = count.ToString();
                }

                // 更新分享状态
                StatusShare?.Visibility = _dispatcher?.Sharing == true ? Visibility.Visible : Visibility.Collapsed;

                await Task.Delay(1000);
            }
            catch { await Task.Delay(500); }
        }
    }

    private static int CountFiles(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var type = obj["type"]?.ToString() ?? "";
            if (type == "file") return 1;
            var count = 0;
            if (obj["children"] is JsonArray arr)
                foreach (var child in arr) count += CountFiles(child);
            return count;
        }
        return 0;
    }

    private async Task loadKbList()
    {
        if (BridgeWebView?.CoreWebView2 == null) return;
        var r = await _dispatcher.HandleAsync("kbs-list", "{}");
        var data = JsonSerializer.Deserialize<JsonObject>(r);
        var kbs = data["kbs"] as JsonArray;
        // 填充知识库列表到前端
        var kbJson = kbs?.Select(k => (JsonNode?)k).ToArray() ?? [];
        await BridgeWebView.ExecuteScriptAsync(
            $"populateKbList({JsonSerializer.Serialize(kbJson)})");

        // 加载第一个知识库的目录树到 WPF TreeView
        if (kbJson?.Length > 0 && BridgeWebView?.CoreWebView2 != null)
        {
            var kbName = kbJson[0].AsObject()?["name"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(kbName))
                await LoadTreeAsync(kbName);
        }
    }

    // ---------- 目录树 ----------
    private async Task LoadTreeAsync(string kb)
    {
        var args = new JsonObject { ["kb"] = kb };
        var raw = await _dispatcher.HandleAsync("tree", args.ToJsonString());
        var data = JsonSerializer.Deserialize<JsonObject>(raw);
        var nodes = data?["tree"] as JsonArray;

        DirTree.Items.Clear();
        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                if (node is JsonObject n)
                {
                    var item = CreateTreeItem(n, kb);
                    DirTree.Items.Add(item);
                }
            }
        }
    }

    private TreeViewItem CreateTreeItem(JsonObject node, string kb)
    {
        var name = node["name"]?.ToString() ?? "";
        var type = node["type"]?.ToString() ?? "file";

        var item = new TreeViewItem
        {
            Header = name,
            Tag = new KbTreeNode(kb, name, type)
        };

        if (type == "dir")
        {
            var children = node["children"] as JsonArray;
            if (children != null)
            {
                foreach (var child in children)
                {
                    if (child is JsonObject c)
                    {
                        item.Items.Add(CreateTreeItem(c, kb));
                    }
                }
            }
        }

        item.MouseLeftButtonDown += TreeItem_MouseLeftDown;
        item.MouseRightButtonUp += TreeItem_MouseRightUp;
        return item;
    }

    private async void TreeItem_MouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        var item = sender as TreeViewItem;
        var tag = item?.Tag as KbTreeNode;
        if (tag?.Type == "dir") return;

        if (BridgeWebView?.CoreWebView2 == null) return;

        // 导航到选中项
        var args = new JsonObject { ["kb"] = tag.Kb, ["path"] = tag.Path };
        var raw = await _dispatcher.HandleAsync("file-read", args.ToJsonString());
        var data = JsonSerializer.Deserialize<JsonObject>(raw);

        if (data?["error"] == null)
        {
            await BridgeWebView.ExecuteScriptAsync(
                $"showContent({data?.ToJsonString() ?? "{}"})");
        }
    }

    private void TreeItem_MouseRightUp(object sender, MouseButtonEventArgs e)
    {
        var item = sender as TreeViewItem;
        var tag = item?.Tag as KbTreeNode;
        if (tag == null) return;

        var menu = new ContextMenu();
        var pos = e.GetPosition(null);

        var revealItem = new MenuItem { Header = "定位到资源管理器" };
        revealItem.Click += async (_, _) =>
        {
            await _dispatcher.HandleAsync("file-reveal",
                new JsonObject { ["kb"] = tag.Kb, ["path"] = tag.Path }.ToJsonString());
        };

        var openItem = new MenuItem { Header = "用默认程序打开" };
        openItem.Click += async (_, _) =>
        {
            await _dispatcher.HandleAsync("file-open",
                new JsonObject { ["kb"] = tag.Kb, ["path"] = tag.Path }.ToJsonString());
        };

        var copyItem = new MenuItem { Header = "复制完整路径" };
        copyItem.Click += async (_, _) =>
        {
            await _dispatcher.HandleAsync("file-copy-path",
                new JsonObject { ["kb"] = tag.Kb, ["path"] = tag.Path }.ToJsonString());
        };

        menu.Items.Add(revealItem);
        menu.Items.Add(openItem);
        menu.Items.Add(copyItem);
        menu.IsOpen = true;
        menu.HorizontalOffset = pos.X;
        menu.VerticalOffset = pos.Y;
    }

    // 辅助 record 类型传递数据
    private record KbTreeNode(string Kb, string Path, string Type);
}
