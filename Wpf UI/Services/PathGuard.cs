using System.IO;
using System.Text.RegularExpressions;

namespace LLM_Wiki.Services;

/// <summary>
/// 路径防护：所有库文件与应用配置只经此处校验（对照 Demo server.js:15-47）。
/// </summary>
public static partial class PathGuard
{
    // 库名：中英文、数字、下划线、中划线（Demo server.js:36-39 同规则）
    [GeneratedRegex("^[a-zA-Z0-9_\\-\\u4e00-\\u9fa5]+$")]
    private static partial Regex KbNameRegex();

    /// <summary>解析存储目录：盘符绝对/UNC 直接用；相对路径相对 appRoot；空值回退 appRoot\wikis；单个反斜杠开头判非法（盘符相对路径，结果不可控）。</summary>
    public static string? ResolveWikisDir(string? v, string appRoot)
    {
        if (string.IsNullOrWhiteSpace(v)) return Path.Combine(appRoot, "wikis");
        var t = v.Trim();
        if (t.StartsWith('\\') && !t.StartsWith("\\\\")) return null;
        return Path.IsPathRooted(t) ? Path.GetFullPath(t) : Path.GetFullPath(Path.Combine(appRoot, t));
    }

    /// <summary>库内安全拼接：拒绝绝对路径与 .. 逃逸，保证结果必须落在 wikisDir\kb\ 内。</summary>
    public static string? SafeJoin(string wikisDir, string kb, string? rel)
    {
        if (!ValidKb(kb)) return null;
        var baseDir = Path.GetFullPath(Path.Combine(wikisDir, kb));
        if (rel != null && (Path.IsPathRooted(rel) || rel.Split('\\', '/').Contains("..")))
            return null;
        var p = rel is null or "" ? baseDir : Path.GetFullPath(Path.Combine(baseDir, rel));
        // 目录内成立硬要求：base + 分隔符 为前缀（防 foobar 吞 foo 的前缀碰撞）
        return p.StartsWith(baseDir + Path.DirectorySeparatorChar)
            || p.Equals(baseDir, StringComparison.OrdinalIgnoreCase)
            ? p
            : null;
    }

    /// <summary>知识库名合法性。</summary>
    public static bool ValidKb(string? name) => !string.IsNullOrEmpty(name) && KbNameRegex().IsMatch(name);
}
