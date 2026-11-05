namespace LLM_Wiki.Services;

/// <summary>
/// 全局调优参数集中点:轮询间隔、节流、缓冲上限、锁重试。
/// 需要改成用户可见设置时,从这里接入 ConfigService,不必再散改各服务。
/// </summary>
public static class TuningOptions
{
    /// <summary>主窗口状态栏轮询间隔。</summary>
    public static readonly TimeSpan StatusBarInterval = TimeSpan.FromSeconds(2);

    /// <summary>任务清理(过期/超限删除)全量扫描的最小间隔;节流避免每次 Recent 都全量读任务文件。</summary>
    public static readonly TimeSpan TaskCleanupInterval = TimeSpan.FromSeconds(60);

    /// <summary>agent 流式输出上屏节流(毫秒);首行始终立即上屏。</summary>
    public const int AgentOutputUiThrottleMs = 200;

    /// <summary>agent 输出缓冲保留上限;超出从头部丢弃旧行。</summary>
    public const int AgentOutputMaxLines = 5000;
    public const long AgentOutputMaxBytes = 1024 * 1024;

    /// <summary>跨进程文件锁的等待上限与重试间隔。</summary>
    public const int FileLockTimeoutMs = 4000;
    public const int FileLockRetryMs = 80;

    /// <summary>扫描时文件正在写入(大小/时间戳不稳定)的重试间隔。</summary>
    public const int ScanUnstableRetryMs = 80;
}
