using System.IO;
using System.Text;

namespace LLM_Wiki.Services;

/// <summary>
/// 资料服务：raw/sources/ 下原格式文件（md/txt/docx/xlsx）的列表、导入、读取、保存、导出。
/// 上传保持原字节，不生成 Markdown 副本；覆盖前旧版本先入回收站，失败则拒绝覆盖。
/// </summary>
public sealed class SourceService(ConfigService config, ChangeService changes, TrashService trash, OperationStore operations)
{
    public IReadOnlyList<SourceInfo> List(string kb)
    {
        var paths = Paths(kb);
        if (!Directory.Exists(paths.Sources)) return [];
        var list = new List<SourceInfo>();
        foreach (var file in Directory.EnumerateFiles(paths.Sources))
        {
            var name = Path.GetFileName(file);
            if (SourceRules.ShouldIgnore(name)) continue;
            var info = new FileInfo(file);
            list.Add(new SourceInfo(
                "raw/sources/" + name,
                name,
                Path.GetExtension(name).ToLowerInvariant(),
                info.Length,
                info.LastWriteTime));
        }
        return [.. list.OrderBy(s => s.Name, StringComparer.Ordinal)];
    }

    public SourceInfo? Get(string kb, string rel)
    {
        var normalized = ChangeService.Normalize(rel);
        var name = Path.GetFileName(normalized);
        var full = FullPath(Paths(kb), normalized);
        if (full == null || !File.Exists(full) || SourceRules.ShouldIgnore(name)) return null;
        var info = new FileInfo(full);
        return new SourceInfo(normalized, name, Path.GetExtension(name).ToLowerInvariant(), info.Length, info.LastWriteTime);
    }

    public bool Exists(string kb, string rel) => Get(kb, rel) != null;

    /// <summary>读取文本与当前原字节哈希；非文本返回错误。</summary>
    public bool TryReadText(string kb, string rel, out string content, out string hash, out string error)
    {
        content = "";
        hash = "";
        error = "";
        var full = FullPath(Paths(kb), ChangeService.Normalize(rel));
        if (full == null || !File.Exists(full))
        {
            error = "资料不存在";
            return false;
        }
        if (!SourceRules.IsText(Path.GetFileName(full)))
        {
            error = "该格式不支持文本编辑";
            return false;
        }
        try
        {
            var bytes = File.ReadAllBytes(full);
            hash = HashUtil.Sha256(full);
            content = FileHelper.DecodeText(bytes);
            return true;
        }
        catch (Exception e)
        {
            error = "读取资料失败：" + e.Message;
            return false;
        }
    }

    /// <summary>导入单个文件；覆盖时先备份旧版本，备份失败拒绝覆盖。</summary>
    public SourceImportItem ImportFile(string kb, string sourcePath, SourceConflictPolicy conflict)
    {
        var name = Path.GetFileName(sourcePath);
        if (!SourceRules.IsSupported(name))
            return SourceImportItem.Failed(name, "此类型暂不支持添加");
        try
        {
            var bytes = File.ReadAllBytes(sourcePath);
            if (bytes.LongLength > SourceRules.MaxFileBytes)
                return SourceImportItem.Failed(name, "超过单文件 20 MiB 上限");
            return ImportBytes(kb, name, bytes, conflict, "添加资料");
        }
        catch (Exception e)
        {
            return SourceImportItem.Failed(name, "读取文件失败：" + e.Message);
        }
    }

    /// <summary>新建文本资料：默认 Markdown；不产生空文件，同名不覆盖。</summary>
    public SourceImportItem CreateText(string kb, string name, string content)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return SourceImportItem.Failed(name, "文件名不能为空");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return SourceImportItem.Failed(name, "文件名包含非法字符");
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            name += ".md";
        }

        if (string.IsNullOrEmpty(content)) return SourceImportItem.Failed(name, "正文不能为空，未创建文件");
        var bytes = new UTF8Encoding(false).GetBytes(content);
        if (bytes.LongLength > SourceRules.MaxFileBytes)
            return SourceImportItem.Failed(name, "超过单文件 20 MiB 上限");
        return ImportBytes(kb, name, bytes, SourceConflictPolicy.Ask, "新建资料");
    }

    /// <summary>
    /// 保存文本资料：expectedHash 非空时必须与磁盘一致，否则 VERSION_CONFLICT（不覆盖）。
    /// 成功后登记“修改”变更并返回新哈希。
    /// </summary>
    public SourceSaveResult SaveText(string kb, string rel, string content, string? expectedHash)
    {
        var normalized = ChangeService.Normalize(rel);
        var full = FullPath(Paths(kb), normalized);
        if (full == null) return new SourceSaveResult(false, "", "", "路径不可用");
        if (!SourceRules.IsText(normalized)) return new SourceSaveResult(false, "", "", "该格式不支持文本编辑");

        var existingHash = File.Exists(full) ? HashUtil.Sha256(full) : "";
        if (!string.IsNullOrEmpty(expectedHash)
            && !string.Equals(existingHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new SourceSaveResult(false, "", existingHash, "VERSION_CONFLICT：文件已被其他程序修改");
        }

        var bytes = new UTF8Encoding(false).GetBytes(content);
        if (bytes.LongLength > SourceRules.MaxFileBytes)
            return new SourceSaveResult(false, "", existingHash, "超过单文件 20 MiB 上限");

        var op = operations.Begin(kb, "source.save", normalized, existingHash, HashUtil.Sha256Text(content), "编辑保存");
        try
        {
            WriteAtomic(full, bytes);
            operations.MarkCommitted(kb, op);
        }
        catch (Exception e)
        {
            operations.Fail(kb, op, e.Message);
            return new SourceSaveResult(false, "", existingHash, "保存失败：" + e.Message);
        }

        var newHash = HashUtil.Sha256(full);
        try
        {
            changes.Record(kb, normalized, "修改", newHash);
            LogWriter.AppendSource(config.WikisDir, kb, normalized, "保存资料");
            operations.Complete(kb, op);
        }
        catch (Exception e)
        {
            operations.Fail(kb, op, "文件已保存，变更记录未完成：" + e.Message);
            return new SourceSaveResult(true, newHash, existingHash, "文件已保存，变更记录未完成，请重试登记");
        }

        return new SourceSaveResult(true, newHash, newHash, "");
    }

    public bool Export(string kb, string rel, string destPath, out string error)
    {
        error = "";
        var full = FullPath(Paths(kb), ChangeService.Normalize(rel));
        if (full == null || !File.Exists(full))
        {
            error = "资料不存在";
            return false;
        }
        try
        {
            File.Copy(full, destPath, overwrite: false);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public string? FullPathFor(string kb, string rel) => FullPath(Paths(kb), ChangeService.Normalize(rel));

    public string Hash(string kb, string rel)
    {
        var full = FullPath(Paths(kb), ChangeService.Normalize(rel));
        if (full == null || !File.Exists(full)) return "";
        try { return HashUtil.Sha256(full); }
        catch { return ""; }
    }

    /// <summary>删除/覆盖前的旧版本交由回收站；资料页“移入回收站”走此入口。</summary>
    public TrashMoveResult MoveToTrash(string kb, string rel, string reason = "手动移入回收站")
        => trash.MoveToTrash(kb, rel, reason, recordAsDeleted: true);

    private SourceImportItem ImportBytes(string kb, string name, byte[] bytes, SourceConflictPolicy conflict, string reason)
    {
        var paths = Paths(kb);
        var rel = "raw/sources/" + name;
        var full = Path.Combine(paths.Sources, name);
        var existed = File.Exists(full);
        var beforeHash = existed ? HashUtil.Sha256(full) : "";

        if (existed && conflict == SourceConflictPolicy.Skip)
            return SourceImportItem.Skipped(name, "同名资料已存在，已跳过");
        if (existed && conflict == SourceConflictPolicy.Ask)
            return SourceImportItem.Conflict(name, "同名资料已存在");

        TrashEntry? backup = null;
        if (existed)
        {
            var moved = trash.MoveToTrash(kb, rel, "覆盖前版本", recordAsDeleted: false, kind: "overwrite");
            if (!moved.Success)
                return SourceImportItem.Failed(name, "无法保留旧版本，已拒绝覆盖：" + moved.Message);
            backup = moved.Entry;
        }

        var newHash = HashBytes(bytes);
        var op = operations.Begin(kb, existed ? "source.replace" : "source.add", rel, beforeHash, newHash, reason);

        var tmp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(paths.Sources);
            File.WriteAllBytes(tmp, bytes);
            if (!string.Equals(HashUtil.Sha256(tmp), newHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("临时文件校验失败");
            File.Move(tmp, full, overwrite: true);
            operations.MarkCommitted(kb, op);
        }
        catch (Exception e)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            operations.Fail(kb, op, e.Message);
            // 新文件没有安全落盘时，把刚备份的旧版本放回原位，避免“覆盖失败却丢了旧文件”
            if (backup != null) trash.RollbackOverwrite(kb, backup, full, out _);
            return SourceImportItem.Failed(name, "保存失败：" + e.Message);
        }

        try
        {
            changes.Record(kb, rel, existed ? "修改" : "新增", newHash);
            LogWriter.AppendSource(config.WikisDir, kb, rel, existed ? "替换资料" : "添加资料");
            operations.Complete(kb, op);
        }
        catch (Exception e)
        {
            operations.Fail(kb, op, "文件已保存，变更记录未完成：" + e.Message);
            return SourceImportItem.Added(name, rel, newHash, existed, "文件已保存，变更记录未完成，请重试登记");
        }

        return SourceImportItem.Added(name, rel, newHash, existed, existed ? "已替换" : "已添加");
    }

    private static string HashBytes(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
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

    private static string? FullPath(KbPaths paths, string rel)
    {
        if (!rel.StartsWith("raw/sources/", StringComparison.OrdinalIgnoreCase)) return null;
        var full = Path.GetFullPath(Path.Combine(paths.Root, rel.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(paths.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    private KbPaths Paths(string kb) => new(Path.Combine(config.WikisDir, kb));
}

public enum SourceConflictPolicy { Ask, Overwrite, Skip }

public enum SourceImportStatus { Added, Skipped, Conflict, Failed }

public sealed record SourceImportItem(
    SourceImportStatus Status,
    string Name,
    string RelPath,
    string Hash,
    bool Replaced,
    string Message)
{
    public static SourceImportItem Added(string name, string rel, string hash, bool replaced, string message)
        => new(SourceImportStatus.Added, name, rel, hash, replaced, message);
    public static SourceImportItem Skipped(string name, string message)
        => new(SourceImportStatus.Skipped, name, "", "", false, message);
    public static SourceImportItem Conflict(string name, string message)
        => new(SourceImportStatus.Conflict, name, "", "", false, message);
    public static SourceImportItem Failed(string name, string message)
        => new(SourceImportStatus.Failed, name, "", "", false, message);
}

public sealed record SourceSaveResult(bool Success, string NewHash, string DiskHash, string Message);

public sealed record SourceInfo(string RelPath, string Name, string Extension, long Size, DateTime Modified)
{
    public string TypeText => SourceRules.TypeLabel(Name);
    public string SizeText => SourceRules.SizeText(Size);
    public string ModifiedText => SourceRules.RelativeTime(Modified);
    public bool IsText => SourceRules.IsText(Name);
    public bool CanPreviewInApp => IsText;
    public string PreviewHint => IsText ? "可预览与编辑" : "原格式保存 · 用系统程序打开";
}
