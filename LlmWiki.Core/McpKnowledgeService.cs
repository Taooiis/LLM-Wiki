using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using ModelContextProtocol;

namespace LLM_Wiki.Services;

/// <summary>
/// MCP 只读/写入能力实现（设计 08§5）：HTTP 与 stdio 共用，不各自拼路径或读写文件。
/// </summary>
public sealed class McpKnowledgeService(
    ConfigService config,
    FileBridge fileBridge,
    KbService kbService,
    WorkflowPromptService prompts,
    McpAccessPolicy policy,
    McpServerContext context)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int SearchLimitDefault = 20;
    private const int SearchLimitMax = 50;
    private const int SearchSnippetLength = 200;
    private const int SearchFileCap = 5000;
    private const int IndexEntryCap = 500;

    public string CurrentKb => context.CurrentKb;

    public string GetVersion(string content) => McpAccessPolicy.ContentVersion(content);

    public string ListKnowledgeBases()
    {
        var kbs = kbService.ListKbs();
        var list = kbs.Select(kb => new McpKbInfo(kb, kb, kb == context.CurrentKb)).ToArray();
        return JsonSerializer.Serialize(new { knowledge_bases = list }, JsonOptions);
    }

    public string ListTree(string? kb, string? relative_dir)
    {
        var dir = policy.ResolveDirectory(kb, relative_dir, out var code);
        if (dir == null) throw Error(code, "目录不可访问");

        var root = Path.Combine(config.WikisDir, kb!);
        var inOriginal = IsUnderRawOriginal(root, dir);
        var entries = new List<McpTreeEntry>();

        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isDir = Directory.Exists(path);
            if (!isDir)
            {
                var readable = IsReadableName(name);
                if (!readable && !inOriginal) continue;
            }

            var rel = McpAccessPolicy.ToDisplayPath(Path.GetRelativePath(root, path));
            entries.Add(new McpTreeEntry(name, isDir ? "dir" : "file", rel, isDir || !IsWritable(rel)));
        }

        var ordered = entries
            .OrderBy(e => e.Type != "dir")
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
        return JsonSerializer.Serialize(new { kb, relative_dir = relative_dir ?? "", entries = ordered }, JsonOptions);
    }

    public string ReadFile(string? kb, string? relative_path)
    {
        var full = policy.ResolveReadPath(kb, relative_path, out var code);
        if (full == null) throw Error(code, "路径不可读");
        if (!File.Exists(full)) throw Error(McpErrorCodes.FileNotFound, "文件不存在");

        var info = new FileInfo(full);
        if (info.Length > McpAccessPolicy.MaxReadBytes)
            throw Error(McpErrorCodes.FileTooLarge, $"文件超过 {McpAccessPolicy.MaxReadBytes / 1024 / 1024} MB 上限");

        string content;
        try
        {
            content = File.ReadAllText(full);
        }
        catch (Exception e)
        {
            throw Error(McpErrorCodes.PathNotAllowed, "文件无法读取：" + e.Message);
        }

        var rel = McpAccessPolicy.ToDisplayPath(relative_path!);
        var result = new McpFileContent(rel, content, "utf-8", false, info.Length, GetVersion(content));
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public string SearchContent(string? kb, string? query, int limit = SearchLimitDefault)
    {
        if (!policy.KbExists(kb)) throw Error(McpErrorCodes.KbNotFound, "知识库不存在");
        if (string.IsNullOrWhiteSpace(query)) throw Error(McpErrorCodes.PathNotAllowed, "搜索关键词不能为空");

        var effectiveLimit = Math.Clamp(limit, 1, SearchLimitMax);
        var root = Path.Combine(config.WikisDir, kb!);
        var matches = new List<McpSearchMatch>();
        var scanned = 0;

        foreach (var dir in new[] { Path.Combine(root, "wiki"), Path.Combine(root, "raw", "sources") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in EnumerateFilesSafe(dir))
            {
                if (matches.Count >= effectiveLimit || scanned >= SearchFileCap) break;
                scanned++;
                if (!IsReadableName(Path.GetFileName(file))) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > McpAccessPolicy.MaxReadBytes) continue;
                    var text = File.ReadAllText(file);
                    var index = text.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    var snippet = Snippet(text, index, query.Trim().Length);
                    matches.Add(new McpSearchMatch(McpAccessPolicy.ToDisplayPath(Path.GetRelativePath(root, file)), snippet));
                }
                catch
                {
                    // 单个文件不可读不影响整体搜索
                }
            }
        }

        return JsonSerializer.Serialize(
            new { kb, query, limit = effectiveLimit, matches = matches.ToArray(), scanned_files = scanned },
            JsonOptions);
    }

    public string GetPendingChanges(string? kb)
    {
        if (!policy.KbExists(kb)) throw Error(McpErrorCodes.KbNotFound, "知识库不存在");
        var changes = ChangeStore.Load(config.WikisDir, kb!)
            .Select(c => new McpPendingChange(McpAccessPolicy.ToDisplayPath(c.File), c.Action, c.Time))
            .ToArray();
        return JsonSerializer.Serialize(new { kb, changes }, JsonOptions);
    }

    public string WriteWikiFile(string? kb, string? relative_path, string? content, string? expected_version = null)
    {
        var full = policy.ResolveWritePath(kb, relative_path, out var code);
        if (full == null) throw Error(code, "当前档位或路径不允许写入");
        if (content == null) throw Error(McpErrorCodes.PathNotAllowed, "写入内容不能为空");
        if (content.Length > McpAccessPolicy.MaxWriteChars)
            throw Error(McpErrorCodes.FileTooLarge, "写入内容超过上限");

        var exists = File.Exists(full);
        if (!string.IsNullOrWhiteSpace(expected_version))
        {
            var current = exists ? McpAccessPolicy.FileVersion(full) : "";
            if (!string.Equals(current, expected_version.Trim(), StringComparison.OrdinalIgnoreCase))
                throw Error(McpErrorCodes.VersionConflict, "文件已被修改，请重新读取后再写");
        }

        try
        {
            if (!fileBridge.WriteTextAtomic(kb!, relative_path!, content, out var error))
                throw Error(McpErrorCodes.WriteNotAllowedPath, error);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw Error(McpErrorCodes.ServiceUnavailable, "写入失败：" + e.Message);
        }

        var rel = McpAccessPolicy.ToDisplayPath(relative_path!);
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var result = new McpWriteResult(rel, GetVersion(content), now, FileBridge.IsSourceRel(rel));
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public string BuildBoundaryPrompt()
    {
        var kb = RequireCurrentKb();
        return prompts.BuildBoundary(kb, TargetLabel(kb));
    }

    public string BuildBatchIngestPrompt(string? files)
    {
        var kb = RequireCurrentKb();
        var parsed = WorkflowPromptService.ParseFiles(files ?? "");
        return prompts.BuildIngest(TargetLabel(kb), false, parsed);
    }

    public string BuildIncrementalIngestPrompt()
    {
        var kb = RequireCurrentKb();
        return prompts.BuildIngest(TargetLabel(kb), true);
    }

    public string BuildQueryPrompt(string question)
    {
        var kb = RequireCurrentKb();
        return prompts.BuildQuery(TargetLabel(kb), question);
    }

    public string BuildLintPrompt()
    {
        var kb = RequireCurrentKb();
        return prompts.BuildLint(TargetLabel(kb));
    }

    public string BuildComparePrompt(string topic)
    {
        var kb = RequireCurrentKb();
        return prompts.BuildCompare(TargetLabel(kb), topic);
    }

    public string BuildCleanPrompt()
    {
        var kb = RequireCurrentKb();
        return prompts.BuildClean(TargetLabel(kb));
    }

    /// <summary>目录索引（Resource wiki://{kb}/index）：递归列出条目，限制条数。</summary>
    public string BuildIndex(string? kb = null)
    {
        kb = string.IsNullOrWhiteSpace(kb) ? RequireCurrentKb() : kb;
        if (!policy.KbExists(kb)) throw Error(McpErrorCodes.KbNotFound, "知识库不存在");
        var root = Path.Combine(config.WikisDir, kb);
        var lines = new List<string> { $"# {kb} 目录索引", "" };
        Walk(root, "", lines, IndexEntryCap);
        return string.Join("\n", lines);
    }

    public string ReadResourceFile(string? kb, string? relativePath)
    {
        var full = policy.ResolveReadPath(kb, relativePath, out var code);
        if (full == null) throw Error(code, "资源不可读");
        if (!File.Exists(full)) throw Error(McpErrorCodes.FileNotFound, "资源不存在");
        if (new FileInfo(full).Length > McpAccessPolicy.MaxReadBytes)
            throw Error(McpErrorCodes.FileTooLarge, "资源超过大小上限");
        return File.ReadAllText(full);
    }

    public string RequireCurrentKb()
    {
        if (string.IsNullOrWhiteSpace(context.CurrentKb))
            throw Error(McpErrorCodes.KbNotFound, "尚未选择知识库");
        return context.CurrentKb;
    }

    private static string TargetLabel(string kb) => $"知识库「{kb}」（通过 MCP 工具访问）";

    private bool IsWritable(string rel)
    {
        var normalized = McpAccessPolicy.NormalizeRelative(rel, out _);
        if (normalized == null) return false;
        var n = normalized.Replace('\\', '/');
        if (!n.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase)) return false;
        if (n.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase)) return false;
        return n.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReadableName(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRawOriginal(string root, string dir)
    {
        var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
        return rel.Equals("raw/original", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("raw/original/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subDirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToArray();
                subDirs = Directory.EnumerateDirectories(dir).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var f in files) yield return f;
            foreach (var d in subDirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                stack.Push(d);
            }
        }
    }

    private static string Snippet(string text, int index, int queryLength)
    {
        var start = Math.Max(0, index - 80);
        var length = Math.Min(SearchSnippetLength, text.Length - start);
        var snippet = text.Substring(start, length)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        return start > 0 ? "…" + snippet : snippet;
    }

    private static void Walk(string dir, string prefix, List<string> lines, int cap)
    {
        if (lines.Count >= cap) return;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir).OrderBy(e => File.Exists(e)).ThenBy(e => e, StringComparer.Ordinal);
        }
        catch
        {
            return;
        }

        foreach (var path in entries)
        {
            if (lines.Count >= cap) return;
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            var isDir = Directory.Exists(path);
            if (!isDir && !IsReadableName(name)) continue;
            lines.Add(isDir ? $"- {prefix}{name}/" : $"- {prefix}{name}");
            if (isDir)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                Walk(path, prefix + name + "/", lines, cap);
            }
        }
    }

    private static McpException Error(string code, string message) => new($"{code}: {message}");

    private sealed record McpKbInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("current")] bool Current);

    private sealed record McpTreeEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("read_only")] bool ReadOnly);

    private sealed record McpFileContent(
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("encoding")] string Encoding,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("version")] string Version);

    private sealed record McpSearchMatch(
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("snippet")] string Snippet);

    private sealed record McpPendingChange(
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("change_type")] string ChangeType,
        [property: JsonPropertyName("time")] string Time);

    private sealed record McpWriteResult(
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("written_at")] string WrittenAt,
        [property: JsonPropertyName("recorded_as_change")] bool RecordedAsChange);
}
