namespace LLM_Wiki.Services;

/// <summary>
/// 统一未保存协调器：各页面把自己的工作区/编辑器登记进来，
/// 切库、切根目录、关闭窗口前统一询问；不再由每个页面各自回滚全局状态。
/// </summary>
public sealed class UnsavedChangesCoordinator
{
    private readonly Dictionary<string, IUnsavedChangesGuard> _guards = new(StringComparer.Ordinal);

    public void Register(string key, IUnsavedChangesGuard guard) => _guards[key] = guard;

    public void Unregister(string key) => _guards.Remove(key);

    public bool HasAny => _guards.Values.Any(g => g.HasUnsavedChanges);

    public async Task<bool> CanLeaveAsync(CancellationToken cancellationToken = default)
    {
        foreach (var guard in _guards.Values.ToArray())
        {
            if (!guard.HasUnsavedChanges) continue;
            if (!await guard.CanLeaveAsync(cancellationToken)) return false;
        }
        return true;
    }
}

/// <summary>用委托实现一个按条件判定的保护器（规则编辑器等轻量场景）。</summary>
public sealed class DelegateUnsavedGuard(Func<bool> hasChanges, Func<Task<bool>> canLeave) : IUnsavedChangesGuard
{
    public bool HasUnsavedChanges => hasChanges();

    public Task<bool> CanLeaveAsync(CancellationToken cancellationToken = default) => canLeave();
}
