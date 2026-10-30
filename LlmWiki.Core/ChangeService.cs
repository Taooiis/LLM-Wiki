using System.IO;

namespace LLM_Wiki.Services;

/// <summary>
/// 待整理变更服务：.changes.json 的读取、合并、比较后确认。
/// 确认在文件锁与重新读取之间完成，只移除与任务快照仍一致的项目；
/// 不提供“按文件名全部清空”，避免并发写入的新记录被整文件覆盖。
/// </summary>
public sealed class ChangeService
{
    private readonly ConfigService _config;

    public ChangeService(ConfigService config) => _config = config;

    public IReadOnlyList<Change> Load(string kb)
    {
        var paths = Paths(kb);
        return ChangeStore.Load(paths.Root);
    }

    public Change? Get(string kb, string rel)
    {
        var normalized = Normalize(rel);
        return Load(kb).FirstOrDefault(c => Normalize(c.File) == normalized);
    }

    public void Record(string kb, string rel, string action, string hash)
    {
        var paths = Paths(kb);
        paths.EnsureStateDirs();
        using var _ = FileLock.Acquire(Path.Combine(paths.Hidden, "changes.lock"));
        var list = ChangeStore.Load(paths.Root);
        var normalized = Normalize(rel);
        var existing = list.FirstOrDefault(c => Normalize(c.File) == normalized);
        // 同一路径同一动作同一版本重复扫描不刷新时间，避免“扫描一次就像又改了一次”
        if (existing != null
            && existing.Action == action
            && string.Equals(existing.Hash ?? "", hash ?? "", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        list.RemoveAll(c => Normalize(c.File) == normalized);
        list.Add(new Change(normalized, action, LogWriter.Now, hash ?? ""));
        ChangeStore.SaveAll(paths.Root, list);
    }

    /// <summary>恢复一条旧记录；已有同路径记录时保留较新的，不覆盖。</summary>
    public void Reinstate(string kb, Change change)
    {
        var paths = Paths(kb);
        paths.EnsureStateDirs();
        using var _ = FileLock.Acquire(Path.Combine(paths.Hidden, "changes.lock"));
        var list = ChangeStore.Load(paths.Root);
        if (list.Any(c => Normalize(c.File) == Normalize(change.File))) return;
        list.Add(change with { File = Normalize(change.File) });
        ChangeStore.SaveAll(paths.Root, list);
    }

    public void RemoveIfMatching(string kb, string rel, string action, string hash)
    {
        var paths = Paths(kb);
        using var _ = FileLock.Acquire(Path.Combine(paths.Hidden, "changes.lock"));
        var list = ChangeStore.Load(paths.Root);
        var before = list.Count;
        list.RemoveAll(c => Normalize(c.File) == Normalize(rel)
                            && c.Action == action
                            && string.Equals(c.Hash ?? "", hash ?? "", StringComparison.OrdinalIgnoreCase));
        if (list.Count != before) ChangeStore.SaveAll(paths.Root, list);
    }

    /// <summary>
    /// 比较后确认：调用方给的每一项都必须与当前变更记录、当前源文件版本一致才会被移除。
    /// 删除项还要求文件没有重新出现。返回确认数与保留原因。
    /// </summary>
    public ConfirmResult Confirm(string kb, IReadOnlyCollection<ConfirmItem> items)
    {
        var paths = Paths(kb);
        paths.EnsureStateDirs();
        var stale = new List<string>();
        var confirmed = 0;

        using var _ = FileLock.Acquire(Path.Combine(paths.Hidden, "changes.lock"));
        var list = ChangeStore.Load(paths.Root);
        var keep = new List<Change>();

        foreach (var item in items)
        {
            var rel = Normalize(item.File);
            var current = list.FirstOrDefault(c => Normalize(c.File) == rel);
            var match = current != null
                        && current.Action == item.Action
                        && string.Equals(current.Hash ?? "", item.Hash ?? "", StringComparison.OrdinalIgnoreCase);

            if (!match)
            {
                stale.Add(rel);
                continue;
            }

            var full = Path.Combine(paths.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (item.Action == "删除")
            {
                if (File.Exists(full))
                {
                    stale.Add(rel);
                    continue;
                }
            }
            else
            {
                if (!File.Exists(full))
                {
                    stale.Add(rel);
                    continue;
                }

                string currentHash;
                try { currentHash = HashUtil.Sha256(full); }
                catch
                {
                    stale.Add(rel);
                    continue;
                }

                if (!string.Equals(currentHash, item.SourceHash ?? item.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    stale.Add(rel);
                    continue;
                }
            }

            confirmed++;
            list.Remove(current!);
        }

        keep.AddRange(list);
        ChangeStore.SaveAll(paths.Root, keep);
        return new ConfirmResult(confirmed, stale);
    }

    public static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');

    private KbPaths Paths(string kb) => new(Path.Combine(_config.WikisDir, kb));
}

public sealed record ConfirmItem(string File, string Action, string Hash, string? SourceHash = null);

public sealed record ConfirmResult(int Confirmed, IReadOnlyList<string> Stale);

/// <summary>进程内 + 跨进程的简单文件锁；共享目录只做尽力协调，不声称分布式事务。</summary>
public sealed class FileLock : IDisposable
{
    private readonly FileStream? _stream;

    private FileLock(FileStream? stream) => _stream = stream;

    public static FileLock Acquire(string lockPath, int timeoutMs = 4000)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new FileLock(stream);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(80);
            }
            catch (IOException)
            {
                return new FileLock(null);
            }
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
    }
}
