using System.IO;

namespace LLM_Wiki.Services;

/// <summary>
/// 知识库生命周期：列表 / 建库骨架（含场景模板）/ 删库（spec D7 语义）。
/// 依赖注入 config.WikisDir（来自 ConfigService），不自己读配置。
/// </summary>
public class KbService(ConfigService config)
{
    private string WikisDir => config.WikisDir;
    // 新建库的骨架目录（对照 Demo server.js:51-55，新增 raw/original）
    private static readonly string[] SkeletonDirs =
    [
        "raw/sources", "raw/assets", "raw/original",
        "wiki/entities", "wiki/concepts", "wiki/sources",
        "wiki/queries", "wiki/synthesis", "wiki/comparisons",
    ];

    /// <summary>现存库名列表（目录即库，跳过隐藏目录）。</summary>
    public IReadOnlyList<string> ListKbs()
    {
        if (!Directory.Exists(config.WikisDir)) return [];
        return Directory
            .EnumerateDirectories(config.WikisDir)
            .Select(d => Path.GetFileName(d))
            .Where(n => !n.StartsWith('.') && PathGuard.ValidKb(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>建库：校验名 → 骨架目录 + 默认文件 + .changes.json。已存在则补缺失骨架（幂等，同 Demo writeFile 存在即跳过）。</summary>
    public string CreateKb(string name, string scene = "通用")
    {
        if (!PathGuard.ValidKb(name))
            throw new ArgumentException("知识库名只允许中英文、数字、下划线、中划线", nameof(name));

        var root = Path.Combine(config.WikisDir, name);
        foreach (var d in SkeletonDirs) Directory.CreateDirectory(Path.Combine(root, d));

        // 场景模板：只影响 purpose/schema 文案（spec 02 §5）
        var (purpose, schema) = SceneTemplates.Get(name, scene, DateTime.Today.ToString("yyyy-MM-dd"));
        var files = new Dictionary<string, string>
        {
            ["purpose.md"] = purpose + "\n",
            ["schema.md"] = schema + "\n",
            ["wiki/index.md"] = $"# {name} - 内容目录\n\n- [全局摘要](overview.md)\n- [操作历史](log.md)\n",
            ["wiki/log.md"] = $"# 操作历史\n\n- {DateTime.Today:yyyy-MM-dd} 创建知识库 {name}\n",
            ["wiki/overview.md"] = $"# {name} - 全局摘要\n\n（Ingest 文档后自动更新）\n",
        };
        foreach (var (rel, content) in files)
        {
            var p = Path.Combine(root, rel);
            if (!File.Exists(p)) File.WriteAllText(p, content);
        }

        // 待处理变更清单初始化（点号开头，目录树跳过）
        var changes = Path.Combine(root, ".changes.json");
        if (!File.Exists(changes)) File.WriteAllText(changes, "[]");
        return root;
    }

    /// <summary>删库前校验：名合法 + 目录存在 + 存储路径可达（UNC 掉线拒绝，spec Review Focus 1）。</summary>
    public (bool ok, string error) ValidateDelete(string name)
    {
        if (!PathGuard.ValidKb(name)) return (false, "知识库名非法");
        try { _ = ListKbs(); }
        catch (Exception e) { return (false, "存储路径不可达，禁止删除：" + e.Message); }
        var root = Path.Combine(config.WikisDir, name);
        if (!Directory.Exists(root)) return (false, "知识库不存在");
        return (true, "");
    }

    /// <summary>整库永久删除（含 raw/original/ 原件；调用方负责 UI 输入库名确认，spec D7）。</summary>
    public void DeleteKb(string name)
    {
        var (ok, error) = ValidateDelete(name);
        if (!ok) throw new InvalidOperationException(error);
        Directory.Delete(Path.Combine(config.WikisDir, name), true);
    }
}
