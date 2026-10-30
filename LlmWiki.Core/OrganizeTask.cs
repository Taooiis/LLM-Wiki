using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

public static class TaskProtocol
{
    public const string IncrementalIngest = "incremental_ingest";
    public const string BatchIngest = "batch_ingest";
    public const string Query = "query";
    public const string Compare = "compare";
    public const string Lint = "lint";
    public const string Clean = "clean";

    public static string Display(string protocol) => protocol switch
    {
        IncrementalIngest => "更新知识",
        BatchIngest => "更新知识",
        Query => "回答问题",
        Compare => "对比主题",
        Lint => "检查知识质量",
        Clean => "处理已删除资料的引用",
        _ => protocol,
    };
}

public enum TaskKind { Update, Ask, Check }

public enum TaskScope { Pending, Selected, All }

public enum TaskState { Prepared, Copied, NeedsReview, PartiallyConfirmed, Confirmed, ClosedWithRemaining, Abandoned }

public sealed class OrganizeTask
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kb")] public string Kb { get; set; } = "";
    [JsonPropertyName("kind")] public TaskKind Kind { get; set; }
    [JsonPropertyName("scope")] public TaskScope Scope { get; set; }
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("transport")] public string Transport { get; set; } = "file";
    [JsonPropertyName("input")] public string Input { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("templateVersion")] public string TemplateVersion { get; set; } = "";
    [JsonPropertyName("ruleVersion")] public string RuleVersion { get; set; } = "";
    [JsonPropertyName("purposeHash")] public string PurposeHash { get; set; } = "";
    [JsonPropertyName("schemaHash")] public string SchemaHash { get; set; } = "";
    [JsonPropertyName("state")] public TaskState State { get; set; }
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("copiedAt")] public string? CopiedAt { get; set; }
    [JsonPropertyName("confirmedAt")] public string? ConfirmedAt { get; set; }
    [JsonPropertyName("confirmNote")] public string? ConfirmNote { get; set; }
    [JsonPropertyName("items")] public List<TaskItem> Items { get; set; } = [];
    [JsonPropertyName("wikiBaseline")] public List<WikiStamp> WikiBaseline { get; set; } = [];
    [JsonPropertyName("attemptId")] public string? AttemptId { get; set; }
    [JsonPropertyName("lastAttemptStatus")] public string? LastAttemptStatus { get; set; }
    [JsonPropertyName("ruleChanged")] public bool RuleChanged { get; set; }

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public string ProtocolText => TaskProtocol.Display(Protocol);

    [JsonIgnore]
    public string StateText => State switch
    {
        TaskState.Prepared => "已准备",
        TaskState.Copied => "已复制，等待外部执行",
        TaskState.NeedsReview => "待核对",
        TaskState.PartiallyConfirmed => "部分确认",
        TaskState.Confirmed => "本次已确认",
        TaskState.ClosedWithRemaining => "已结束，仍有待整理",
        TaskState.Abandoned => "已放弃",
        _ => "",
    };

    public bool CanConfirm => State is TaskState.Prepared or TaskState.Copied or TaskState.NeedsReview or TaskState.PartiallyConfirmed;

    /// <summary>Prepared/Copied（已复制待执行）任务可由用户声明已在外部执行，主动进入核对（03§10 L125）。</summary>
    [JsonIgnore]
    public bool CanMarkExecutedExternally => State is TaskState.Prepared or TaskState.Copied;
}

public sealed class TaskItem
{
    [JsonPropertyName("relPath")] public string RelPath { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("changeTime")] public string ChangeTime { get; set; } = "";
    [JsonPropertyName("sourceHash")] public string SourceHash { get; set; } = "";
    [JsonPropertyName("changeHash")] public string ChangeHash { get; set; } = "";
    [JsonPropertyName("confirmed")] public bool Confirmed { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("invalidReason")] public string? InvalidReason { get; set; }

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public string ReviewText => Confirmed ? "已核对" : !string.IsNullOrEmpty(InvalidReason) ? InvalidReason! : "待核对";
    public bool CanSelect => !Confirmed && string.IsNullOrEmpty(InvalidReason)
                             && Action is "新增" or "修改" or "删除" or "所选资料" or "全部资料";
    public bool ConfirmClearsChange => Action is "新增" or "修改" or "删除";
    public string StatusText => Confirmed ? "已确认" : InvalidReason ?? "";
}

public sealed class WikiStamp
{
    [JsonPropertyName("relPath")] public string RelPath { get; set; } = "";
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed record WikiChange(string RelPath, string Action, bool IsLog)
{
    public string DisplayAction => IsLog ? "操作日志" : Action;
}

public sealed record TaskConfirmResult(
    bool Success,
    int Confirmed,
    int Stale,
    IReadOnlyList<string> StalePaths,
    IReadOnlyList<string> NoRecordPaths,
    string Message);
