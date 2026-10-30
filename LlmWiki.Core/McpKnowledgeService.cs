using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using ModelContextProtocol;

namespace LLM_Wiki.Services;

/// <summary>
/// MCP 能力实现：HTTP 与 stdio 共用授权、路径和版本规则。
/// 只读工具不依赖客户端先调用 boundary；写入必须显式授权并带版本前置条件。
/// </summary>
public sealed class McpKnowledgeService(
    ConfigService config,
    FileBridge fileBridge,
    KbService kbService,
    PromptComposer prompts,
    McpAccessPolicy policy,
    McpServerContext context,
    ReviewService reviews,
    SourceService sources)
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
        var kb = policy.AuthorizedKb;
        var list = string.IsNullOrWhiteSpace(kb)
            ? Array.Empty<object>()
            : [new { id = kb, name = kb, current = true }];
        return JsonSerializer.Serialize(new { knowledge_bases = list }, JsonOptions);
    }

    public string ListTree(string? kb, string? relativeDir)
    {
        var dir = policy.ResolveDirectory(kb, relativeDir, out var code);
        if (dir == null) throw Error(code, "目录不可访问");

        var root = Path.Combine(config.WikisDir, kb!);
        var entries = new List<McpTreeEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            var isDir = Directory.Exists(path);
            if (!isDir && !IsVisibleName(name, path, root)) continue;
            var rel = McpAccessPolicy.ToDisplayPath(Path.GetRelativePath(root, path));
            entries.Add(new McpTreeEntry(name, isDir ? "dir" : "file", rel, isDir || !IsWritable(rel)));
        }
        var ordered = entries.OrderBy(e => e.Type != "dir").ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(new { kb, relative_dir = relativeDir ?? "", entries = ordered }, JsonOptions);
    }

    /// <summary>读文本文件：按 64 KiB 分段，返回原字节版本、总字节、游标和完整性。</summary>
    public string ReadFile(string? kb, string? relativePath, string? version, string? cursor)
    {
        var full = policy.ResolveReadPath(kb, relativePath, out var code);
        if (full == null) throw Error(code, "路径不可读");
        if (!File.Exists(full)) throw Error(McpErrorCodes.FileNotFound, "文件不存在");

        var info = new FileInfo(full);
        if (info.Length > McpAccessPolicy.MaxReadBytes)
            throw Error(McpErrorCodes.FileTooLarge, "文件超过 20 MiB 上限");

        var fileVersion = McpAccessPolicy.FileVersion(full);
        if (!string.IsNullOrEmpty(version) && !string.Equals(version, fileVersion, StringComparison.OrdinalIgnoreCase))
            throw Error(McpErrorCodes.VersionConflict, "文件已变化，请重新读取后再继续");

        string content;
        try { content = FileHelper.DecodeText(File.ReadAllBytes(full)); }
        catch (Exception e) { throw Error(McpErrorCodes.SourceReadFailed, "文件无法读取：" + e.Message); }

        var offset = ParseCursor(cursor);
        var chunk = TakeChunk(content, offset, out var next, out var chunkBytes);
        var rel = McpAccessPolicy.ToDisplayPath(relativePath!);
        var result = new McpFileContent(
            rel,
            chunk,
            "utf-8",
            chunkBytes < Encoding.UTF8.GetByteCount(content),
            info.Length,
            fileVersion,
            info.Length,
            next,
            string.IsNullOrEmpty(next));
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>读取原格式资料内容；docx/xlsx 只在读取时临时提取，不写入知识库。</summary>
    public string ReadSourceContent(string? kb, string? relativePath, string? version, string? cursor)
    {
        var full = policy.ResolveSourcePath(kb, relativePath, out var code);
        if (full == null) throw Error(code, "资料路径不可读或格式不支持");
        if (!File.Exists(full)) throw Error(McpErrorCodes.FileNotFound, "资料不存在");

        var info = new FileInfo(full);
        if (info.Length > McpAccessPolicy.MaxReadBytes)
            throw Error(McpErrorCodes.FileTooLarge, "资料超过 20 MiB 上限");

        var fileVersion = McpAccessPolicy.FileVersion(full);
        if (!string.IsNullOrEmpty(version) && !string.Equals(version, fileVersion, StringComparison.OrdinalIgnoreCase))
            throw Error(McpErrorCodes.VersionConflict, "资料已变化，请重新读取后再继续");

        var name = Path.GetFileName(full);
        var format = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var warnings = new List<string>();
        string content;
        try
        {
            if (SourceRules.IsText(name))
            {
                content = FileHelper.DecodeText(File.ReadAllBytes(full));
            }
            else if (format is "docx" or "docm")
            {
                var (text, error) = OfficeConverter.ConvertDocx(name, File.ReadAllBytes(full));
                content = text ?? throw new InvalidDataException(error);
                if (content.Contains("转换损失", StringComparison.Ordinal))
                    warnings.Add("文档包含未完全提取的结构，请查看内容开头的转换损失说明。");
            }
            else if (format is "xlsx" or "xlsm")
            {
                var (text, error) = OfficeConverter.ConvertXlsx(name, File.ReadAllBytes(full));
                content = text ?? throw new InvalidDataException(error);
                if (content.Contains("已截断", StringComparison.Ordinal))
                    warnings.Add("工作表超过 200 行已截断；不能把当前结果当作完整资料内容。");
            }
            else if (format is "pptx" or "pptm")
            {
                var (text, error) = OfficeConverter.ConvertPptx(name, File.ReadAllBytes(full));
                content = text ?? throw new InvalidDataException(error);
                warnings.Add("只提取了幻灯片文字与备注；图形、图表、图片和动画未覆盖。");
            }
            else
            {
                throw new NotSupportedException("此格式暂不支持读取");
            }
        }
        catch (NotSupportedException e)
        {
            throw Error(McpErrorCodes.FormatNotSupported, e.Message);
        }
        catch (Exception e)
        {
            throw Error(McpErrorCodes.SourceReadFailed, "读取资料失败：" + e.Message);
        }

        var offset = ParseCursor(cursor);
        var chunk = TakeChunk(content, offset, out var next, out var chunkBytes);
        var result = new McpSourceContent(
            McpAccessPolicy.ToDisplayPath(relativePath!),
            format,
            fileVersion,
            chunk,
            info.Length,
            next,
            string.IsNullOrEmpty(next),
            chunkBytes < Encoding.UTF8.GetByteCount(content),
            warnings);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public string SearchContent(string? kb, string? query, int limit = SearchLimitDefault)
    {
        if (!policy.KbExists(kb)) throw Error(McpErrorCodes.KbNotFound, "知识库不存在或未授权");
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
                if (!SourceRules.IsText(Path.GetFileName(file))) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > McpAccessPolicy.MaxReadBytes) continue;
                    var text = FileHelper.DecodeText(File.ReadAllBytes(file));
                    var index = text.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    matches.Add(new McpSearchMatch(
                        McpAccessPolicy.ToDisplayPath(Path.GetRelativePath(root, file)),
                        Snippet(text, index, query.Trim().Length)));
                }
                catch { }
            }
        }

        return JsonSerializer.Serialize(
            new { kb, query, limit = effectiveLimit, matches = matches.ToArray(), scanned_files = scanned },
            JsonOptions);
    }

    public string GetPendingChanges(string? kb)
    {
        if (!policy.KbExists(kb)) throw Error(McpErrorCodes.KbNotFound, "知识库不存在或未授权");
        var changes = ChangeStore.Load(Path.Combine(config.WikisDir, kb!))
            .Select(c => new McpPendingChange(McpAccessPolicy.ToDisplayPath(c.File), c.Action, c.Time))
            .ToArray();
        return JsonSerializer.Serialize(new { kb, changes }, JsonOptions);
    }

    public string WriteWikiFile(string? kb, string? relativePath, string? content, string? expectedVersion, bool expectedAbsent)
    {
        var full = policy.ResolveWritePath(kb, relativePath, out var code);
        if (full == null) throw Error(code, "当前档位或路径不允许写入");
        if (content == null) throw Error(McpErrorCodes.PathNotAllowed, "写入内容不能为空");
        if (Encoding.UTF8.GetByteCount(content) > McpAccessPolicy.MaxWriteBytes)
            throw Error(McpErrorCodes.FileTooLarge, "写入内容超过 4 MiB 上限");

        var exists = File.Exists(full);
        var currentVersion = exists ? McpAccessPolicy.FileVersion(full) : "";
        if (exists)
        {
            if (expectedAbsent)
                throw Error(McpErrorCodes.VersionConflict, "目标文件已存在，不能按新建条件写入");
            if (string.IsNullOrWhiteSpace(expectedVersion))
                throw Error(McpErrorCodes.VersionRequired, "覆盖已有文件必须提供 expected_version");
            if (!string.Equals(currentVersion, expectedVersion.Trim(), StringComparison.OrdinalIgnoreCase))
                throw Error(McpErrorCodes.VersionConflict, "文件已被修改，请重新读取后再写");
            if (reviews.IsManualProtected(kb!, relativePath!, currentVersion))
                throw Error(McpErrorCodes.ManualReviewRequired, "此页含人工修订内容，需在桌面应用明确解除保护后再写");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(expectedVersion))
                throw Error(McpErrorCodes.VersionConflict, "文件不存在时不能提供 expected_version");
            if (!expectedAbsent)
                throw Error(McpErrorCodes.VersionRequired, "新建文件必须显式提供 expectedAbsent=true");
        }

        try
        {
            if (!fileBridge.WriteTextAtomic(kb!, relativePath!, content, out var error))
                throw Error(McpErrorCodes.WriteNotAllowedPath, error);
            var rel = McpAccessPolicy.ToDisplayPath(relativePath!);
            LogWriter.Append(config.WikisDir, kb!, $"- {LogWriter.Now} MCP 写入知识：{rel}");
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw Error(McpErrorCodes.ServiceUnavailable, "写入失败：" + e.Message);
        }

        var written = File.Exists(full) ? McpAccessPolicy.FileVersion(full) : McpAccessPolicy.ContentVersion(content);
        var result = new McpWriteResult(
            McpAccessPolicy.ToDisplayPath(relativePath!),
            written,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            false);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public string BuildBoundaryPrompt()
    {
        var kb = RequireCurrentKb();
        return prompts.ComposeBoundary(kb, null, "mcp");
    }

    public string BuildBatchIngestPrompt(string? files)
    {
        var kb = RequireCurrentKb();
        var parsed = WorkflowPromptService.ParseFiles(files ?? "")
            .Select(f => f.StartsWith("raw/sources/", StringComparison.OrdinalIgnoreCase) ? f : "raw/sources/" + f)
            .ToList();
        return prompts.ComposeBatch(kb, parsed, "mcp");
    }

    public string BuildIncrementalIngestPrompt()
        => prompts.ComposeProtocol(RequireCurrentKb(), TaskProtocol.IncrementalIngest, "", "mcp");

    public string BuildQueryPrompt(string question)
        => prompts.ComposeProtocol(RequireCurrentKb(), TaskProtocol.Query, question, "mcp");

    public string BuildLintPrompt()
        => prompts.ComposeProtocol(RequireCurrentKb(), TaskProtocol.Lint, "", "mcp");

    public string BuildComparePrompt(string topic)
        => prompts.ComposeProtocol(RequireCurrentKb(), TaskProtocol.Compare, topic, "mcp");

    public string BuildCleanPrompt()
        => prompts.ComposeProtocol(RequireCurrentKb(), TaskProtocol.Clean, "", "mcp");

    public string BuildIndex(string? kb = null)
    {
        kb = string.IsNullOrWhiteSpace(kb) ? RequireCurrentKb() : kb;
        if (!policy.KbExists(kb)) throw Error(McpErrorCodes.KbNotFound, "知识库不存在或未授权");
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

    private static bool IsVisibleName(string name, string full, string root)
    {
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (rel.StartsWith("raw/sources/", StringComparison.OrdinalIgnoreCase)) return SourceRules.IsSupported(name);
        return SourceRules.IsText(name) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
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
            catch { continue; }

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
        var snippet = text.Substring(start, length).Replace("\r", " ").Replace("\n", " ").Trim();
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
        catch { return; }

        foreach (var path in entries)
        {
            if (lines.Count >= cap) return;
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            var isDir = Directory.Exists(path);
            if (!isDir && !SourceRules.IsText(name)) continue;
            lines.Add(isDir ? $"- {prefix}{name}/" : $"- {prefix}{name}");
            if (isDir)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                Walk(path, prefix + name + "/", lines, cap);
            }
        }
    }

    private static int ParseCursor(string? cursor)
        => int.TryParse(cursor, out var value) && value >= 0 ? value : 0;

    /// <summary>按字符切出不超过 64 KiB UTF-8 的片段；游标为字符偏移。</summary>
    private static string TakeChunk(string content, int offset, out string nextCursor, out int chunkBytes)
    {
        if (offset >= content.Length)
        {
            nextCursor = "";
            chunkBytes = 0;
            return "";
        }

        var end = offset;
        var bytes = 0;
        while (end < content.Length)
        {
            var width = Encoding.UTF8.GetByteCount(content.AsSpan(end, 1));
            if (bytes + width > McpAccessPolicy.MaxChunkBytes) break;
            bytes += width;
            end++;
        }
        if (end == offset) end = offset + 1;
        chunkBytes = bytes;
        nextCursor = end >= content.Length ? "" : end.ToString();
        return content.Substring(offset, end - offset);
    }

    private static McpException Error(string code, string message) => new($"{code}: {message}");

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
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("totalBytes")] long TotalBytes,
        [property: JsonPropertyName("nextCursor")] string NextCursor,
        [property: JsonPropertyName("complete")] bool Complete);

    private sealed record McpSourceContent(
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("sourceVersion")] string SourceVersion,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("totalBytes")] long TotalBytes,
        [property: JsonPropertyName("nextCursor")] string NextCursor,
        [property: JsonPropertyName("complete")] bool Complete,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

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
