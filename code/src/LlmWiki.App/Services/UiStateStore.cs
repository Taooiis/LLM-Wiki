using System.IO;
using System.Text.Json;

namespace LlmWiki.App.Services;

/// <summary>
/// 界面记忆：窗口几何 + 上次知识库 + 上次 tab，存 AppRoot/ui.json。
/// 读写都走 Update（读-改-写 + 进程内锁）：窗口关窗时存几何、前端切 tab 时存 tab，互不覆盖。
/// </summary>
public class UiStateStore(string appRoot)
{
    // 两个实例（窗口 / 桥分发器）指向同一文件，锁放静态避免交错写
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions J = new() { WriteIndented = true };

    private readonly string _file = Path.Combine(Path.GetFullPath(appRoot), "ui.json");

    /// <summary>读取当前记忆；文件缺失或损坏一律回默认值，不影响启动。</summary>
    public UiState Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(_file)) return new UiState();
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(_file)) ?? new UiState();
            }
            catch { return new UiState(); }
        }
    }

    /// <summary>读-改-写：只动调用方关心的字段，其它字段保持磁盘上的最新值。</summary>
    public void Update(Action<UiState> change)
    {
        lock (Gate)
        {
            var state = Load();
            change(state);
            try { File.WriteAllText(_file, JsonSerializer.Serialize(state, J)); }
            catch { /* 记忆不是关键路径：写失败不打断使用 */ }
        }
    }
}

/// <summary>界面状态。数值可空：首次启动没有历史，交给 WPF 默认值。</summary>
public class UiState
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>上次选中的知识库名（库被删就回退到第一个）。</summary>
    public string LastKb { get; set; } = "";

    /// <summary>上次停留的页签索引（0=待处理变更…5=小白指南）。</summary>
    public int LastTab { get; set; }
}
