using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>
/// 整理任务：把范围、源文件版本、规则版本、知识基线冻结为 .llmwiki/tasks/{id}.json。
/// 复制/本机执行前持久化；确认时逐项比较当前版本，只移除仍匹配的待整理记录。
/// </summary>
public sealed class TaskService(
    ConfigService config,
    ChangeService changes,
    ScanService scanner,
    SourceService sources,
    ReviewService reviews,
    PromptComposer prompts)
{
    public const int KeepEndedTasks = 50;
    public static readonly TimeSpan KeepEndedDays = TimeSpan.FromDays(30);

    public OrganizeTask CreateUpdateTask(string kb, TaskScope scope, IReadOnlyCollection<string>? selectedRelPaths = null, string? transport = "file")
    {
        var kbRoot = Root(kb);
        var all = changes.Load(kb);
        var items = new List<TaskItem>();

        if (scope == TaskScope.All)
        {
            foreach (var source in sources.List(kb))
            {
                var change = all.FirstOrDefault(c => ChangeService.Normalize(c.File) == source.RelPath);
                items.Add(new TaskItem
                {
                    RelPath = source.RelPath,
                    Action = change?.Action ?? "全部资料",
                    ChangeTime = change?.Time ?? "",
                    SourceHash = sources.Hash(kb, source.RelPath),
                    ChangeHash = change?.Hash ?? "",
                });
            }
        }
        else
        {
            var selected = scope == TaskScope.Selected && selectedRelPaths != null
                ? selectedRelPaths.Select(ChangeService.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            foreach (var change in all)
            {
                var rel = ChangeService.Normalize(change.File);
                if (selected != null && !selected.Contains(rel)) continue;
                items.Add(new TaskItem
                {
                    RelPath = rel,
                    Action = change.Action,
                    ChangeTime = change.Time,
                    SourceHash = change.Action == "删除" ? change.Hash : sources.Hash(kb, rel),
                    ChangeHash = change.Hash,
                });
            }

            // 资料页明确带入、但当前没有待整理记录的文件也要进入任务范围
            if (selected != null)
            {
                foreach (var rel in selected)
                {
                    if (items.Any(i => i.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!sources.Exists(kb, rel)) continue;
                    items.Add(new TaskItem
                    {
                        RelPath = rel,
                        Action = "所选资料",
                        SourceHash = sources.Hash(kb, rel),
                    });
                }
            }
        }

        var kind = scope == TaskScope.Pending ? TaskProtocol.IncrementalIngest : TaskProtocol.BatchIngest;
        var task = NewTask(kb, TaskKind.Update, scope, kind, transport);
        task.Items = items;
        task.WikiBaseline = [.. WikiSnapshot(kb)];
        task.Prompt = prompts.ComposeUpdate(task, transport ?? "file");
        return SaveAndReuse(kb, task);
    }

    public OrganizeTask CreateAskTask(string kb, string subKind, string input, string? transport = "file")
    {
        var task = NewTask(kb, TaskKind.Ask, TaskScope.Selected, subKind, transport);
        task.Input = input.Trim();
        task.WikiBaseline = [.. WikiSnapshot(kb)];
        task.Prompt = prompts.ComposeAsk(task, transport ?? "file");
        return SaveAndReuse(kb, task);
    }

    public OrganizeTask CreateCheckTask(string kb, string subKind, string? transport = "file")
    {
        var task = NewTask(kb, TaskKind.Check, TaskScope.Selected, subKind, transport);
        if (subKind == TaskProtocol.Clean)
        {
            task.Items = [.. changes.Load(kb)
                .Where(c => c.Action == "删除")
                .Select(c => new TaskItem
                {
                    RelPath = ChangeService.Normalize(c.File),
                    Action = c.Action,
                    ChangeTime = c.Time,
                    SourceHash = c.Hash,
                    ChangeHash = c.Hash,
                })];
        }
        task.WikiBaseline = [.. WikiSnapshot(kb)];
        task.Prompt = prompts.ComposeCheck(task, transport ?? "file");
        return SaveAndReuse(kb, task);
    }

    private OrganizeTask NewTask(string kb, TaskKind kind, TaskScope scope, string protocol, string? transport)
    {
        var rule = reviews.GetRuleStatus(kb);
        return new OrganizeTask
        {
            SchemaVersion = 1,
            Id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Kb = kb,
            Kind = kind,
            Scope = scope,
            Protocol = protocol,
            Transport = transport ?? "file",
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            RuleVersion = rule.CurrentVersion,
            PurposeHash = FileHash(Path.Combine(Root(kb), "purpose.md")),
            SchemaHash = FileHash(Path.Combine(Root(kb), "schema.md")),
            TemplateVersion = PromptComposer.TemplateVersion,
            State = TaskState.Prepared,
        };
    }

    public void MarkCopied(string taskId)
    {
        var task = Load(taskId);
        if (task == null) return;
        task.CopiedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        if (task.State == TaskState.Prepared) task.State = TaskState.Copied;
        Save(task);
    }

    public OrganizeTask RefreshAndSave(OrganizeTask task)
    {
        var refreshed = Refresh(task);
        Save(refreshed);
        return refreshed;
    }

    /// <summary>刷新任务：逐项比较源版本与变更记录，附加 InvalidReason；不覆盖整任务为未知状态。</summary>
    public OrganizeTask Refresh(OrganizeTask task)
    {        var currentChanges = changes.Load(task.Kb);
        var rule = reviews.GetRuleStatus(task.Kb);
        task.RuleChanged = rule.NeedsReview || task.RuleVersion != rule.CurrentVersion;

        foreach (var item in task.Items)
        {
            item.InvalidReason = "";
            var change = currentChanges.FirstOrDefault(c => ChangeService.Normalize(c.File) == item.RelPath);
            if (item.Action == "删除")
            {
                var full = Path.Combine(Root(task.Kb), item.RelPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    item.InvalidReason = "已删除资料重新出现";
                else if (change == null || change.Action != "删除"
                         || !string.Equals(change.Hash ?? "", item.ChangeHash ?? "", StringComparison.OrdinalIgnoreCase))
                    item.InvalidReason = "删除记录已变化";
            }
            else if (item.Action is "新增" or "修改")
            {
                var diskHash = sources.Hash(task.Kb, item.RelPath);
                if (string.IsNullOrEmpty(diskHash))
                    item.InvalidReason = "资料已不存在";
                else if (!string.Equals(diskHash, item.SourceHash, StringComparison.OrdinalIgnoreCase))
                    item.InvalidReason = "资料内容有新变化";
                else if (change == null
                         || change.Action != item.Action
                         || !string.Equals(change.Hash ?? "", item.ChangeHash ?? "", StringComparison.OrdinalIgnoreCase))
                    item.InvalidReason = "待整理记录已变化";
            }
            else
            {
                // 所选资料/全部资料中的无变更文件：仍可整理，但不能用来清除任何待整理记录
                var diskHash = sources.Hash(task.Kb, item.RelPath);
                if (string.IsNullOrEmpty(diskHash)) item.InvalidReason = "资料已不存在";
            }
        }

        return task;
    }

    /// <summary>比较并确认：仅勾选项、仅仍匹配当前版本与变更记录的项目。</summary>
    public TaskConfirmResult Confirm(string taskId, IReadOnlyCollection<string> selectedRelPaths, string? note = null)
    {
        var task = Load(taskId);
        if (task == null) return new TaskConfirmResult(false, 0, 0, [], [], "任务不存在");

        scanner.Scan(task.Kb, recordChanges: true, verifyHashes: false);
        Refresh(task);

        var selected = selectedRelPaths.Select(ChangeService.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = task.Items.Where(i => selected.Contains(i.RelPath)).ToList();
        var stale = chosen.Where(i => !string.IsNullOrEmpty(i.InvalidReason)).Select(i => i.RelPath).ToList();
        var confirmable = chosen.Where(i => string.IsNullOrEmpty(i.InvalidReason)
                                            && i.Action is "新增" or "修改" or "删除").ToList();
        var noRecord = chosen
            .Where(i => string.IsNullOrEmpty(i.InvalidReason) && i.Action is "所选资料" or "全部资料")
            .Select(i => i.RelPath)
            .ToList();

        var op = OperationStoreFor(task.Kb).Begin(task.Kb, "task.confirm", task.Id, "", "", $"确认 {confirmable.Count} 项");
        var result = changes.Confirm(task.Kb, [.. confirmable.Select(i => new ConfirmItem(i.RelPath, i.Action, i.ChangeHash, i.SourceHash))]);
        foreach (var item in confirmable)
        {
            if (!result.Stale.Contains(item.RelPath)) item.Confirmed = true;
        }
        stale.AddRange(result.Stale);

        task.ConfirmedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        task.ConfirmNote = note;
        task.State = task.Items.All(i => i.Confirmed)
            ? TaskState.Confirmed
            : TaskState.PartiallyConfirmed;
        Save(task);
        OperationStoreFor(task.Kb).Complete(task.Kb, op);

        return new TaskConfirmResult(
            true,
            result.Confirmed,
            stale.Count,
            stale,
            noRecord,
            stale.Count > 0
                ? $"已确认 {result.Confirmed} 项；{stale.Count} 项有新变化，仍保留在待整理清单中"
                : $"已确认 {result.Confirmed} 项");
    }

    /// <summary>误确认后重新列为待整理：只登记当前版本，不回滚文件。</summary>
    public void ReopenItems(string taskId, IReadOnlyCollection<string> rels)
    {
        var task = Load(taskId);
        if (task == null) return;
        foreach (var rel in rels)
        {
            var item = task.Items.FirstOrDefault(i => i.RelPath.Equals(ChangeService.Normalize(rel), StringComparison.OrdinalIgnoreCase));
            if (item == null) continue;
            item.Confirmed = false;
            var hash = item.Action == "删除" ? item.ChangeHash : sources.Hash(task.Kb, item.RelPath);
            if (!string.IsNullOrEmpty(hash))
            {
                var action = item.Action == "删除" ? "删除" : item.Action == "新增" ? "新增" : "修改";
                changes.Record(task.Kb, item.RelPath, action, hash);
            }
        }
        task.State = TaskState.PartiallyConfirmed;
        task.ConfirmedAt = null;
        Save(task);
    }

    public void Abandon(string taskId)
    {
        var task = Load(taskId);
        if (task == null) return;
        task.State = TaskState.Abandoned;
        task.ConfirmedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Save(task);
    }

    public OrganizeTask? Load(string taskId)
    {
        var file = PathsForTask(taskId);
        if (file == null) return null;
        try
        {
            var task = StateJson.Load<OrganizeTask>(file);
            task?.Items ??= [];
            task?.WikiBaseline ??= [];
            return task;
        }
        catch { return null; }
    }

    public IReadOnlyList<OrganizeTask> Recent(string kb)
    {
        var dir = Paths(kb).TasksDir;
        if (!Directory.Exists(dir)) return [];
        Cleanup(kb);
        var list = new List<OrganizeTask>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var task = StateJson.Load<OrganizeTask>(file);
                if (task == null || !task.Kb.Equals(kb, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(task);
            }
            catch { }
        }
        return [.. list.OrderByDescending(t => t.CreatedAt, StringComparer.Ordinal)];
    }

    public IReadOnlyList<OrganizeTask> Active(string kb)
        => [.. Recent(kb).Where(t => t.State is TaskState.Prepared or TaskState.Copied or TaskState.PartiallyConfirmed)];

    /// <summary>本次变化：任务准备以来的 wiki 新增/修改/删除；日志单独列出。</summary>
    public IReadOnlyList<WikiChange> ReviewWikiChanges(string kb, IReadOnlyList<WikiStamp> baseline)
    {
        var current = WikiSnapshot(kb).ToDictionary(s => s.RelPath, StringComparer.OrdinalIgnoreCase);
        var old = baseline.ToDictionary(s => s.RelPath, StringComparer.OrdinalIgnoreCase);
        var list = new List<WikiChange>();
        foreach (var (rel, stamp) in current)
        {
            if (!old.TryGetValue(rel, out var before))
                list.Add(new WikiChange(rel, "新增", rel.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase)));
            else if (!string.Equals(before.Hash, stamp.Hash, StringComparison.OrdinalIgnoreCase))
                list.Add(new WikiChange(rel, "修改", rel.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var rel in old.Keys)
        {
            if (!current.ContainsKey(rel))
                list.Add(new WikiChange(rel, "删除", rel.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase)));
        }
        return [.. list.OrderBy(c => c.IsLog).ThenBy(c => c.RelPath, StringComparer.Ordinal)];
    }

    public IReadOnlyList<WikiStamp> WikiSnapshot(string kb)
    {
        var paths = Paths(kb);
        if (!Directory.Exists(paths.Wiki)) return [];
        var list = new List<WikiStamp>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(paths.Wiki, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!SourceRules.IsText(name)) continue;
                var rel = Path.GetRelativePath(paths.Root, file).Replace('\\', '/');
                try { list.Add(new WikiStamp { RelPath = rel, Hash = HashUtil.Sha256(file) }); }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private OrganizeTask SaveAndReuse(string kb, OrganizeTask task)
    {
        // 相同快照重复复制不重复创建任务
        foreach (var existing in Recent(kb))
        {
            if (existing.State is TaskState.Confirmed or TaskState.Abandoned) continue;
            if (existing.Kind != task.Kind || existing.Scope != task.Scope || existing.Protocol != task.Protocol
                || existing.RuleVersion != task.RuleVersion || existing.Input != task.Input)
                continue;
            if (existing.Items.Count != task.Items.Count) continue;
            var same = existing.Items.All(e => task.Items.Any(t =>
                t.RelPath.Equals(e.RelPath, StringComparison.OrdinalIgnoreCase)
                && t.Action == e.Action
                && string.Equals(t.SourceHash, e.SourceHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.ChangeHash, e.ChangeHash, StringComparison.OrdinalIgnoreCase)));
            if (same) return existing;
        }
        Save(task);
        return task;
    }

    private void Save(OrganizeTask task)
    {
        var paths = Paths(task.Kb);
        paths.EnsureStateDirs();
        if (!File.Exists(paths.Manifest))
        {
            // 首次生成任务前建立扫描基线，避免任务快照与后续扫描口径不一致
            scanner.Scan(task.Kb, recordChanges: false);
        }
        StateJson.Save(paths.Task(task.Id), task);
    }

    private string? PathsForTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (!Directory.Exists(config.WikisDir)) return null;
        foreach (var kbDir in Directory.EnumerateDirectories(config.WikisDir))
        {
            var file = Path.Combine(kbDir, ".llmwiki", "tasks", taskId + ".json");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    private OperationStore OperationStoreFor(string kb) => new(config);

    private string Root(string kb) => Path.Combine(config.WikisDir, kb);

    private static string FileHash(string path)
    {
        try { return File.Exists(path) ? HashUtil.Sha256(path) : ""; }
        catch { return ""; }
    }

    private void Cleanup(string kb)
    {
        var dir = Paths(kb).TasksDir;
        if (!Directory.Exists(dir)) return;
        var ended = new List<(string file, DateTime endedAt)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var task = StateJson.Load<OrganizeTask>(file);
                if (task == null) continue;
                if (task.State is TaskState.Confirmed or TaskState.Abandoned)
                {
                    var when = DateTime.TryParse(task.ConfirmedAt, out var d) ? d : File.GetLastWriteTime(file);
                    ended.Add((file, when));
                }
            }
            catch { }
        }
        var toDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ended.OrderByDescending(e => e.endedAt).Skip(KeepEndedTasks)) toDelete.Add(item.file);
        foreach (var item in ended.Where(e => DateTime.Now - e.endedAt > KeepEndedDays)) toDelete.Add(item.file);
        foreach (var file in toDelete)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private KbPaths Paths(string kb) => new(Path.Combine(config.WikisDir, kb));
}
