namespace LLM_Wiki.Services;

/// <summary>
/// 统一未保存协调器：各页面把自己的工作区/编辑器登记进来，
/// 切库、切根目录、关闭窗口前统一询问；不再由每个页面各自回滚全局状态。
/// 另登记保存/放弃委托，供 D13 退出保护（00§9.4）在合并对话框中一次性执行。
/// </summary>
public sealed class UnsavedChangesCoordinator
{
    private readonly Dictionary<string, IUnsavedChangesGuard> _guards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<Task<bool>>> _savers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _discards = new(StringComparer.Ordinal);

    public void Register(string key, IUnsavedChangesGuard guard) => _guards[key] = guard;

    public void Unregister(string key) => _guards.Remove(key);

    /// <summary>登记“直接保存，不再询问”的委托；返回 false 表示保存失败（保留草稿）。</summary>
    public void RegisterSaver(string key, Func<Task<bool>> saveAll) => _savers[key] = saveAll;

    /// <summary>登记“放弃本次缓冲”的委托；只丢弃未保存内容，不回滚已落盘结果。</summary>
    public void RegisterDiscarder(string key, Action discard) => _discards[key] = discard;

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

    /// <summary>D13“保存并取消任务后退出”：直接保存全部草稿，不再逐个弹 D02。</summary>
    public async Task<bool> SaveAllAsync()
    {
        foreach (var save in _savers.Values.ToArray())
        {
            if (!await save()) return false;
        }
        return true;
    }

    /// <summary>D13“放弃草稿”：只丢本次缓冲，不回滚已落盘内容。</summary>
    public void DiscardAll()
    {
        foreach (var discard in _discards.Values.ToArray())
        {
            discard();
        }
    }
}

/// <summary>用委托实现一个按条件判定的保护器（规则编辑器等轻量场景）。</summary>
public sealed class DelegateUnsavedGuard(Func<bool> hasChanges, Func<Task<bool>> canLeave) : IUnsavedChangesGuard
{
    public bool HasUnsavedChanges => hasChanges();

    public Task<bool> CanLeaveAsync(CancellationToken cancellationToken = default) => canLeave();
}
