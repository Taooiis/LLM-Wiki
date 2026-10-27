using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
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

    /// <summary>窗口被收进托盘时触发（App 用它弹一次气泡提示）。</summary>
    public event Action? HiddenToTray;

    public MainWindow()
    {
        InitializeComponent();
        RestoreGeometry();
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
}
