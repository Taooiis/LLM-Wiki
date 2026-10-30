using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>
/// 库内回收站：.llmwiki/trash/{条目ID}/ 保存原字节与元信息。
/// 删除/覆盖前先写凭据并校验回收副本，成功后才移除/替换源文件；不依赖系统回收站。
/// </summary>
public sealed class TrashService(ConfigService config, ChangeService changes, OperationStore operations)
{
    public TrashMoveResult MoveToTrash(string kb, string rel, string reason, bool recordAsDeleted = true, string kind = "deleted")
    {
        var paths = Paths(kb);
        var full = FullPath(paths, rel);
        if (full == null || !File.Exists(full))
            return new TrashMoveResult(false, null, "资料不存在，未移入回收站");

        string hash;
        long size;
        try
        {
            hash = HashUtil.Sha256(full);
            size = new FileInfo(full).Length;
        }
        catch (Exception e)
        {
            return new TrashMoveResult(false, null, "读取资料失败：" + e.Message);
        }

        // 覆盖前版本只是保留旧文件，不产生“删除”待整理项
        var prior = recordAsDeleted ? changes.Get(kb, rel) : null;
        var op = operations.Begin(kb, recordAsDeleted ? "source.trash" : "source.overwrite-backup", rel, hash, "", reason);
        var id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var entryDir = paths.TrashEntry(id);
        var payload = Path.Combine(entryDir, "payload");
        try
        {
            Directory.CreateDirectory(entryDir);
            File.Copy(full, payload);
            if (!string.Equals(HashUtil.Sha256(payload), hash, StringComparison.OrdinalIgnoreCase))
            {
                CleanupEntry(entryDir);
                operations.Fail(kb, op, "回收副本校验失败");
                return new TrashMoveResult(false, null, "回收副本校验失败，原文件已保留");
            }

            var meta = new TrashEntry
            {
                SchemaVersion = 1,
                Id = id,
                Kind = kind,
                OriginalRel = ChangeService.Normalize(rel),
                OriginalName = Path.GetFileName(full),
                Size = size,
                Hash = hash,
                DeletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Reason = reason,
                OperationId = op.Id,
                PriorChange = prior,
            };
            StateJson.Save(Path.Combine(entryDir, "meta.json"), meta);
            operations.MarkCommitted(kb, op);
        }
        catch (Exception e)
        {
            CleanupEntry(entryDir);
            operations.Fail(kb, op, e.Message);
            return new TrashMoveResult(false, null, "写入回收站失败，原文件已保留：" + e.Message);
        }

        try
        {
            File.Delete(full);
        }
        catch (Exception e)
        {
            // 源文件未能移除：删除刚写入的回收副本，保持“未发生删除”
            CleanupEntry(entryDir);
            operations.Fail(kb, op, "移除源文件失败：" + e.Message);
            return new TrashMoveResult(false, null, "移入回收站失败，原文件已保留：" + e.Message);
        }

        if (recordAsDeleted)
        {
            try
            {
                changes.Record(kb, ChangeService.Normalize(rel), "删除", hash);
                LogWriter.AppendSource(config.WikisDir, kb, ChangeService.Normalize(rel), "移入回收站");
                operations.Complete(kb, op);
            }
            catch (Exception e)
            {
                operations.Fail(kb, op, "文件已移入回收站，变更记录未完成：" + e.Message);
                return new TrashMoveResult(true, LoadEntry(entryDir), "已移入回收站，变更记录未完成，请重试登记");
            }
        }
        else
        {
            operations.Complete(kb, op);
        }

        return new TrashMoveResult(true, LoadEntry(entryDir), "");
    }

    public IReadOnlyList<TrashEntry> List(string kb)
    {
        var dir = Paths(kb).TrashDir;
        if (!Directory.Exists(dir)) return [];
        var list = new List<TrashEntry>();
        foreach (var entryDir in Directory.EnumerateDirectories(dir))
        {
            var entry = LoadEntry(entryDir);
            if (entry != null) list.Add(entry);
        }
        return [.. list.OrderByDescending(e => e.DeletedAt, StringComparer.Ordinal)];
    }

    public long TotalBytes(string kb) => List(kb).Sum(e => e.Size);

    /// <summary>恢复；同名冲突时由调用方提供新名称（不含路径）。不覆盖现有文件。</summary>
    public TrashRestoreResult Restore(string kb, string id, string? newName = null)
    {
        var paths = Paths(kb);
        var entryDir = paths.TrashEntry(id);
        var entry = LoadEntry(entryDir);
        if (entry == null) return new TrashRestoreResult(false, "", "回收站条目不存在或已损坏");
        if (entry.RestoredAt != null) return new TrashRestoreResult(false, "", "该条目已恢复，不能重复恢复");

        var payload = Path.Combine(entryDir, "payload");
        if (!File.Exists(payload)) return new TrashRestoreResult(false, "", "回收副本不存在，无法恢复");
        string hash;
        try { hash = HashUtil.Sha256(payload); }
        catch (Exception e) { return new TrashRestoreResult(false, "", "回收副本读取失败：" + e.Message); }
        if (!string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase))
            return new TrashRestoreResult(false, "", "回收副本已变化，不能恢复");

        var dir = Path.GetDirectoryName(entry.OriginalRel.Replace('/', Path.DirectorySeparatorChar)) ?? "raw/sources";
        var name = string.IsNullOrWhiteSpace(newName) ? entry.OriginalName : newName.Trim();
        if (!SourceRules.IsSupported(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new TrashRestoreResult(false, "", "恢复名称不可用");

        var rel = ChangeService.Normalize(Path.Combine(dir, name));
        var target = FullPath(paths, rel);
        if (target == null) return new TrashRestoreResult(false, "", "恢复路径不可用");
        if (File.Exists(target)) return new TrashRestoreResult(false, rel, "同名文件已存在，请另存名称");

        var op = operations.Begin(kb, "trash.restore", rel, "", hash, "从回收站恢复");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(payload, target, overwrite: false);
            if (!string.Equals(HashUtil.Sha256(target), hash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(target); } catch { }
                operations.Fail(kb, op, "恢复文件校验失败");
                return new TrashRestoreResult(false, rel, "恢复文件校验失败");
            }
            operations.MarkCommitted(kb, op);
        }
        catch (Exception e)
        {
            operations.Fail(kb, op, e.Message);
            return new TrashRestoreResult(false, rel, "恢复失败：" + e.Message);
        }

        // 仅当恢复到原路径、原字节，且删除前已有待整理记录时，恢复原记录；
        // 其它情况按当前磁盘事实登记新增/修改，不凭同名清除别的删除项。
        var isOriginal = ChangeService.Normalize(entry.OriginalRel) == rel;
        if (isOriginal && entry.Kind == "deleted" && entry.PriorChange is { } prior
            && string.Equals(prior.Hash ?? "", hash, StringComparison.OrdinalIgnoreCase))
        {
            changes.Reinstate(kb, prior);
        }
        else
        {
            // 目标路径此前必须为空，按真实事实登记新增
            changes.Record(kb, rel, "新增", hash);
        }

        LogWriter.AppendSource(config.WikisDir, kb, rel, "从回收站恢复");
        operations.Complete(kb, op);

        // 恢复后条目保留为历史还是移除？设计允许恢复后条目不再可重复恢复；
        // 这里保留元信息并标记 RestoredAt，避免重复恢复到两个位置。
        entry.RestoredAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        entry.RestoredRel = rel;
        StateJson.Save(Path.Combine(entryDir, "meta.json"), entry);
        return new TrashRestoreResult(true, rel, "");
    }

    /// <summary>覆盖写失败时把备份恢复到原位置，并移除该备份条目。</summary>
    public bool RollbackOverwrite(string kb, TrashEntry entry, string targetFull, out string error)
    {
        error = "";
        var entryDir = Paths(kb).TrashEntry(entry.Id);
        var payload = Path.Combine(entryDir, "payload");
        if (!File.Exists(payload))
        {
            error = "覆盖前版本不存在";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
            File.Copy(payload, targetFull, overwrite: true);
            if (!string.Equals(HashUtil.Sha256(targetFull), entry.Hash, StringComparison.OrdinalIgnoreCase))
            {
                error = "旧版本恢复校验失败";
                return false;
            }
            CleanupEntry(entryDir);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public bool Purge(string kb, string id, out string error)    {
        error = "";
        var dir = Paths(kb).TrashEntry(id);
        if (!Directory.Exists(dir))
        {
            error = "回收站条目不存在";
            return false;
        }
        try
        {
            Directory.Delete(dir, true);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public void PurgeAll(string kb)
    {
        var dir = Paths(kb).TrashDir;
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateDirectories(dir))
        {
            try { Directory.Delete(entry, true); } catch { }
        }
    }

    public bool Export(string kb, string id, string destPath, out string error)
    {
        error = "";
        var entry = LoadEntry(Paths(kb).TrashEntry(id));
        if (entry == null)
        {
            error = "回收站条目不存在";
            return false;
        }
        var payload = Path.Combine(Paths(kb).TrashEntry(id), "payload");
        if (!File.Exists(payload))
        {
            error = "回收副本不存在";
            return false;
        }
        try
        {
            File.Copy(payload, destPath, overwrite: false);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private TrashEntry? LoadEntry(string entryDir)
    {
        try
        {
            var meta = Path.Combine(entryDir, "meta.json");
            if (!File.Exists(meta)) return null;
            return StateJson.Load<TrashEntry>(meta);
        }
        catch { return null; }
    }

    private static string? FullPath(KbPaths paths, string rel)
    {
        var normalized = ChangeService.Normalize(rel);
        if (!normalized.StartsWith("raw/sources/", StringComparison.OrdinalIgnoreCase)) return null;
        var full = Path.GetFullPath(Path.Combine(paths.Root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(paths.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static void CleanupEntry(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private KbPaths Paths(string kb) => new(Path.Combine(config.WikisDir, kb));
}

public sealed record TrashMoveResult(bool Success, TrashEntry? Entry, string Message);

public sealed record TrashRestoreResult(bool Success, string RelPath, string Message);

public sealed class TrashEntry
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "deleted";
    [JsonPropertyName("originalRel")] public string OriginalRel { get; set; } = "";
    [JsonPropertyName("originalName")] public string OriginalName { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("deletedAt")] public string DeletedAt { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("operationId")] public string OperationId { get; set; } = "";
    [JsonPropertyName("priorChange")] public Change? PriorChange { get; set; }
    [JsonPropertyName("restoredAt")] public string? RestoredAt { get; set; }
    [JsonPropertyName("restoredRel")] public string? RestoredRel { get; set; }

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public string SizeText => SourceRules.SizeText(Size);
    public string StatusText => RestoredAt == null ? "可恢复" : "已恢复";
    public bool CanRestore => RestoredAt == null;
}
