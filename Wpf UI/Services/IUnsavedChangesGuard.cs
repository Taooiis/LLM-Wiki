namespace LLM_Wiki.Services;

/// <summary>
/// 统一未保存内容保护（指南 §15.2）。
/// 文档工作区等实现方提供"是否存在未保存内容"与"是否允许离开"的判定；
/// 切换文件/知识库/页面/关闭窗口前必须经此询问，不允许各页面自行实现。
/// </summary>
public interface IUnsavedChangesGuard
{
    /// <summary>当前是否存在需要保护的未保存内容。</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>
    /// 询问是否可以离开当前位置。
    /// 返回 true：用户明确选择离开，或保存成功；
    /// 返回 false：必须停留在当前位置（含"保存后离开"但保存失败的情况）。
    /// </summary>
    Task<bool> CanLeaveAsync(CancellationToken cancellationToken = default);
}
