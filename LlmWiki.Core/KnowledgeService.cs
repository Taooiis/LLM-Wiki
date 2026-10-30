using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LLM_Wiki.Services;

/// <summary>知识页服务：wiki/ 目录树、默认页、空知识判定、链接解析与高级手动修订。</summary>
public sealed partial class KnowledgeService(ConfigService config, ReviewService reviews, SourceService sources)
{
    private static readonly Dictionary<string, string> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["entities"] = "实体",
        ["concepts"] = "概念",
        ["sources"] = "资料摘要",
        ["queries"] = "问题分析",
        ["synthesis"] = "综合分析",
        ["comparisons"] = "对比分析",
    };

    public IReadOnlyList<KnowledgeNode> Tree(string kb)
    {
        var paths = Paths(kb);
        return Directory.Exists(paths.Wiki) ? BuildDir(paths.Wiki, paths.Root, 0) : [];
    }

    private static IReadOnlyList<KnowledgeNode> BuildDir(string dir, string kbRoot, int depth)
    {
        if (depth > 8) return [];
        List<(string name, bool isDir, string full)> entries;
        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(dir)
                .Select(p => (Path.GetFileName(p), Directory.Exists(p), p))
                .Where(e => e.Item1.Length > 0 && !e.Item1.StartsWith('.')
                            && !e.Item1.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Item1, e.Item2, e.p))];
        }
        catch
        {
            return [];
        }

        var nodes = new List<KnowledgeNode>();
        foreach (var (name, isDir, full) in entries
                     .OrderBy(e => e.isDir ? 0 : 1)
                     .ThenBy(e => e.name, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(kbRoot, full).Replace('\\', '/');
            if (isDir)
            {
                var alias = CategoryAliases.GetValueOrDefault(rel["wiki/".Length..], name);
                nodes.Add(new KnowledgeNode(name, rel, true, alias, BuildDir(full, kbRoot, depth + 1), 0));
            }
            else
            {
                // 日志是整理页的操作记录，不作为普通知识内容出现
                if (name.Equals("log.md", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SourceRules.IsText(name)) continue;
                var size = 0L;
                try { size = new FileInfo(full).Length; } catch { }
                nodes.Add(new KnowledgeNode(name, rel, false, name, [], size));
            }
        }
        return nodes;
    }

    public string DefaultPage(string kb)
    {
        var paths = Paths(kb);
        if (File.Exists(Path.Combine(paths.Wiki, "index.md"))) return "wiki/index.md";
        if (File.Exists(Path.Combine(paths.Wiki, "overview.md"))) return "wiki/overview.md";
        return "";
    }

    public bool TryRead(string kb, string rel, out string content, out string hash, out string error)
    {
        content = "";
        hash = "";
        error = "";
        var paths = Paths(kb);
        var normalized = ChangeService.Normalize(rel);
        if (!normalized.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase))
        {
            error = "只允许读取知识文件";
            return false;
        }
        if (normalized.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase))
        {
            error = "操作日志请在整理页查看";
            return false;
        }
        var full = Path.Combine(paths.Root, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            error = "页面不存在";
            return false;
        }
        try
        {
            var bytes = File.ReadAllBytes(full);
            content = FileHelper.DecodeText(bytes);
            hash = HashUtil.Sha256(full);
            return true;
        }
        catch (Exception e)
        {
            error = "读取失败：" + e.Message;
            return false;
        }
    }

    /// <summary>知识是否仍只有初始骨架（按建库时记录的模板哈希判定），没有可靠记录时保守展示内容。</summary>
    public KnowledgeState GetState(string kb)
    {
        var paths = Paths(kb);
        var meta = LoadMeta(paths);
        var hasSources = sources.List(kb).Count > 0;
        if (!Directory.Exists(paths.Wiki)) return new KnowledgeState(KnowledgeStateKind.Empty, hasSources);
        if (meta == null || string.IsNullOrEmpty(meta.IndexTemplateHash))
            return new KnowledgeState(HasAnyPage(paths) ? KnowledgeStateKind.Content : KnowledgeStateKind.Empty, hasSources);

        foreach (var file in Directory.EnumerateFiles(paths.Wiki, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(paths.Wiki, file).Replace('\\', '/');
            if (rel.Equals("index.md", StringComparison.OrdinalIgnoreCase)
                || rel.Equals("overview.md", StringComparison.OrdinalIgnoreCase)
                || rel.Equals("log.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (SourceRules.IsText(Path.GetFileName(file)))
                return new KnowledgeState(KnowledgeStateKind.Content, hasSources);
        }

        var indexOk = File.Exists(Path.Combine(paths.Wiki, "index.md"))
                      && HashUtil.Sha256(Path.Combine(paths.Wiki, "index.md")) == meta.IndexTemplateHash;
        var overviewOk = File.Exists(Path.Combine(paths.Wiki, "overview.md"))
                         && HashUtil.Sha256(Path.Combine(paths.Wiki, "overview.md")) == meta.OverviewTemplateHash;
        return new KnowledgeState(
            indexOk || overviewOk ? KnowledgeStateKind.Skeleton : KnowledgeStateKind.Content,
            hasSources);
    }

    private static bool HasAnyPage(KbPaths paths)
    {
        try
        {
            return Directory.EnumerateFiles(paths.Wiki, "*", SearchOption.AllDirectories)
                .Any(f => SourceRules.IsText(Path.GetFileName(f))
                          && !Path.GetFileName(f).Equals("log.md", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>高级手动修订：只允许 wiki/ 下 md/txt，日志只读；版本不一致拒绝覆盖。</summary>
    public ManualRevisionResult SaveManualRevision(string kb, string rel, string content, string? expectedHash)
    {
        var paths = Paths(kb);
        var normalized = ChangeService.Normalize(rel);
        if (!normalized.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase)
            || !SourceRules.IsText(normalized)
            || normalized.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase))
            return new ManualRevisionResult(false, "", "此文件不允许手动修订");

        var full = Path.Combine(paths.Root, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return new ManualRevisionResult(false, "", "页面不存在");

        var current = HashUtil.Sha256(full);
        if (!string.IsNullOrEmpty(expectedHash)
            && !string.Equals(current, expectedHash, StringComparison.OrdinalIgnoreCase))
            return new ManualRevisionResult(false, current, "文件已被其他程序修改，请查看磁盘版本后重试");

        try
        {
            var tmp = full + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
            File.Move(tmp, full, overwrite: true);
            var hash = HashUtil.Sha256(full);
            reviews.MarkManualRevision(kb, normalized, hash);
            LogWriter.Append(config.WikisDir, kb, $"- {LogWriter.Now} 手动修订知识：{normalized}");
            return new ManualRevisionResult(true, hash, "");
        }
        catch (Exception e)
        {
            return new ManualRevisionResult(false, current, "保存失败：" + e.Message);
        }
    }

    /// <summary>解析 [[页面名]]：唯一命中返回路径，多候选返回候选，缺失返回空。</summary>
    public LinkTarget ResolveWikiLink(string kb, string fromRel, string target)
    {
        var paths = Paths(kb);
        var name = target.Trim();
        if (name.Length == 0) return new LinkTarget("", []);
        var candidates = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(paths.Wiki, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!fileName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && !fileName.Equals(name, StringComparison.Ordinal)) continue;
                var rel = Path.GetRelativePath(paths.Root, file).Replace('\\', '/');
                candidates.Add(rel);
            }
        }
        catch { }
        return new LinkTarget(candidates.Count == 1 ? candidates[0] : "", candidates);
    }

    public string[] SourceNamesIn(string content)
    {
        var result = new List<string>();
        foreach (Match m in SourceRefRegex().Matches(content))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.Add(name);
        }
        return [.. result];
    }

    public KbMeta? LoadMeta(KbPaths paths)
    {
        try { return StateJson.Load<KbMeta>(paths.KbMeta); }
        catch { return null; }
    }

    private KbPaths Paths(string kb) => new(Path.Combine(config.WikisDir, kb));

    [GeneratedRegex(@"\[来源[:：]\s*([^\]]+)\]")]
    private static partial Regex SourceRefRegex();
}

public enum KnowledgeStateKind { Empty, Skeleton, Content }

public sealed record KnowledgeState(KnowledgeStateKind Kind, bool HasSources);

public sealed record KnowledgeNode(string Name, string RelPath, bool IsFolder, string DisplayName, IReadOnlyList<KnowledgeNode> Children, long Size);

public sealed record LinkTarget(string Resolved, IReadOnlyList<string> Candidates);

public sealed record ManualRevisionResult(bool Success, string Hash, string Message);

public sealed class KbMeta
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("indexTemplateHash")] public string IndexTemplateHash { get; set; } = "";
    [JsonPropertyName("overviewTemplateHash")] public string OverviewTemplateHash { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
