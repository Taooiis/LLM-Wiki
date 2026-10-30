using System.IO;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>本机 Agent 配置。只保存适配器身份、路径和验证结果，不保存账号、API Key 或令牌。</summary>
public sealed class AgentProfile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("adapterId")] public string AdapterId { get; set; } = "codex-cli";
    [JsonPropertyName("executablePath")] public string ExecutablePath { get; set; } = "";
    [JsonPropertyName("verifiedVersion")] public string VerifiedVersion { get; set; } = "";
    [JsonPropertyName("detected")] public bool Detected { get; set; }
    [JsonPropertyName("loggedIn")] public bool LoggedIn { get; set; }
    [JsonPropertyName("executionVerified")] public bool ExecutionVerified { get; set; }
    [JsonPropertyName("allowWorkspaceWrite")] public bool AllowWorkspaceWrite { get; set; }
    [JsonPropertyName("lastCheckedAt")] public string LastCheckedAt { get; set; } = "";
    [JsonPropertyName("loginDetail")] public string LoginDetail { get; set; } = "";
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
            var file = adapterId == null ? legacy : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(legacy)!, "agent-profile." + (adapterId switch { AgentTools.OpenCode => AgentTools.OpenCode, AgentTools.WorkBuddy => AgentTools.WorkBuddy, _ => CodexCliAdapter.AdapterId }) + ".json");
            if (adapterId != null && !File.Exists(file))
            {
                var previous = File.Exists(legacy) ? StateJson.Load<AgentProfile>(legacy) : null;
                return previous?.AdapterId == adapterId ? previous : new AgentProfile { AdapterId = adapterId };
            }
            return File.Exists(file) ? StateJson.Load<AgentProfile>(file) ?? new AgentProfile { AdapterId = adapterId ?? CodexCliAdapter.AdapterId } : new AgentProfile { AdapterId = adapterId ?? CodexCliAdapter.AdapterId };
        }
        catch
        {
            return new AgentProfile { AdapterId = adapterId ?? CodexCliAdapter.AdapterId };
        }
    }

    public void Save(AgentProfile profile)
    {
        var path = PathFor();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var id = profile.AdapterId switch { AgentTools.OpenCode => AgentTools.OpenCode, AgentTools.WorkBuddy => AgentTools.WorkBuddy, _ => CodexCliAdapter.AdapterId };
        StateJson.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "agent-profile." + id + ".json"), profile);
        StateJson.Save(path, profile);
    }
}

/// <summary>Codex CLI 原生非交互适配器：检测、实际执行探测、构建参数、解析事件和取消归属。</summary>
public sealed class CodexCliAdapter : ILocalAgentAdapter
{
    public const string AdapterId = "codex-cli";

    public static string? FindExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir.Trim(), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[]
                 {
                     System.IO.Path.Combine(local, "Programs", "codex", "codex.exe"),
                     System.IO.Path.Combine(local, "Programs", "Codex", "codex.exe"),
                     System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "codex.exe"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<(List<AgentCheck> Checks, string Version, bool LoggedIn)> DetectAsync(string executable, CancellationToken ct)
    {
        var checks = new List<AgentCheck>();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            checks.Add(new AgentCheck("install", "安装", "未通过", "没有找到 codex.exe；请选择真实原生命令行程序。"));
            return (checks, "", false);
        }
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new AgentCheck("install", "安装", "未通过", "首版只支持原生 codex.exe；不使用 .cmd/.ps1 包装冒充适配。"));
            return (checks, "", false);
        }

        var version = await RunAsync(executable, ["--version"], TimeSpan.FromSeconds(8), ct);
        var versionText = (version.Stdout + " " + version.Stderr).Trim();
        var versionOk = version.ExitCode == 0 && versionText.Length > 0;
        checks.Add(new AgentCheck("install", "安装", versionOk ? "已检测" : "未通过",
            versionOk ? Shorten(versionText) : "无法运行 --version：" + Shorten(versionText)));

        var execHelp = await RunAsync(executable, ["exec", "--help"], TimeSpan.FromSeconds(8), ct);
        var help = execHelp.Stdout + "\n" + execHelp.Stderr;
        var execOk = execHelp.ExitCode == 0 && help.Contains("exec", StringComparison.OrdinalIgnoreCase);
        var flagsOk = help.Contains("--json", StringComparison.OrdinalIgnoreCase)
                      && help.Contains("--sandbox", StringComparison.OrdinalIgnoreCase);
        checks.Add(new AgentCheck("exec", "非交互执行", execOk && flagsOk ? "已验证" : "未通过",
            execOk ? (flagsOk ? "exec、--json、--sandbox 参数可用" : "exec 可用，但缺少 --json/--sandbox；不能假定完整适配")
                   : "exec --help 失败：" + Shorten(help)));

        var login = await RunAsync(executable, ["login", "status"], TimeSpan.FromSeconds(8), ct);
        var loginText = (login.Stdout + " " + login.Stderr).Trim();
        var loggedIn = login.ExitCode == 0
                       && !loginText.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                       && (loginText.Contains("logged in", StringComparison.OrdinalIgnoreCase)
                           || loginText.Contains("authenticated", StringComparison.OrdinalIgnoreCase)
                           || loginText.Contains("已登录", StringComparison.OrdinalIgnoreCase));
        checks.Add(new AgentCheck("login", "登录", loggedIn ? "已检测" : "未验证",
            loggedIn ? Shorten(loginText) : "需要在 Codex 外部完成登录；应用不读取认证文件。 " + Shorten(loginText)));

        return (checks, versionOk ? Shorten(versionText) : "", loggedIn);
    }

    /// <summary>在隔离临时目录实际跑一次非交互任务；消耗已有账号额度。</summary>
    public async Task<(bool Success, string Detail, string Output)> ProbeExecutionAsync(string executable, CancellationToken ct)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "llmwiki-codex-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var psi = BuildStartInfo(executable, temp, allowWrite: false);
            using var process = new System.Diagnostics.Process { StartInfo = psi };
            if (!process.Start())
                return (false, "无法启动 Codex 进程", "");
            await process.StandardInput.WriteAsync("只回复 OK，不要修改任何文件。");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "实际执行验证超时（90 秒）", "");
            }
            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            var success = process.ExitCode == 0 && output.Length > 0;
            return (success,
                success ? "实际非交互执行通过" : $"退出码 {process.ExitCode}：{Shorten(error.Length > 0 ? error : output)}",
                Shorten(output, 1000));
        }
        catch (Exception e)
        {
            return (false, "实际执行失败：" + e.Message, "");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    public System.Diagnostics.ProcessStartInfo BuildStartInfo(string executable, string kbPath, bool allowWrite)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = kbPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--color");
        psi.ArgumentList.Add("never");
        psi.ArgumentList.Add("--cd");
        psi.ArgumentList.Add(kbPath);
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add(allowWrite ? "workspace-write" : "read-only");
        psi.ArgumentList.Add("-");
        return psi;
    }

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
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

    private static string Shorten(string text, int max = 240)
    {
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}
