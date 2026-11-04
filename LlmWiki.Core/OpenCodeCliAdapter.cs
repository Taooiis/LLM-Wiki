using System.Diagnostics;
using System.Text;

namespace LLM_Wiki.Services;

/// <summary>
/// 本机 CLI 适配器契约（适配器模式）：检测、模型列表、实际执行探测、构建启动参数。
/// 接入新 CLI 时：实现本接口（元数据随接口提供），并在 <see cref="AgentTools"/> 注册表加一行；
/// 服务层与 UI 从注册表动态生成，无需改动。模型选择是契约的一部分：各适配器决定如何传递。
/// </summary>
public interface ILocalAgentAdapter
{
    /// <summary>适配器稳定 ID（用于配置档案与注册，如 "opencode-cli"）。</summary>
    string Id { get; }
    /// <summary>UI 显示名（如 "OpenCode CLI"）。</summary>
    string DisplayName { get; }
    /// <summary>可执行文件名（用于路径提示与文件对话框过滤）。</summary>
    string ExecutableName { get; }
    /// <summary>在 PATH 与常见安装目录自动查找可执行文件；找不到返回 null。</summary>
    string? FindExecutable();
    Task<(List<AgentCheck> Checks, string Version, bool LoggedIn)> DetectAsync(string executable, CancellationToken ct);
    /// <summary>拉取该 CLI 可用的模型 ID 列表（只读命令，不含凭据）；不支持模型选择的适配器返回空列表。</summary>
    Task<List<string>> ListModelsAsync(string executable, CancellationToken ct);
    Task<(bool Success, string Detail, string Output)> ProbeExecutionAsync(string executable, string? model, CancellationToken ct);
    ProcessStartInfo BuildStartInfo(string executable, string kbPath, bool allowWrite, string? model);
    /// <summary>把 CLI 的一行原始输出转换为面板摘要；返回 null 表示该行无信息量、不上屏。</summary>
    string? FormatOutputLine(string rawLine);
    /// <summary>若该行是 AI 的正文发言则返回文本（作为任务结论的素材），否则返回 null。</summary>
    string? ExtractAiText(string rawLine);
}

/// <summary>注册表条目：新 CLI 在 <see cref="AgentTools"/> 注册一行即可被 UI 与服务层发现。</summary>
public sealed record AgentToolDescriptor(string Id, string DisplayName, Func<ILocalAgentAdapter> Create);

public static class AgentTools
{
    public const string OpenCode = "opencode-cli";

    // 新 CLI 接入点：实现 ILocalAgentAdapter 后在此加一行，下拉/工厂/名称/查找全部自动生效。
    private static readonly AgentToolDescriptor[] _tools =
    [
        new(OpenCode, "OpenCode CLI", () => new OpenCodeCliAdapter()),
    ];

    public static bool IsCli(string id) => _tools.Any(t => t.Id == id);

    /// <summary>全部已注册工具（UI 下拉数据源）。</summary>
    public static IReadOnlyList<AgentToolDescriptor> Tools => _tools;

    /// <summary>适配器工厂：按 ID 创建。</summary>
    public static ILocalAgentAdapter Adapter(string id) =>
        _tools.FirstOrDefault(t => t.Id == id)?.Create()
        ?? throw new InvalidOperationException("该工具不支持 CLI 自动执行。");

    /// <summary>显示名；未注册的 ID 回落为通用名。</summary>
    public static string Name(string id) =>
        _tools.FirstOrDefault(t => t.Id == id)?.DisplayName ?? "本机 CLI";

    /// <summary>显示名反查 ID；未注册的显示名回落为默认工具。</summary>
    public static string IdOf(string displayName) =>
        _tools.FirstOrDefault(t => t.DisplayName == displayName)?.Id ?? _tools[0].Id;

    public static string? FindExecutable(string id) =>
        _tools.FirstOrDefault(t => t.Id == id)?.Create().FindExecutable();
}

/// <summary>Native OpenCode run adapter. Official CLI: run/--format/--file.
/// Credentials remain in OpenCode. No auto approval or changes to its configuration.</summary>
public sealed class OpenCodeCliAdapter : ILocalAgentAdapter
{
    public string Id => AgentTools.OpenCode;
    public string DisplayName => "OpenCode CLI";
    public string ExecutableName => "opencode.exe";

    public string? FindExecutable()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
            });
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Path.Combine(d.Trim().Trim('"'), "opencode.exe")).FirstOrDefault(File.Exists);
    }

    public async Task<(List<AgentCheck> Checks, string Version, bool LoggedIn)> DetectAsync(string executable, CancellationToken ct)
    {
        var checks = new List<AgentCheck>();
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return ([new("install", "安装", "未通过", "请选择 OpenCode 原生 opencode.exe，不执行 shell 包装。")], "", false);
        var version = await CliProcess.RunAsync(executable, ["--version"], TimeSpan.FromSeconds(10), ct);
        var help = await CliProcess.RunAsync(executable, ["run", "--help"], TimeSpan.FromSeconds(10), ct);
        var installed = version.ExitCode == 0 && !string.IsNullOrWhiteSpace(version.Stdout);
        var text = help.Stdout + help.Stderr;
        // OpenCode v2 移除了 --dir,库根由进程 WorkingDirectory 决定,不再检测该参数
        var supported = help.ExitCode == 0 && new[] { "--format", "--file", "--agent" }.All(text.Contains);
        checks.Add(new("install", "安装", installed ? "已检测" : "未通过", installed ? "OpenCode 版本检测成功" : "无法读取版本"));
        checks.Add(new("exec", "非交互执行", supported ? "已验证" : "未通过", supported ? "run/JSON/附件/agent 参数可用" : "当前 OpenCode 版本缺少所需参数"));
        // auth list 是 CLI 自己的只读查询（列出已存储凭据），应用不读取认证文件
        var auth = await CliProcess.RunAsync(executable, ["auth", "list"], TimeSpan.FromSeconds(10), ct);
        var authText = auth.Stdout + " " + auth.Stderr;
        var loggedIn = auth.ExitCode == 0 && authText.Contains("stored", StringComparison.OrdinalIgnoreCase);
        checks.Add(new("login", "账号与模型", loggedIn ? "已检测" : "未验证",
            loggedIn ? "已登录（存在已存储凭据）；模型可用性以实际执行验证为准"
                     : "未发现已存储凭据；请在 OpenCode 中运行 opencode auth login。"));
        return (checks, CliProcess.Shorten(version.Stdout), loggedIn);
    }

    /// <summary>opencode models 输出每行一个 provider/model；只保留含 "/" 的行，失败返回空列表。</summary>
    public async Task<List<string>> ListModelsAsync(string executable, CancellationToken ct)
    {
        var result = await CliProcess.RunAsync(executable, ["models"], TimeSpan.FromSeconds(15), ct);
        if (result.ExitCode != 0) return [];
        return result.Stdout.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains('/', StringComparison.Ordinal) && !l.StartsWith('~'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProcessStartInfo BuildStartInfo(string executable, string kbPath, bool allowWrite, string? model)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = kbPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // v2 已无 --dir 参数,WorkingDirectory=kbPath 即库根,OpenCode 从 cwd 发现项目
        var args = new List<string> { "run", "请读取附加的任务说明并执行；只处理任务中明确授权的范围。", "--format", "json", "--agent", allowWrite ? "build" : "plan" };
        // 用户显式选择模型时经命令行参数传递；留空则跟随 OpenCode 自己的默认配置
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model); }
        foreach (var arg in args) psi.ArgumentList.Add(arg);
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

    /// <summary>面板摘要：--format json 的原始流含完整文件内容，直接展示会淹没用户；这里转成一行人类可读摘要。</summary>
    public string? FormatOutputLine(string rawLine)
    {
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(rawLine);
            var type = json?["type"]?.GetValue<string>();
            if (type is "step_start" or "step_finish") return null; // 流程框架事件，无信息量
            if (type == "error")
                return "⚠ " + (json?["error"]?["message"]?.GetValue<string>() ?? "执行出错");
            if (type == "text")
            {
                var text = json?["part"]?["text"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(text) ? null : "AI：" + text.Trim();
            }
            if (type == "tool")
            {
                var part = json?["part"];
                var tool = part?["tool"]?.GetValue<string>() ?? "工具";
                var done = part?["state"]?["status"]?.GetValue<string>() == "completed";
                var target = DescribeToolInput(part?["state"]?["input"]);
                return (done ? "✔ " : "▶ ") + tool + (string.IsNullOrEmpty(target) ? "" : " " + target);
            }
            return null;
        }
        catch { return rawLine; } // 非 JSON 行（如混入的日志）原样保留
    }

    /// <summary>AI 的正文发言（text 事件）即任务结论素材；工具调用与流程事件不参与。</summary>
    public string? ExtractAiText(string rawLine)
    {
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(rawLine);
            if (json?["type"]?.GetValue<string>() != "text") return null;
            var text = json["part"]?["text"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch { return null; }
    }

    /// <summary>提取工具调用的关键参数做摘要（文件名/模式/命令），只取前 60 字符。</summary>
    private static string DescribeToolInput(System.Text.Json.Nodes.JsonNode? input)
    {
        if (input is not System.Text.Json.Nodes.JsonObject obj) return "";
        foreach (var key in new[] { "path", "filePath", "pattern", "command", "url", "query" })
        {
            if (obj[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                var text = key == "path" ? System.IO.Path.GetFileName(s) : s;
                return text.Length > 60 ? " " + text[..60] + "…" : " " + text;
            }
        }
        return "";
    }

    public async Task<(bool Success, string Detail, string Output)> ProbeExecutionAsync(string executable, string? model, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmwiki-opencode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var marker = "LLMWIKI_" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(dir, "probe.txt"), marker, ct);
            var task = Path.Combine(dir, "task.txt");
            await File.WriteAllTextAsync(task, "只读取当前目录的 probe.txt，原样回复其内容。不要修改文件或执行命令。", ct);
            var psi = BuildStartInfo(executable, dir, false, model);
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
