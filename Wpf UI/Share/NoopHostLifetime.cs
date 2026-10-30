using Microsoft.Extensions.Hosting;

namespace LLM_Wiki.Share;

/// <summary>
/// 桌面应用内的 Kestrel 宿主不跟随控制台生命周期；服务只由用户操作或应用退出停止。
/// </summary>
public sealed class NoopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
