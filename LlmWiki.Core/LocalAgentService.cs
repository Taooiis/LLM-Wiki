using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

public enum AgentRunState
{
    Idle,
    Starting,
    Running,
    Cancelling,
    Ended,
    Failed,
    Cancelled,
    Interrupted,
}

/// <summary>本机 Codex CLI 执行服务：单任务、真实进程、双管道、输出上限、取消与归属核实。</summary>
public sealed class LocalAgentService
{
    private readonly AgentProfileStore _store;
    public AgentProfile LoadProfile(string id) => _store.Load(id);
    public void SelectProfile(string id)
    {
        if (IsRunning) return;
        Profile = _store.Load(id);
        RaiseChanged();
    }
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private Process? _process;
    private DateTime _processStartedAt;
    private string _processPath = "";
    private long _outputBytes;
    private int _outputLines;
    private int _onOutput;
    private bool _cancelRequested;

    public LocalAgentService(AgentProfileStore? store = null)
    {
        _store = store ?? new AgentProfileStore();
        Profile = _store.Load();
    }

    public AgentProfile Profile { get; private set; }
    public AgentRunState State { get; private set; } = AgentRunState.Idle;
    public string StatusText { get; private set; } = "空闲";
    public string OutputText
    {
        get { lock (_gate) return _output.ToString(); }
    }
    public bool OutputTruncated { get; private set; }
    public DateTime LastEventAt { get; private set; } = DateTime.Now;
    public string CurrentAttemptId { get; private set; } = "";
    public string CurrentKb { get; private set; } = "";

    public event Action? Changed;
    public event Action<AgentAttempt>? Finished;

    public bool IsRunning => State is AgentRunState.Starting or AgentRunState.Running or AgentRunState.Cancelling;

    public void ReloadProfile()
    {
        Profile = _store.Load();
        RaiseChanged();
    }

    public void SaveProfile(AgentProfile profile)
    {
        Profile = profile;
        _store.Save(profile);
        RaiseChanged();
    }

    public async Task<(List<AgentCheck> Checks, string Version, bool LoggedIn)> DetectAsync(string executable, CancellationToken ct, string? adapterId = null)
        => await AgentTools.Adapter(adapterId ?? Profile.AdapterId).DetectAsync(executable, ct);

    public async Task<(bool Success, string Detail, string Output)> ProbeAsync(string executable, CancellationToken ct, string? adapterId = null)
        => await AgentTools.Adapter(adapterId ?? Profile.AdapterId).ProbeExecutionAsync(executable, ct);

    public static string? FindExecutable() => CodexCliAdapter.FindExecutable();

    /// <summary>当前已验证的读取范围：首轮只把纯文本/结构化文本视为可读；Office/PDF/PPT/图片需另行验证。</summary>
    public bool CanReadSource(string relativePath)
        => Profile.Ready && SourceFormatRegistry.IsTextReadable(relativePath);

    /// <summary>启动本机任务；返回后进程在后台运行，完成通过 Finished 事件通知。</summary>
    public AgentAttempt? Start(string kb, string kbPath, string taskId, string prompt, bool allowWrite, out string error)
    {
        error = "";
        if (IsRunning)
        {
            error = "已有本机任务在运行；首版不排队，请先等待或取消当前任务。";
            return null;
        }
        if (!Profile.Ready)
        {
            error = "本机工具未通过安装、登录和实际执行验证，不能开始。";
            return null;
        }
        if (!Directory.Exists(kbPath))
        {
            error = "知识库目录不可访问。";
            return null;
        }

        var attempt = new AgentAttempt
        {
            SchemaVersion = 1,
            Id = DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Kb = kb,
            TaskId = taskId,
            AdapterId = Profile.AdapterId,
            Executable = Profile.ExecutablePath,
            Version = Profile.VerifiedVersion,
            Sandbox = Profile.AdapterId == AgentTools.OpenCode ? (allowWrite ? "opencode-build-policy" : "opencode-plan-policy") : allowWrite ? "workspace-write" : "read-only",
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            State = "starting",
        };
        var attemptsDir = Path.Combine(kbPath, ".llmwiki", "attempts");
        Directory.CreateDirectory(attemptsDir);
        StateJson.Save(Path.Combine(attemptsDir, attempt.Id + ".json"), attempt);

        try
        {
            var psi = AgentTools.Adapter(Profile.AdapterId).BuildStartInfo(Profile.ExecutablePath, kbPath, allowWrite);
            if (Profile.AdapterId == AgentTools.OpenCode)
            {
                var taskFile = Path.Combine(attemptsDir, attempt.Id + ".task.txt");
                File.WriteAllText(taskFile, prompt, new UTF8Encoding(false));
                psi.ArgumentList.Add("--file"); psi.ArgumentList.Add(taskFile);
            }
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data, false);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data, true);
            if (!process.Start())
            {
                error = "无法启动 " + AgentTools.Name(Profile.AdapterId) + " 进程。";
                process.Dispose();
                if (attempt.AdapterId == AgentTools.OpenCode)
                    try { File.Delete(Path.Combine(attemptsDir, attempt.Id + ".task.txt")); } catch { }
                attempt.State = "failed";
                attempt.Detail = error;
                SaveAttempt(attemptsDir, attempt);
                return null;
            }

            lock (_gate)
            {
                _output.Clear();
                _outputBytes = 0;
                _outputLines = 0;
                OutputTruncated = false;
                _process = process;
                _processStartedAt = process.StartTime;
                _processPath = Profile.ExecutablePath;
                _cancelRequested = false;
                CurrentAttemptId = attempt.Id;
                CurrentKb = kb;
                State = AgentRunState.Running;
                StatusText = "运行中 · 正在等待工具响应";
                LastEventAt = DateTime.Now;
            }
            RaiseChanged();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _ = Task.Run(async () =>
            {
                try
                {
                    if (attempt.AdapterId == CodexCliAdapter.AdapterId) await process.StandardInput.WriteAsync(prompt);
                    await process.StandardInput.FlushAsync();
                    process.StandardInput.Close();
                }
                catch (Exception e)
                {
                    OnLine("写入任务失败：" + e.Message, true);
                }
            });

            _ = Task.Run(() => FinishAsync(process, attemptsDir, attempt));
            return attempt;
        }
        catch (Exception e)
        {
            if (attempt.AdapterId == AgentTools.OpenCode)
                try { File.Delete(Path.Combine(attemptsDir, attempt.Id + ".task.txt")); } catch { }
            error = "启动本机工具失败：" + e.Message;
            attempt.State = "failed";
            attempt.Detail = error;
            SaveAttempt(attemptsDir, attempt);
            State = AgentRunState.Failed;
            StatusText = error;
            RaiseChanged();
            return null;
        }
    }

    /// <summary>
    /// 取消先请求（03§5 L66）：先置 Cancelling 并尝试优雅终止（有主窗口的进程发送关闭消息；
    /// 无窗口 CLI 进程没有主窗口，CloseMainWindow 返回 false，直接跳过）。
    /// 5 秒仍未结束再由用户决定是否强制结束，强杀路径保留在 ForceKill。
    /// </summary>
    public void RequestCancel()
    {
        if (!IsRunning) return;
        Process? process;
        lock (_gate)
        {
            process = _process;
            _cancelRequested = true;
            State = AgentRunState.Cancelling;
            StatusText = "已请求结束；等待进程停止。5 秒后可强制结束本次进程。";
        }
        try { process?.CloseMainWindow(); } catch { }
        RaiseChanged();
    }

    /// <summary>只结束能核实归属的本次进程树；不能误杀其他 Codex 会话。</summary>
    public bool ForceKill(out string error)
    {
        error = "";
        Process? process;
        lock (_gate) process = _process;
        if (process == null)
        {
            error = "没有可归属的本次进程。";
            return false;
        }
        try
        {
            if (process.HasExited) return true;
            if (!string.Equals(process.StartInfo.FileName, _processPath, StringComparison.OrdinalIgnoreCase))
            {
                error = "进程身份与本次启动不一致，已拒绝结束。";
                return false;
            }
            var started = process.StartTime;
            if (Math.Abs((started - _processStartedAt).TotalSeconds) > 2)
            {
                error = "进程启动时间与记录不一致，已拒绝结束。";
                return false;
            }
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception e)
        {
            error = "强制结束失败：" + e.Message;
            return false;
        }
    }

    private async Task FinishAsync(Process process, string attemptsDir, AgentAttempt attempt)
    {
        try
        {
            await process.WaitForExitAsync();
            // 进程已退出但继承管道可能尚未关闭；有限等待，避免无限卡运行。
            await Task.Delay(3000);
            attempt.ExitCode = process.ExitCode;
            attempt.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            bool cancelRequested;
            lock (_gate) cancelRequested = _cancelRequested;
            if (cancelRequested)
            {
                // 用户请求过取消且进程随后退出：取消成功；已写入的文件不自动回滚。
                attempt.State = "cancelled";
                attempt.Detail = "任务已取消，可能保留部分修改";
            }
            else if (process.ExitCode == 0)
            {
                attempt.State = "ended";
                attempt.Detail = "外部程序已结束，请核对结果";
            }
            else
            {
                attempt.State = "failed";
                attempt.Detail = "外部程序异常结束，退出码 " + process.ExitCode;
            }
            SaveAttempt(attemptsDir, attempt);
            lock (_gate)
            {
                _process = null;
                State = cancelRequested
                    ? AgentRunState.Cancelled
                    : process.ExitCode == 0 ? AgentRunState.Ended : AgentRunState.Failed;
                StatusText = attempt.Detail;
            }
        }
        catch (Exception e)
        {
            attempt.State = "failed";
            attempt.Detail = "等待进程结束失败：" + e.Message;
            SaveAttempt(attemptsDir, attempt);
            lock (_gate)
            {
                _process = null;
                State = AgentRunState.Failed;
                StatusText = attempt.Detail;
            }
        }
        finally
        {
            try { process.Dispose(); } catch { }
            if (attempt.AdapterId == AgentTools.OpenCode)
                try { File.Delete(Path.Combine(attemptsDir, attempt.Id + ".task.txt")); } catch { }
            RaiseChanged();
            try { Finished?.Invoke(attempt); } catch { }
        }
    }

    private void OnLine(string? line, bool stderr)
    {
        if (string.IsNullOrEmpty(line)) return;
        LastEventAt = DateTime.Now;
        lock (_gate)
        {
            if (++_onOutput % 8 == 0) RaiseChanged();
            var prefix = stderr ? "[stderr] " : "";
            var text = prefix + line + "\n";
            _output.Append(text);
            _outputBytes += Encoding.UTF8.GetByteCount(text);
            _outputLines++;
            while (_outputLines > 5000 || _outputBytes > 1024 * 1024)
            {
                var content = _output.ToString();
                var cut = content.IndexOf('\n');
                if (cut < 0) break;
                _output.Remove(0, cut + 1);
                _outputBytes -= Encoding.UTF8.GetByteCount(content[..(cut + 1)]);
                _outputLines--;
                OutputTruncated = true;
            }
        }
    }

    private static void SaveAttempt(string dir, AgentAttempt attempt)
    {
        try { StateJson.Save(Path.Combine(dir, attempt.Id + ".json"), attempt); } catch { }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }
}

public sealed class AgentAttempt
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kb")] public string Kb { get; set; } = "";
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("adapterId")] public string AdapterId { get; set; } = "";
    [JsonPropertyName("executable")] public string Executable { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("sandbox")] public string Sandbox { get; set; } = "";
    [JsonPropertyName("startedAt")] public string StartedAt { get; set; } = "";
    [JsonPropertyName("endedAt")] public string? EndedAt { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("exitCode")] public int? ExitCode { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
