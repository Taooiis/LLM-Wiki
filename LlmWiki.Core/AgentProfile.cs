using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>本机 Agent 配置。只保存适配器身份、路径、模型选择和验证结果，不保存账号、API Key 或令牌。</summary>
public sealed class AgentProfile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("adapterId")] public string AdapterId { get; set; } = AgentTools.OpenCode;
    [JsonPropertyName("executablePath")] public string ExecutablePath { get; set; } = "";
    [JsonPropertyName("verifiedVersion")] public string VerifiedVersion { get; set; } = "";
    [JsonPropertyName("detected")] public bool Detected { get; set; }
    [JsonPropertyName("loggedIn")] public bool LoggedIn { get; set; }
    [JsonPropertyName("executionVerified")] public bool ExecutionVerified { get; set; }
    [JsonPropertyName("allowWorkspaceWrite")] public bool AllowWorkspaceWrite { get; set; }
    [JsonPropertyName("lastCheckedAt")] public string LastCheckedAt { get; set; } = "";
    [JsonPropertyName("loginDetail")] public string LoginDetail { get; set; } = "";
    // 任务执行使用的模型 ID（provider/model）；空 = 跟随外部 CLI 自己的默认模型。
    [JsonPropertyName("modelId")] public string ModelId { get; set; } = "";
    [JsonPropertyName("formatStatus")] public Dictionary<string, string> FormatStatus { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public bool Ready => AgentTools.IsCli(AdapterId) && Detected && LoggedIn && ExecutionVerified
                         && !string.IsNullOrWhiteSpace(ExecutablePath)
                         && File.Exists(ExecutablePath);
}

public sealed record AgentCheck(string Key, string Label, string Status, string Detail)
{
    public bool IsOk => Status is "通过" or "已验证" or "已检测";
}

public sealed class AgentProfileStore
{
    private readonly string _directory;
    public AgentProfileStore(string? directory = null) => _directory = directory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki");
    private string PathFor() => System.IO.Path.Combine(_directory, "agent-profile.json");

    public AgentProfile Load(string? adapterId = null)
    {
        try
        {
            var legacy = PathFor();
            var id = adapterId ?? AgentTools.OpenCode;
            var file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(legacy)!, "agent-profile." + id + ".json");
            if (!File.Exists(file))
            {
                // 兼容最早的单档案文件；旧 Codex 档案不再适用（已移除 Codex 支持）。
                var previous = File.Exists(legacy) ? StateJson.Load<AgentProfile>(legacy) : null;
                return previous?.AdapterId == id ? previous : new AgentProfile { AdapterId = id };
            }
            var loaded = File.Exists(file) ? StateJson.Load<AgentProfile>(file) : null;
            return loaded != null && AgentTools.IsCli(loaded.AdapterId)
                ? loaded
                : new AgentProfile { AdapterId = id };
        }
        catch
        {
            return new AgentProfile { AdapterId = adapterId ?? AgentTools.OpenCode };
        }
    }

    public void Save(AgentProfile profile)
    {
        var path = PathFor();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        StateJson.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "agent-profile." + profile.AdapterId + ".json"), profile);
        StateJson.Save(path, profile);
    }
}

/// <summary>CLI 进程执行共用工具：带超时的非交互命令运行与文本缩短。</summary>
internal static class CliProcess
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = new System.Diagnostics.Process { StartInfo = psi };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, "", "命令超时");
            }
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (Exception e)
        {
            return (-1, "", e.Message);
        }
    }

    public static string Shorten(string text, int max = 240)
    {
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}
