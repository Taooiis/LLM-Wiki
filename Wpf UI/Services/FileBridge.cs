using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>目录树节点；字段小写与 Demo /api/tree JSON 形状一致。</summary>
public record TreeItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("children")] TreeItem[]? Children);

/// <summary>
/// 文件桥：目录树 / md·txt 读写 / 删除 / 源列表 / 变更清单。
/// 只服务纯文本文件；office 上传删除归 UploadService（成对语义，spec 02 §4）。
/// </summary>
public class FileBridge(string wikisDir)
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>递归目录树：目录在前按名排序；跳过点号开头与 node_modules；wiki 区只认 md/txt，raw/original 下原件全认（spec 02 §4）。</summary>
    public TreeItem[] Tree(string kb) => TreeDir(Path.Combine(wikisDir, kb), 0, inOriginal: false);

    private TreeItem[] TreeDir(string dir, int depth, bool inOriginal)
    {
        if (depth > 6) return [];
        try
        {
            var entries = System.IO.Directory.EnumerateFileSystemEntries(dir).ToArray();
            var items = new List<(string name, string type, string full)>();
            foreach (var e in entries)
            {
                var name = Path.GetFileName(e);
                if (name.StartsWith('.') || name == "node_modules") continue;
                if (System.IO.Directory.Exists(e))
                {
                    // 进入 raw/original 后所有文件（office 原件）照常入树
                    var childInOriginal = inOriginal
                        || name.Equals("original", StringComparison.OrdinalIgnoreCase)
                            && dir.EndsWith(Path.Combine("raw"), StringComparison.OrdinalIgnoreCase);
                    items.Add((name, "dir", e));
                    // 递归在下一行统一处理（重新列目录），此处只登记
                    items[^1] = items[^1] with { };
                }
                else if (inOriginal || FileHelper.IsTextFile(name))
                {
                    items.Add((name, "file", e));
                }
            }
            return [.. items
                .OrderBy(i => i.type != "dir")
                .ThenBy(i => i.name, StringComparer.Ordinal)
                .Select(i =>
                {
                    if (i.type == "dir")
                    {
                        var childInOriginal = inOriginal
                            || (Path.GetFileName(i.full.TrimEnd(Path.DirectorySeparatorChar)) ?? "").Equals("original", StringComparison.OrdinalIgnoreCase)
                                && Path.GetFileName(Path.GetDirectoryName(i.full) ?? "").Equals("raw", StringComparison.OrdinalIgnoreCase);
                        return new TreeItem(i.name, "dir", TreeDir(i.full, depth + 1, childInOriginal));
                    }
                    return new TreeItem(i.name, "file", null);
                })];
        }
        catch { return []; } // 目录不存在或不可读 → 空树（同 Demo tree）
    }

    /// <summary>读 md/txt 文本。</summary>
    public string? ReadText(string kb, string rel, out string error)
    {
        error = "";
        var p = PathGuard.SafeJoin(wikisDir, kb, rel);
        if (p == null || !FileHelper.IsTextFile(Path.GetFileName(p)))
        {
            error = "只允许读写 md/txt";
            return null;
        }
        if (!File.Exists(p))
        {
            error = "文件不存在";
            return null;
        }
        return File.ReadAllText(p);
    }

    /// <summary>写 md/txt；source 范围动作记 log 与变更（带指纹），purpose/schema 走配置更新（spec 02 §7）。</summary>
    public bool WriteText(string kb, string rel, string content, out string error)
    {
        error = "";
        var p = PathGuard.SafeJoin(wikisDir, kb, rel);
        if (p == null || rel == null || !FileHelper.IsTextFile(Path.GetFileName(p)))
        {
            error = "只允许读写 md/txt";
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);

        var n = rel.Replace(Separators[0], Separators[1]);
        if (IsSourceRel(n))
        {
            LogWriter.AppendSource(wikisDir, kb, n, "源文档编辑保存");
            ChangeStore.Record(wikisDir, kb, n, "编辑", HashUtil.Sha256Text(content));
        }
        else if (n == "purpose.md" || n == "schema.md")
        {
            LogWriter.AppendSource(wikisDir, kb, n, "配置更新", "");
        }
        return true;
    }

    /// <summary>删除 md/txt；office 路径拒绝（归 UploadService）。</summary>
    public bool Delete(string kb, string rel, out string error)
    {
        error = "";
        var p = PathGuard.SafeJoin(wikisDir, kb, rel);
        if (p == null || rel == null || !FileHelper.IsTextFile(Path.GetFileName(p)))
        {
            error = "只允许删除 md/txt";
            return false;
        }
        if (File.Exists(p)) File.Delete(p);

        var n = rel.Replace(Separators[0], Separators[1]);
        if (IsSourceRel(n))
        {
            LogWriter.AppendSource(wikisDir, kb, n, "源文档删除");
            ChangeStore.Record(wikisDir, kb, n, "删除", "");
        }
        return true;
    }

    /// <summary>源列表：raw/sources/ 下 md/txt（office 副本为 .docx.md 形式，自然在列）。</summary>
    public string[] Sources(string kb)
    {
        var dir = Path.Combine(wikisDir, kb, "raw", "sources");
        return Directory.Exists(dir)
            ? [.. Directory.GetFiles(dir).Where(f => FileHelper.IsTextFile(Path.GetFileName(f))).Select(Path.GetFileName)!]
            : [];
    }

    public List<Change> Changes(string kb) => ChangeStore.Load(wikisDir, kb);

    /// <summary>标记已处理：按 file 清单清除；空清单清空全部（同 Demo /api/changes/clear）。</summary>
    public void ClearChanges(string kb, string[] files)
    {
        var list = ChangeStore.Load(wikisDir, kb);
        list = files.Length == 0 ? [] : [.. list.Where(e => !files.Contains(e.File))];
        ChangeStore.SaveAll(wikisDir, kb, list);
    }

    // raw/sources/ 判断（照 server.js:82-85）
    public static bool IsSourceRel(string rel) => rel == "raw/sources" || rel.StartsWith("raw/sources/");
}
