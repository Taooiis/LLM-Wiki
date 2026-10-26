using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmWiki.App.Services;

/// <summary>待处理变更条目；字段小写序列化（Demo .changes.json 形状 + hash 指纹，spec 02 §7）。</summary>
public record Change(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("time")] string Time,
    [property: JsonPropertyName("hash")] string Hash);

/// <summary>
/// .changes.json 读写：同文件去重（一次动作覆盖同一文件旧条目），UploadService 与 FileBridge 共用。
/// </summary>
public static class ChangeStore
{
    private static string PathFor(string wikisDir, string kb) =>
        Path.Combine(wikisDir, kb, ".changes.json");

    public static List<Change> Load(string wikisDir, string kb)
    {
        try
        {
            var f = PathFor(wikisDir, kb);
            if (!File.Exists(f)) return [];
            return JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(f)) ?? [];
        }
        catch { return []; } // 坏文件回退空（同 Demo loadChanges）
    }

    public static void Save(string wikisDir, string kb, List<Change> list)
    {
        Directory.CreateDirectory(Path.Combine(wikisDir, kb));
        File.WriteAllText(PathFor(wikisDir, kb), JsonSerializer.Serialize(list));
    }

    /// <summary>覆盖保存（给 ClearChanges 的差集结果用）。</summary>
    public static void SaveAll(string wikisDir, string kb, List<Change> list) => Save(wikisDir, kb, list);

    /// <summary>记录一条变更（同文件覆盖旧记录，照 Demo recordChange）。</summary>
    public static void Record(string wikisDir, string kb, string relSlash, string action, string hash)
    {
        var list = Load(wikisDir, kb).Where(e => e.File != relSlash).ToList();
        list.Add(new Change(relSlash, action, LogWriter.Now, hash));
        Save(wikisDir, kb, list);
    }
}
