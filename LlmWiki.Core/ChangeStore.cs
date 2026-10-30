using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>待整理变更条目；字段小写序列化，保持既有 .changes.json 形状。</summary>
public record Change(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("time")] string Time,
    [property: JsonPropertyName("hash")] string Hash);

/// <summary>.changes.json 读写（按库根目录）。写入先临时文件再原子替换。</summary>
public static class ChangeStore
{
    public static string PathFor(string kbRoot) => Path.Combine(kbRoot, ".changes.json");

    public static List<Change> Load(string kbRoot)
    {
        try
        {
            var f = PathFor(kbRoot);
            if (!File.Exists(f)) return [];
            var list = StateJson.Load<List<Change>>(f) ?? [];
            return [.. list.Where(c => c is { File: not null, Action: not null } )];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveAll(string kbRoot, List<Change> list)
    {
        Directory.CreateDirectory(kbRoot);
        StateJson.Save(PathFor(kbRoot), list);
    }

    public static void Record(string kbRoot, string relSlash, string action, string hash)
    {
        var list = Load(kbRoot).Where(e => e.File != relSlash).ToList();
        list.Add(new Change(relSlash, action, LogWriter.Now, hash));
        SaveAll(kbRoot, list);
    }
}
