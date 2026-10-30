using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>
/// 规则版本提醒与人工修订保护（.llmwiki/reviews.json）。
/// 规则版本由 purpose.md + schema.md 的真实字节计算；不改写 Markdown 本身。
/// </summary>
public sealed class ReviewService(ConfigService config)
{
    public string RuleVersion(string kb)
    {
        var paths = Paths(kb);
        var h1 = File.Exists(Path.Combine(paths.Root, "purpose.md")) ? HashUtil.Sha256(Path.Combine(paths.Root, "purpose.md")) : "";
        var h2 = File.Exists(Path.Combine(paths.Root, "schema.md")) ? HashUtil.Sha256(Path.Combine(paths.Root, "schema.md")) : "";
        return HashUtil.Sha256Text(h1 + "|" + h2);
    }

    public RuleStatus GetRuleStatus(string kb)
    {
        var paths = Paths(kb);
        var current = RuleVersion(kb);
        var stateExists = File.Exists(paths.Reviews);
        var reviews = Load(kb);
        if (!stateExists)
        {
            // 存量库首次接入：把当前规则记为已核对，避免把“没有历史状态”冒充成“规则刚改过”。
            reviews.ConfirmedRuleVersion = current;
            reviews.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            try { StateJson.Save(paths.Reviews, reviews); } catch { }
        }
        return new RuleStatus(
            current,
            reviews.ConfirmedRuleVersion,
            reviews.IgnoredRuleVersion,
            !string.IsNullOrEmpty(reviews.ConfirmedRuleVersion)
            && current != reviews.ConfirmedRuleVersion
            && current != reviews.IgnoredRuleVersion);
    }

    /// <summary>建库或首次建立状态时，把当前规则记为已核对，避免新库一创建就提醒。</summary>
    public void InitializeIfMissing(string kb)
    {
        var paths = Paths(kb);
        paths.EnsureStateDirs();
        if (File.Exists(paths.Reviews)) return;
        var state = new ReviewsState
        {
            SchemaVersion = 1,
            ConfirmedRuleVersion = RuleVersion(kb),
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        StateJson.Save(paths.Reviews, state);
    }

    public void AcknowledgeRules(string kb)
    {
        var paths = Paths(kb);
        var state = Load(kb);
        state.ConfirmedRuleVersion = RuleVersion(kb);
        state.IgnoredRuleVersion = "";
        state.ConfirmedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        StateJson.Save(paths.Reviews, state);
    }

    /// <summary>“稍后处理”只忽略当前版本，不推进已确认版本。</summary>
    public void IgnoreCurrentRules(string kb)
    {
        var state = Load(kb);
        state.IgnoredRuleVersion = RuleVersion(kb);
        StateJson.Save(Paths(kb).Reviews, state);
    }

    public void MarkManualRevision(string kb, string rel, string hash)
    {
        var state = Load(kb);
        var normalized = ChangeService.Normalize(rel);
        state.ManualReviews.RemoveAll(m => ChangeService.Normalize(m.RelPath) == normalized);
        state.ManualReviews.Add(new ManualReview
        {
            RelPath = normalized,
            Hash = hash,
            At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        });
        StateJson.Save(Paths(kb).Reviews, state);
    }

    public bool IsManualProtected(string kb, string rel, string currentHash)
    {
        var normalized = ChangeService.Normalize(rel);
        var state = Load(kb);
        var item = state.ManualReviews.FirstOrDefault(m => ChangeService.Normalize(m.RelPath) == normalized);
        return item != null && string.Equals(item.Hash, currentHash, StringComparison.OrdinalIgnoreCase);
    }

    public void ClearManualProtection(string kb, string rel)
    {
        var normalized = ChangeService.Normalize(rel);
        var state = Load(kb);
        if (state.ManualReviews.RemoveAll(m => ChangeService.Normalize(m.RelPath) == normalized) > 0)
            StateJson.Save(Paths(kb).Reviews, state);
    }

    public IReadOnlyList<ManualReview> ManualReviews(string kb) => Load(kb).ManualReviews;

    private ReviewsState Load(string kb)
    {
        try
        {
            var state = StateJson.Load<ReviewsState>(Paths(kb).Reviews);
            state ??= new ReviewsState();
            state.ManualReviews ??= [];
            return state;
        }
        catch
        {
            return new ReviewsState();
        }
    }

    private KbPaths Paths(string kb) => new(Path.Combine(config.WikisDir, kb));
}

public sealed record RuleStatus(
    string CurrentVersion,
    string ConfirmedVersion,
    string IgnoredVersion,
    bool NeedsReview);

public sealed class ReviewsState
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("confirmedRuleVersion")] public string ConfirmedRuleVersion { get; set; } = "";
    [JsonPropertyName("ignoredRuleVersion")] public string IgnoredRuleVersion { get; set; } = "";
    [JsonPropertyName("confirmedAt")] public string? ConfirmedAt { get; set; }
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("manualReviews")] public List<ManualReview> ManualReviews { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class ManualReview
{
    [JsonPropertyName("relPath")] public string RelPath { get; set; } = "";
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("at")] public string At { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
