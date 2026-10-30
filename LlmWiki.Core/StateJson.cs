using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>
/// .llmwiki/ 状态 JSON 的读写：临时文件 + 原子替换；未知字段通过 JsonExtensionData 保留。
/// 未来 schemaVersion 只读报错，不以空对象覆盖。
/// </summary>
public static class StateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(text, Options);
    }

    public static void Save<T>(string path, T value) where T : class
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var text = JsonSerializer.Serialize(value, Options);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>兼容入口：文件损坏时返回 null 并给出错误，不静默覆盖。</summary>
    public static T? TryLoad<T>(string path, out string error) where T : class
    {
        error = "";
        try { return Load<T>(path); }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }
}

/// <summary>每库隐藏状态目录 .llmwiki 的路径约定。</summary>
public sealed class KbPaths(string kbRoot)
{
    public string Root { get; } = Path.GetFullPath(kbRoot);
    public string Wiki => Path.Combine(Root, "wiki");
    public string Sources => Path.Combine(Root, "raw", "sources");
    public string Hidden => Path.Combine(Root, ".llmwiki");

    public string Manifest => Path.Combine(Hidden, "manifest.json");
    public string Reviews => Path.Combine(Hidden, "reviews.json");
    public string KbMeta => Path.Combine(Hidden, "kb.json");
    public string TasksDir => Path.Combine(Hidden, "tasks");
    public string OperationsDir => Path.Combine(Hidden, "operations");
    public string TrashDir => Path.Combine(Hidden, "trash");
    public string Changes => Path.Combine(Root, ".changes.json");

    public string Task(string id) => Path.Combine(TasksDir, id + ".json");
    public string Operation(string id) => Path.Combine(OperationsDir, id + ".json");
    public string TrashEntry(string id) => Path.Combine(TrashDir, id);

    public void EnsureStateDirs()
    {
        Directory.CreateDirectory(TasksDir);
        Directory.CreateDirectory(OperationsDir);
        Directory.CreateDirectory(TrashDir);
    }
}

/// <summary>源格式规则：本次仅 md/txt/docx/xlsx，单文件 20 MiB；原字节保存，不转 Markdown。</summary>
public static class SourceRules
{
    public const long MaxFileBytes = 20L * 1024 * 1024;

    public static readonly string[] SupportedExtensions = [".md", ".txt", ".docx", ".xlsx"];

    public static bool IsSupported(string name)
        => SupportedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    public static bool IsText(string name)
        => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    public static string TypeLabel(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".md" => "Markdown",
        ".txt" => "文本",
        ".docx" => "Word",
        ".xlsx" => "Excel",
        _ => "文件",
    };

    public static string SizeText(long size) => size < 1024 ? $"{size} B"
        : size < 1024 * 1024 ? $"{size / 1024.0:0.#} KB"
        : $"{size / 1024.0 / 1024.0:0.#} MB";

    public static string RelativeTime(DateTime time)
    {
        var today = DateTime.Today;
        var day = time.Date;
        return day == today ? $"今天 {time:HH:mm}"
            : day == today.AddDays(-1) ? $"昨天 {time:HH:mm}"
            : time.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>Office 锁文件、应用临时文件、隐藏状态目录不进入扫描。</summary>
    public static bool ShouldIgnore(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.StartsWith('.') || name.StartsWith("~$")) return true;
        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase)) return true;
        return !IsSupported(name);
    }
}
