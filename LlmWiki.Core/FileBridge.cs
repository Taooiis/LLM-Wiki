using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>目录树节点；字段小写与 Demo /api/tree JSON 形状一致。RelPath 为相对库根的路径（本地构建，非 JSON 必需）。</summary>
public record TreeItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("children")] TreeItem[]? Children,
    [property: JsonPropertyName("rel")] string? RelPath = null);

/// <summary>
/// 源文件条目：文件名 + 大小 + 修改时间（源文档页列表行元数据，设计 02§3.2）。
/// 含展示格式化与类型筛选判断，避免 XAML 侧再堆 converter。
/// </summary>
public record SourceEntry(string Name, long Size, DateTime Modified)
{
    /// <summary>类型标签（白名单内源文档只有 md/txt 两类）。</summary>
    public string TypeText => Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "Markdown" : "文本";

    /// <summary>大小展示文本（如 4.2 KB）。</summary>
    public string SizeText => Size < 1024 ? $"{Size} B"
        : Size < 1024 * 1024 ? $"{Size / 1024.0:0.#} KB"
        : $"{Size / 1024.0 / 1024.0:0.#} MB";

    /// <summary>修改时间展示文本（今天/昨天显示相对时间，更早显示日期）。</summary>
    public string ModifiedText
    {
        get
        {
            var today = DateTime.Today;
            var day = Modified.Date;
            return day == today ? $"今天 {Modified:HH:mm}"
                : day == today.AddDays(-1) ? $"昨天 {Modified:HH:mm}"
                : Modified.ToString("MM-dd HH:mm");
        }
    }

    /// <summary>类型筛选匹配（"全部"以外的筛选键直接对比类型标签）。</summary>
    public bool MatchesType(string filter) =>
        filter is null || filter == "全部" || filter == TypeText;
}

/// <summary>
/// 文件桥：目录树 / md·txt 读写 / 删除 / 源列表 / 变更清单。
/// 只服务纯文本文件；office 上传删除归 UploadService（成对语义，spec 02 §4）。
/// </summary>
public class FileBridge(ConfigService config)
{
    private string WikisDir => config.WikisDir;
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>库根目录完整路径（显示与跨页共享用；Task 6 起根目录来源切到 ConfigService）。</summary>
    public string KbRoot(string kb) => Path.GetFullPath(Path.Combine(config.WikisDir, kb));

    /// <summary>递归目录树：目录在前按名排序；跳过点号开头与 node_modules；wiki 区只认 md/txt，raw/original 下原件全认（spec 02 §4）。RelPath 相对库根。</summary>
    public TreeItem[] Tree(string kb) => TreeDir(Path.Combine(config.WikisDir, kb), 0, inOriginal: false, rootDir: Path.Combine(config.WikisDir, kb));

    private TreeItem[] TreeDir(string dir, int depth, bool inOriginal, string rootDir)
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
                        return new TreeItem(i.name, "dir", TreeDir(i.full, depth + 1, childInOriginal, rootDir), Path.GetRelativePath(rootDir, i.full));
                    }
                    return new TreeItem(i.name, "file", null, Path.GetRelativePath(rootDir, i.full));
                })];
        }
        catch { return []; } // 目录不存在或不可读 → 空树（同 Demo tree）
    }

    /// <summary>读 md/txt 文本。</summary>
    public string? ReadText(string kb, string rel, out string error)
    {
        error = "";
        var p = PathGuard.SafeJoin(config.WikisDir, kb, rel);
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
        => WriteTextCore(kb, rel, content, atomic: false, out error);

    /// <summary>
    /// 原子写：先写同目录临时文件再替换，失败不产生半截文件（设计 08§5.4）。
    /// 变更登记规则与 <see cref="WriteText"/> 完全一致。
    /// </summary>
    public bool WriteTextAtomic(string kb, string rel, string content, out string error)
        => WriteTextCore(kb, rel, content, atomic: true, out error);

    private bool WriteTextCore(string kb, string rel, string content, bool atomic, out string error)
    {
        error = "";
        var p = PathGuard.SafeJoin(config.WikisDir, kb, rel);
        if (p == null || rel == null || !FileHelper.IsTextFile(Path.GetFileName(p)))
        {
            error = "只允许读写 md/txt";
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        if (atomic)
        {
            var tmp = p + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tmp, content);
                File.Move(tmp, p, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
        else
        {
            File.WriteAllText(p, content);
        }

        var n = rel.Replace(Separators[0], Separators[1]);
        if (IsSourceRel(n))
        {
            LogWriter.AppendSource(config.WikisDir, kb, n, "源文档编辑保存");
            ChangeStore.Record(config.WikisDir, kb, n, "编辑", HashUtil.Sha256Text(content));
        }
        else if (n == "purpose.md" || n == "schema.md")
        {
            LogWriter.AppendSource(config.WikisDir, kb, n, "配置更新", "");
        }
        return true;
    }

    /// <summary>删除 md/txt；office 路径拒绝（归 UploadService）。</summary>
    public bool Delete(string kb, string rel, out string error)
    {
        error = "";
        var p = PathGuard.SafeJoin(config.WikisDir, kb, rel);
        if (p == null || rel == null || !FileHelper.IsTextFile(Path.GetFileName(p)))
        {
            error = "只允许删除 md/txt";
            return false;
        }
        if (File.Exists(p)) File.Delete(p);

        var n = rel.Replace(Separators[0], Separators[1]);
        if (IsSourceRel(n))
        {
            LogWriter.AppendSource(config.WikisDir, kb, n, "源文档删除");
            ChangeStore.Record(config.WikisDir, kb, n, "删除", "");
        }
        return true;
    }

    /// <summary>源列表：raw/sources/ 下 md/txt（office 副本为 .docx.md 形式，自然在列）。</summary>
    public string[] Sources(string kb)
    {
        var dir = Path.Combine(config.WikisDir, kb, "raw", "sources");
        return Directory.Exists(dir)
            ? [.. Directory.GetFiles(dir).Where(f => FileHelper.IsTextFile(Path.GetFileName(f))).Select(Path.GetFileName)!]
            : [];
    }

    /// <summary>源条目列表（含元数据，按文件名稳定排序）：raw/sources/ 下 md/txt。</summary>
    public SourceEntry[] SourceEntries(string kb)
    {
        var dir = Path.Combine(config.WikisDir, kb, "raw", "sources");
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.GetFiles(dir)
            .Where(f => FileHelper.IsTextFile(Path.GetFileName(f)))
            .Select(f =>
            {
                var fi = new FileInfo(f);
                return new SourceEntry(fi.Name, fi.Length, fi.LastWriteTime);
            })
            .OrderBy(e => e.Name, StringComparer.Ordinal)];
    }

    public List<Change> Changes(string kb) => ChangeStore.Load(config.WikisDir, kb);

    /// <summary>标记已处理：按 file 清单清除；空清单清空全部（同 Demo /api/changes/clear）。</summary>
    public void ClearChanges(string kb, string[] files)
    {
        var list = ChangeStore.Load(config.WikisDir, kb);
        list = files.Length == 0 ? [] : [.. list.Where(e => !files.Contains(e.File))];
        ChangeStore.SaveAll(config.WikisDir, kb, list);
    }

    // raw/sources/ 判断（照 server.js:82-85）
    public static bool IsSourceRel(string rel) => rel == "raw/sources" || rel.StartsWith("raw/sources/");
}
