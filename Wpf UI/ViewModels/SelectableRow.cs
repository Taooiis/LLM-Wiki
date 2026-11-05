using CommunityToolkit.Mvvm.ComponentModel;

namespace LLM_Wiki.ViewModels;

/// <summary>
/// 可勾选行基类:列表/清单行的 IsSelected 单点定义。
/// 派生类通过 override OnSelectedChanged 附加行为(替代各自重复的 _isSelected 与事件样板)。
/// </summary>
public abstract partial class SelectableRow : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => OnSelectedChanged(value);

    /// <summary>勾选变化后触发;派生类按需 override。</summary>
    protected virtual void OnSelectedChanged(bool value) { }
}
