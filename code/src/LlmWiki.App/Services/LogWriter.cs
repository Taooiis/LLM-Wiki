using System.IO;

namespace LlmWiki.App.Services;

/// <summary>
/// 库内 wiki/log.md 追加器（对应 Demo server.js:72-81 的 logSourceChange）。
/// 日志失败不影响主流程（try 吞错，同 Demo）。
/// </summary>
public static class LogWriter
{
    // 通用追加一行
    public static void Append(string wikisDir, string kb, string line)
    {
        try
        {
            var dir = Path.Combine(wikisDir, kb, "wiki");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "log.md");
            if (!File.Exists(file)) File.WriteAllText(file, "# 操作历史\n\n");
            File.AppendAllText(file, line + "\n");
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    /// <summary>源范围动作行；note 为 null 自动补"（需重新 Ingest）"，空串则不加（配置更新用）。</summary>
    public static void AppendSource(string wikisDir, string kb, string rel, string action, string? note = null)
        => Append(wikisDir, kb,
            $"- {Now} {action}：{rel}{note ?? "（需重新 Ingest）"}");

    /// <summary>统一时间戳格式 yyyy-MM-dd HH:mm（Demo 同款）。</summary>
    public static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm");
}
