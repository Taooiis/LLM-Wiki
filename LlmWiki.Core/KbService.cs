using System.IO;

namespace LLM_Wiki.Services;

/// <summary>
/// 知识库生命周期：列表 / 建库骨架（含场景模板）/ 删库。
/// 新建库不创建 raw/original；已有历史目录只兼容保留，不自动迁移。
/// </summary>
public class KbService(ConfigService config, ReviewService reviews)
{
    private static readonly string[] SkeletonDirs =
    [
        "raw/sources", "raw/assets",
        "wiki/entities", "wiki/concepts", "wiki/sources",
        "wiki/queries", "wiki/synthesis", "wiki/comparisons",
        ".llmwiki",
    ];

    public IReadOnlyList<string> ListKbs() => ListKbsWithError().Kbs;

    /// <summary>区分“没有知识库”和“根目录不可访问”，后者不能冒充无库引导。</summary>
    public (IReadOnlyList<string> Kbs, string Error) ListKbsWithError()
    {
        if (!Directory.Exists(config.WikisDir))
            return ([], "知识库根目录不存在或不可访问：" + config.WikisDir);
        try
        {
            var list = Directory
                .EnumerateDirectories(config.WikisDir)
                .Select(d => Path.GetFileName(d))
                .Where(n => !n.StartsWith('.') && PathGuard.ValidKb(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            return (list, "");
        }
        catch (Exception e)
        {
            return ([], "读取知识库根目录失败：" + e.Message);
        }
    }

    public bool KbExists(string? kb)
        => !string.IsNullOrWhiteSpace(kb)
           && PathGuard.ValidKb(kb)
           && Directory.Exists(Path.Combine(config.WikisDir, kb));

    public string KbRoot(string kb) => Path.GetFullPath(Path.Combine(config.WikisDir, kb));

    /// <summary>建库：校验名 → 骨架 + 默认文件 + 初始状态。重名抛 <see cref="KbExistsException"/>，不改动已有库。</summary>
    public string CreateKb(string name, string scene = "通用", string? purposeOverride = null)
    {
        if (!PathGuard.ValidKb(name))
            throw new ArgumentException("知识库名只允许中英文、数字、下划线、中划线", nameof(name));

        var root = Path.Combine(config.WikisDir, name);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new KbExistsException(name);

        foreach (var d in SkeletonDirs) Directory.CreateDirectory(Path.Combine(root, d));

        var (purpose, schema) = SceneTemplates.Get(name, scene, DateTime.Today.ToString("yyyy-MM-dd"));
        if (!string.IsNullOrWhiteSpace(purposeOverride)) purpose = purposeOverride.Trim();
        var index = $"# {name} - 内容目录\n\n- [全局摘要](overview.md)\n- [操作历史](log.md)\n";
        var overview = $"# {name} - 全局摘要\n\n（整理资料后自动更新）\n";
        var files = new Dictionary<string, string>
        {
            ["purpose.md"] = purpose + "\n",
            ["schema.md"] = schema + "\n",
            ["wiki/index.md"] = index,
            ["wiki/log.md"] = $"# 操作历史\n\n- {DateTime.Today:yyyy-MM-dd} 创建知识库 {name}\n",
            ["wiki/overview.md"] = overview,
        };
        foreach (var (rel, content) in files)
        {
            var p = Path.Combine(root, rel);
            if (!File.Exists(p)) File.WriteAllText(p, content, new System.Text.UTF8Encoding(false));
        }

        var changes = Path.Combine(root, ".changes.json");
        if (!File.Exists(changes)) File.WriteAllText(changes, "[]");

        var paths = new KbPaths(root);
        paths.EnsureStateDirs();
        StateJson.Save(paths.KbMeta, new KbMeta
        {
            SchemaVersion = 1,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            IndexTemplateHash = HashUtil.Sha256Text(index),
            OverviewTemplateHash = HashUtil.Sha256Text(overview),
        });
        reviews.InitializeIfMissing(name);
        return root;
    }

    public (bool ok, string error) ValidateDelete(string name)
    {
        if (!PathGuard.ValidKb(name)) return (false, "知识库名非法");
        try { _ = ListKbs(); }
        catch (Exception e) { return (false, "存储路径不可达，禁止删除：" + e.Message); }
        var root = Path.Combine(config.WikisDir, name);
        if (!Directory.Exists(root)) return (false, "知识库不存在");
        return (true, "");
    }

    /// <summary>整库永久删除（含隐藏状态与回收站）。调用方负责输入库名、停止服务与草稿保护。</summary>
    public void DeleteKb(string name)
    {
        var (ok, error) = ValidateDelete(name);
        if (!ok) throw new InvalidOperationException(error);
        Directory.Delete(Path.Combine(config.WikisDir, name), true);
    }
}

public sealed class KbExistsException(string name) : Exception($"这个知识库已存在：{name}");
