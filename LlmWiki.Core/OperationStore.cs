using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>
/// 可恢复操作凭据：文件已提交但变更/日志未登记时，用于幂等补记与崩溃恢复。
/// 状态：prepared → committed → completed / failed。
/// </summary>
public sealed class OperationStore(ConfigService config)
{
    public Operation Begin(string kb, string kind, string targetRel, string beforeHash, string afterHash, string note = "")
    {
        var paths = Paths(kb);
        paths.EnsureStateDirs();
        var op = new Operation
        {
            SchemaVersion = 1,
            Id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8],
            Kind = kind,
            Target = targetRel.Replace('\\', '/'),
            BeforeHash = beforeHash,
            AfterHash = afterHash,
            Note = note,
            State = "prepared",
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        Save(paths, op);
        return op;
    }

    public void MarkCommitted(string kb, Operation op)
    {
        op.State = "committed";
        op.CommittedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Save(Paths(kb), op);
    }

    public void Complete(string kb, Operation op)
    {
        op.State = "completed";
        op.CompletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Save(Paths(kb), op);
    }

    public void Fail(string kb, Operation op, string error)
    {
        op.State = "failed";
        op.Error = error;
        op.CompletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Save(Paths(kb), op);
    }

    public IReadOnlyList<Operation> Pending(string kb)
    {
        var dir = Paths(kb).OperationsDir;
        if (!Directory.Exists(dir)) return [];
        var list = new List<Operation>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var op = StateJson.Load<Operation>(file);
                if (op is { State: "prepared" or "committed" }) list.Add(op);
            }
            catch { }
        }
        return list;
    }

    private static void Save(KbPaths paths, Operation op)
        => StateJson.Save(paths.Operation(op.Id), op);

    private KbPaths Paths(string kb) => new(Path.Combine(config.WikisDir, kb));
}

public sealed class Operation
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("beforeHash")] public string BeforeHash { get; set; } = "";
    [JsonPropertyName("afterHash")] public string AfterHash { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("committedAt")] public string? CommittedAt { get; set; }
    [JsonPropertyName("completedAt")] public string? CompletedAt { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
