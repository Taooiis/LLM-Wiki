using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using LlmWiki.App.Services;

namespace LlmWiki.App;

/// <summary>
/// 应用入口：单实例 + 托盘常驻。
/// 单实例靠 Mutex 判定，第二个进程通过命名管道让已有实例把窗口唤到前台，然后自己退出。
/// 托盘常驻：关窗口不等于退出（分享服务在服，关掉就断线了），从托盘菜单才能真正退出。
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\LlmWiki.App.SingleInstance";
    private const string PipeName = "LlmWiki.App.Show";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private MainWindow? _window;
    private readonly List<MainWindow> _childWindows = new();
    private TrayIcon? _tray;
    private CancellationTokenSource? _pipeCts;
    private HotKeyService? _hotKey;

    private string[]? _pendingFiles;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 0. 全局异常捕获 + 崩溃日志（必须最先装）
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash(ex.ExceptionObject as Exception);
        // WPF UI 线程异常
        Dispatcher.UnhandledException += (_, ex) => { LogCrash(ex.Exception); ex.Handled = true; };

        // 1. 单实例判定：拿不到 Mutex 说明已有进程在跑，通知它露脸后本进程退出
        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // 第二个进程：把文件路径通过管道发给已有实例
            SignalExistingInstance(e.Args);
            Shutdown();
            return;
        }
        _ownsMutex = true;

        base.OnStartup(e);

        // 2. 窗口隐藏后应用不能自动退出：只有托盘“退出”或真正关窗才结束进程
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _window = new MainWindow();
        _tray = new TrayIcon(ActivateWindow, ExitApp);
        _window.HiddenToTray += () => _tray?.NotifyHidden();
        // 2.2 初始化托停通知服务
        ToastService.SetNotifyIcon(_tray!.Icon);
        // ShutdownMode 是 OnExplicitShutdown：关掉最后一个窗口不会自动退，需要显式结束
        _window.Closed += (_, _) => Shutdown();
        _window.Show();

        // 2.3 全局快捷键：Ctrl+Shift+L 唤出/隐藏
        _hotKey = new HotKeyService();
        _hotKey.Attach(_window);
        _hotKey.HotKeyPressed += ToggleWindow;

        // 保存待导入的文件路径（App.xaml.cs 传进来）
        _pendingFiles = e.Args.Where(File.Exists).ToArray();
        if (_pendingFiles.Length > 0) _window.PendingFiles = _pendingFiles;

        // 3. 监听“第二次启动”，把已有窗口唤到前台
        _pipeCts = new CancellationTokenSource();
        _ = ListenForActivationAsync(_pipeCts.Token);
    }

    /// <summary>把主面板从托盘/最小化状态捞回前台。</summary>
    private void ActivateWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // 从别的程序切回来时，Windows 不一定给焦点：Topmost 抖一下强制到最前
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>全局快捷键：已在前台则隐藏，否则唤出（toggle）。</summary>
    private void ToggleWindow()
    {
        if (_window is null) return;
        if (_window.IsVisible && _window.IsActive)
        {
            _window.Hide();
            return;
        }
        ActivateWindow();
    }

    /// <summary>托盘“退出”：先置退出标记，让关窗拦截放行。</summary>
    private void ExitApp()
    {
        if (_window is null) { Shutdown(); return; }
        _window.ReallyExit();
    }

    /// <summary>管道服务：每次连接读一行（"show" 或 "show;path1;path2..."），然后把窗口唤到前台。</summary>
    private async Task ListenForActivationAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(token);
                Dispatcher.Invoke(() =>
                {
                    ActivateWindow();
                    // 消息带文件路径（"show;path1;path2"）→ 交给 MainWindow 导入
                    if (line != null && line.StartsWith("show;", StringComparison.OrdinalIgnoreCase))
                    {
                        var files = line.Split(';').Skip(1).Where(File.Exists).ToArray();
                        if (files.Length > 0 && _window is MainWindow w)
                            _ = w.ImportFilesFromCommandLine(files);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 管道断开或被占用：稍后重建，不让后台任务直接死掉
                try { await Task.Delay(500, token); } catch { break; }
            }
        }
    }

    /// <summary>第二个进程调用：连接已有实例的管道让它显示窗口+导入文件。连不上就算了，不阻塞。</summary>
    private static void SignalExistingInstance(string[]? args = null)
    {
        var msg = "show";
        if (args != null && args.Length > 0)
        {
            var files = string.Join(";", args.Where(File.Exists));
            if (files.Length > 0) msg = "show;" + files;
        }
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(200);
                using var writer = new StreamWriter(client);
                writer.WriteLine(msg);
                writer.Flush();
                return;
            }
            catch
            {
                // 已有实例可能还没建好管道，短暂重试
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>崩溃异常写日志 + 弹窗。</summary>
    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var path = Path.GetDirectoryName(Path.GetTempPath()) + "\\LLM Wiki\\crash.log";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var info = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex.GetType()}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(path, info);
            // 不弹窗，防止二次崩溃卡死
        }
        catch { /* 写入失败也别崩 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _hotKey?.Dispose();
        _tray?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* 已释放/线程变更，忽略 */ }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
