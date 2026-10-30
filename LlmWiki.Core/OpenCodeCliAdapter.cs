using System.Diagnostics;
using System.Text;

namespace LLM_Wiki.Services;

public interface ILocalAgentAdapter
{
    Task<(List<AgentCheck> Checks, string Version, bool LoggedIn)> DetectAsync(string executable, CancellationToken ct);
    Task<(bool Success, string Detail, string Output)> ProbeExecutionAsync(string executable, CancellationToken ct);
    ProcessStartInfo BuildStartInfo(string executable, string kbPath, bool allowWrite);
}

public static class AgentTools
{
    public const string OpenCode = "opencode-cli";
    public const string WorkBuddy = "workbuddy-desktop";
    public static bool IsCli(string id) => id is CodexCliAdapter.AdapterId or OpenCode;
    public static string Name(string id) => id switch
    {
        OpenCode => "OpenCode CLI",
        WorkBuddy => "WorkBuddy 桌面",
        _ => "Codex CLI"
    };
    public static ILocalAgentAdapter Adapter(string id) => id switch
    {
        CodexCliAdapter.AdapterId => new CodexCliAdapter(),
        OpenCode => new OpenCodeCliAdapter(),
        _ => throw new InvalidOperationException("该桌面工具使用 MCP/复制任务接入，不支持 CLI 自动执行。")
    };
    public static string? FindExecutable(string id)
    {
        if (id == CodexCliAdapter.AdapterId) return CodexCliAdapter.FindExecutable();
        var file = id == OpenCode ? "opencode.exe" : "WorkBuddy.exe";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WorkBuddy"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "workbuddy"),
            });
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Path.Combine(d.Trim().Trim('"'), file)).FirstOrDefault(File.Exists);
    }
}

/// <summary>Native OpenCode run adapter. Official CLI: run/--format/--dir/--file.
/// Credentials remain in OpenCode. No auto approval or changes to its configuration.</summary>
public sealed class OpenCodeCliAdapter : ILocalAgentAdapter
{
    public async Task<(List<AgentCheck> Checks, string Version, bool LoggedIn)> DetectAsync(string executable, CancellationToken ct)
    {
        var checks = new List<AgentCheck>();
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return ([new("install", "安装", "未通过", "请选择 OpenCode 原生 opencode.exe，不执行 shell 包装。")], "", false);
        var version = await CodexCliAdapter.RunAsync(executable, ["--version"], TimeSpan.FromSeconds(10), ct);
        var help = await CodexCliAdapter.RunAsync(executable, ["run", "--help"], TimeSpan.FromSeconds(10), ct);
        var installed = version.ExitCode == 0 && !string.IsNullOrWhiteSpace(version.Stdout);
        var text = help.Stdout + help.Stderr;
        var supported = help.ExitCode == 0 && new[] { "--format", "--file", "--dir", "--agent" }.All(text.Contains);
        checks.Add(new("install", "安装", installed ? "已检测" : "未通过", installed ? "OpenCode 版本检测成功" : "无法读取版本"));
        checks.Add(new("exec", "非交互执行", supported ? "已验证" : "未通过", supported ? "run/JSON/目录/附件/agent 参数可用" : "当前 OpenCode 版本缺少所需参数"));
        checks.Add(new("login", "账号与模型", "未验证", "在 OpenCode 中完成登录；实际执行验证通过后确认可用，不读取认证文件。"));
        return (checks, version.Stdout.Trim(), false);
    }

    public ProcessStartInfo BuildStartInfo(string executable, string kbPath, bool allowWrite)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = kbPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in new[] { "run", "请读取附加的任务说明并执行；只处理任务中明确授权的范围。", "--format", "json", "--dir", kbPath, "--agent", allowWrite ? "build" : "plan" })
            psi.ArgumentList.Add(arg);
        // Restrictions apply to this process only; OpenCode policies are not OS sandboxing.
        if (!allowWrite)
        {
            psi.Environment.TryGetValue("OPENCODE_PERMISSION", out var inherited);
            System.Text.Json.Nodes.JsonNode? parsed = string.IsNullOrWhiteSpace(inherited) ? new System.Text.Json.Nodes.JsonObject() : System.Text.Json.Nodes.JsonNode.Parse(inherited);
            if (parsed is System.Text.Json.Nodes.JsonObject rules)
            {
                foreach (var denied in new[] { "edit", "bash", "external_directory", "question", "task" }) rules[denied] = "deny";
                psi.Environment["OPENCODE_PERMISSION"] = rules.ToJsonString();
            }
            else throw new InvalidOperationException("现有 OPENCODE_PERMISSION 不是规则对象，无法叠加只读限制，请检查 OpenCode 权限配置。");
        }
        return psi;
    }

    public async Task<(bool Success, string Detail, string Output)> ProbeExecutionAsync(string executable, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmwiki-opencode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var marker = "LLMWIKI_" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(dir, "probe.txt"), marker, ct);
            var task = Path.Combine(dir, "task.txt");
            await File.WriteAllTextAsync(task, "只读取当前目录的 probe.txt，原样回复其内容。不要修改文件或执行命令。", ct);
            var psi = BuildStartInfo(executable, dir, false);
            psi.ArgumentList.Add("--file"); psi.ArgumentList.Add(task);
            using var process = new Process { StartInfo = psi };
            process.Start(); process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var text = await output; _ = await error;
                var ok = process.ExitCode == 0 && text.Contains(marker, StringComparison.Ordinal);
                return (ok, ok ? "OpenCode 实际执行及纯文本读取验证通过" : "验证未通过：检查 OpenCode 账号、模型、权限与文件读取能力。", "");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                try { await Task.WhenAll(output, error); } catch { }
                return (false, "OpenCode 验证已取消或超时（90秒）", "");
            }
        }
        catch (Exception e) { return (false, "OpenCode 验证失败：" + e.GetType().Name, ""); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
