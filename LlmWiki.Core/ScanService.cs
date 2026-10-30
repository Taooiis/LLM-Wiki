using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>
/// 轻量文件扫描：以原字节 SHA256 建立 .llmwiki/manifest.json 基线，
/// 启动、进入资料页、刷新、确认前调用；扫描不完整时不判删除、不替换上次完整清单。
/// </summary>
public sealed class ScanService(ConfigService config, ChangeService changes, ReviewService reviews)
{
    public ScanResult Scan(string kb, bool recordChanges = true, bool verifyHashes = false)
    {
        var paths = Paths(kb);
        if (!Directory.Exists(paths.Sources))
        {
            return new ScanResult(false, true, 0, 0, 0, [], "资料目录不存在", false);
        }

        var previous = LoadManifest(paths);
        var bootstrap = previous == null;
        var incomplete = false;
        var now = new Dictionary<string, SourceStamp>(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var modified = new List<string>();
        var deleted = new List<string>();
        var unstable = new List<string>();

        foreach (var file in Directory.EnumerateFiles(paths.Sources))
        {
            var name = Path.GetFileName(file);
            if (SourceRules.ShouldIgnore(name)) continue;
            var rel = "raw/sources/" + name;
            try
            {
                var stamp = ReadStamp(file, rel, previous, verifyHashes);
                if (stamp == null)
                {
                    unstable.Add(rel);
                    continue;
                }
                now[rel] = stamp;
            }
            catch
            {
                incomplete = true;
            }
        }

        if (previous != null)
        {
            foreach (var old in previous.Files)
            {
                if (now.ContainsKey(old.Key)) continue;
                if (incomplete || unstable.Count > 0)
                {
                    // 未完整读到的文件不能判删除：保留旧基线，等待下次完整扫描
                    now[old.Key] = old.Value;
                    continue;
                }
                deleted.Add(old.Key);
            }
        }

        // 变化判定：首次接入只建立基线，不把存量资料一次性写成新增
        if (!bootstrap)
        {
            foreach (var (rel, stamp) in now)
            {
                if (!previous!.Files.TryGetValue(rel, out var old))
                {
                    added.Add(rel);
                }
                else if (!string.Equals(old.Hash, stamp.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    modified.Add(rel);
                }
            }
        }

        var complete = !incomplete && unstable.Count == 0;
        if (complete)
        {
            var manifest = new SourceManifest
            {
                SchemaVersion = 1,
                Generation = (previous?.Generation ?? 0) + 1,
                ScannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                RuleVersion = reviews.RuleVersion(kb),
                Complete = true,
                Files = now,
            };
            StateJson.Save(paths.Manifest, manifest);
        }

        if (recordChanges)
        {
            foreach (var rel in added)
                changes.Record(kb, rel, "新增", now[rel].Hash);
            foreach (var rel in modified)
                changes.Record(kb, rel, "修改", now[rel].Hash);
            foreach (var rel in deleted)
            {
                previous!.Files.TryGetValue(rel, out var old);
                changes.Record(kb, rel, "删除", old?.Hash ?? "");
            }
        }

        return new ScanResult(
            complete,
            bootstrap,
            added.Count,
            modified.Count,
            deleted.Count,
            unstable,
            incomplete ? "部分目录或文件无法读取，本次不判断删除" : "",
            bootstrap);
    }

    /// <summary>确认前校验指定资料的真实版本；返回与 manifest 不一致的路径。</summary>
    public IReadOnlyList<string> VerifyHashes(string kb, IEnumerable<string> rels)
    {
        var paths = Paths(kb);
        var manifest = LoadManifest(paths);
        var list = new List<string>();
        foreach (var rel in rels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.Combine(paths.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                list.Add(rel);
                continue;
            }
            string hash;
            try { hash = HashUtil.Sha256(full); }
            catch
            {
                list.Add(rel);
                continue;
            }
            if (manifest != null && manifest.Files.TryGetValue(rel, out var stamp)
                && !string.Equals(stamp.Hash, hash, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(rel);
            }
        }
        return list;
    }

    public IReadOnlyList<SourceStamp> Baseline(string kb)
    {
        var manifest = LoadManifest(Paths(kb));
        return manifest == null ? [] : [.. manifest.Files.Values.OrderBy(s => s.RelPath, StringComparer.Ordinal)];
    }

    private static SourceStamp? ReadStamp(string file, string rel, SourceManifest? previous, bool verifyHashes)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = new FileInfo(file);
            var size = before.Length;
            var mtime = before.LastWriteTimeUtc.Ticks;
            if (previous != null
                && !verifyHashes
                && previous.Files.TryGetValue(rel, out var old)
                && old.Size == size
                && old.LastWriteUtcTicks == mtime)
            {
                return old;
            }

            var hash = HashUtil.Sha256(file);
            var after = new FileInfo(file);
            if (after.Length == size && after.LastWriteTimeUtc.Ticks == mtime)
            {
                return new SourceStamp
                {
                    RelPath = rel,
                    Size = size,
                    LastWriteUtcTicks = mtime,
                    Hash = hash,
                };
            }
            Thread.Sleep(80);
        }
        return null;
    }

    private static SourceManifest? LoadManifest(KbPaths paths)
    {
        try
        {
            var manifest = StateJson.Load<SourceManifest>(paths.Manifest);
            if (manifest?.SchemaVersion > 1) return null;
            manifest?.Files ??= new Dictionary<string, SourceStamp>(StringComparer.OrdinalIgnoreCase);
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    private KbPaths Paths(string kb) => new(Path.Combine(config.WikisDir, kb));
}

public sealed record ScanResult(
    bool Complete,
    bool Bootstrap,
    int Added,
    int Modified,
    int Deleted,
    IReadOnlyList<string> Unstable,
    string Warning,
    bool HistoryUnverified)
{
    public int ChangeCount => Added + Modified + Deleted;
    public bool HasChanges => ChangeCount > 0;
}

public sealed class SourceManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("generation")] public int Generation { get; set; }
    [JsonPropertyName("scannedAt")] public string ScannedAt { get; set; } = "";
    [JsonPropertyName("ruleVersion")] public string RuleVersion { get; set; } = "";
    [JsonPropertyName("complete")] public bool Complete { get; set; }
    [JsonPropertyName("files")] public Dictionary<string, SourceStamp> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class SourceStamp
{
    [JsonPropertyName("relPath")] public string RelPath { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("lastWriteUtcTicks")] public long LastWriteUtcTicks { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
