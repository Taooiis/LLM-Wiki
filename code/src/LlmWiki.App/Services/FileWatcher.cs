using System.IO;

namespace LlmWiki.App.Services;

/// <summary>
/// 知识库文件变更监听：Agent 在外部改文件后，面板自己刷新，不用手点“刷新”。
/// 只监听当前知识库根（递归）；连续写入合并成一次回调，避免批量改文件时刷屏。
/// 事件在后台线程触发，调用方自行切到 UI 线程。
/// </summary>
public sealed class FileWatcher : IDisposable
{
    // 合并窗口：保存大文件或 Agent 批量写会产生一串事件，600ms 内只回调一次
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(600);

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;

    /// <summary>当前监听的库根目录；未监听时为 null。</summary>
    public string? CurrentRoot { get; private set; }

    /// <summary>变更回调，参数为发生变化的库根目录（已防抖）。</summary>
    public event Action<string>? Changed;

    /// <summary>切换到指定库根；路径不存在或为空则只停止监听。</summary>
    public void Watch(string? root)
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            CurrentRoot = null;

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
                // 默认 8KB 缓冲在批量写时容易溢出，放大到 64KB
                InternalBufferSize = 64 * 1024,
            };
            w.Changed += OnAny;
            w.Created += OnAny;
            w.Deleted += OnAny;
            w.Renamed += OnAny;
            w.EnableRaisingEvents = true;

            _watcher = w;
            CurrentRoot = root;
        }
    }

    private void OnAny(object? sender, FileSystemEventArgs e)
    {
        // Office 保存会生成 ~$xxx.docx 临时文件，对用户没有意义
        var name = e.Name;
        if (name is not null && Path.GetFileName(name).StartsWith("~$", StringComparison.Ordinal)) return;

        string? root;
        lock (_gate)
        {
            root = CurrentRoot;
            if (root is null) return;

            _debounce ??= new System.Threading.Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    // 回调时现取当前根：防抖窗口内切换了知识库，就只通知新库
    private void Fire()
    {
        string? root;
        lock (_gate) root = CurrentRoot;
        if (root is not null) Changed?.Invoke(root);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
            CurrentRoot = null;
        }
    }
}
