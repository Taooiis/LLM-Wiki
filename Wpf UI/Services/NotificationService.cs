using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace LLM_Wiki.Services;

/// <summary>
/// 一条全局通知。Category 标记来源类别（task/mcp/share/import/system…），
/// Level 为 info/success/warning/error；新消息类别零改动，通知中心只负责记录与展示。
/// </summary>
public sealed class NotificationMessage
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Level { get; init; } = "info";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public DateTime Time { get; init; }
    public bool IsRead { get; set; }
}

/// <summary>
/// 全局通知中心：各来源经 Publish 发布带时间戳的消息，右上角铃铛统一查看。
/// 展示窗口固定近 3 天；消息按天落 JSON Lines 日志（%APPDATA%\LLM_Wiki\notifications），
/// 已读状态单独存 read-state.json，重启后未读不丢。线程安全：Changed 已 marshal 到 UI 线程。
/// </summary>
public sealed class NotificationService
{
    private const int MaxItems = 200;
    private static readonly TimeSpan Window = TimeSpan.FromDays(3);

    private readonly object _gate = new();
    private readonly List<NotificationMessage> _items = new();
    private readonly string _dir;
    private readonly string _readStatePath;
    private int _seq;
    private int _unread;

    public NotificationService()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki", "notifications");
        _readStatePath = Path.Combine(_dir, "read-state.json");
        LoadRecent();
    }

    /// <summary>近 3 天的通知，最新在前。UI 监听 Changed 后重新取 Items。</summary>
    public IReadOnlyList<NotificationMessage> Items
    {
        get
        {
            lock (_gate)
            {
                var floor = DateTime.Now - Window;
                return [.. _items.Where(m => m.Time >= floor)];
            }
        }
    }

    public int UnreadCount
    {
        get
        {
            lock (_gate)
            {
                var floor = DateTime.Now - Window;
                return _items.Count(m => !m.IsRead && m.Time >= floor);
            }
        }
    }

    /// <summary>发布一条通知：入内存列表、按天追加日志文件。线程安全。</summary>
    public void Publish(string category, string level, string title, string detail = "")
    {
        var message = new NotificationMessage
        {
            Id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Interlocked.Increment(ref _seq).ToString("x4"),
            Category = category,
            Level = level,
            Title = title,
            Detail = detail,
            Time = DateTime.Now,
        };
        lock (_gate)
        {
            _items.Insert(0, message);
            TrimLocked();
            try { AppendFile(message); } catch { }
            RecountUnreadLocked();
        }
        RaiseChanged();
    }

    /// <summary>单条标记已读。</summary>
    public void MarkRead(string id)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(m => m.Id == id);
            if (item == null || item.IsRead) return;
            item.IsRead = true;
            RecountUnreadLocked();
            try { SaveReadStateLocked(); } catch { }
        }
        RaiseChanged();
    }

    public void MarkAllRead()
    {
        lock (_gate)
        {
            foreach (var m in _items) m.IsRead = true;
            RecountUnreadLocked();
            try { SaveReadStateLocked(); } catch { }
        }
        RaiseChanged();
    }

    /// <summary>清空当前视图（日志文件保留留档）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _unread = 0;
            try { SaveReadStateLocked(); } catch { }
        }
        RaiseChanged();
    }

    /// <summary>启动时加载近 3 天日志并恢复已读状态。</summary>
    private void LoadRecent()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var floor = DateTime.Now - Window;
            var readIds = LoadReadState();
            var loaded = new List<NotificationMessage>();
            foreach (var file in Directory.EnumerateFiles(_dir, "notifications-*.jsonl"))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var m = JsonSerializer.Deserialize<NotificationMessage>(line);
                        if (m != null && m.Time >= floor) loaded.Add(m);
                    }
                    catch { }
                }
            }
            lock (_gate)
            {
                _items.Clear();
                _items.AddRange(loaded.OrderByDescending(m => m.Time).Take(MaxItems));
                foreach (var m in _items) m.IsRead = readIds.Contains(m.Id);
                RecountUnreadLocked();
            }
        }
        catch { }
    }

    private void TrimLocked()
    {
        var floor = DateTime.Now - Window;
        _items.RemoveAll(m => m.Time < floor);
        while (_items.Count > MaxItems) _items.RemoveAt(_items.Count - 1);
    }

    private void RecountUnreadLocked()
    {
        var floor = DateTime.Now - Window;
        _unread = _items.Count(m => !m.IsRead && m.Time >= floor);
    }

    private void AppendFile(NotificationMessage message)
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "notifications-" + message.Time.ToString("yyyy-MM-dd") + ".jsonl");
        File.AppendAllText(file, JsonSerializer.Serialize(message) + Environment.NewLine, new UTF8Encoding(false));
    }

    private void SaveReadStateLocked()
    {
        Directory.CreateDirectory(_dir);
        var readIds = _items.Where(m => m.IsRead).Select(m => m.Id).ToList();
        File.WriteAllText(_readStatePath, JsonSerializer.Serialize(readIds), new UTF8Encoding(false));
    }

    private HashSet<string> LoadReadState()
    {
        if (!File.Exists(_readStatePath)) return new HashSet<string>();
        var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_readStatePath)) ?? [];
        return new HashSet<string>(ids);
    }

    private void RaiseChanged() => Ui.Run(() => Changed?.Invoke());

    public event Action? Changed;
}
