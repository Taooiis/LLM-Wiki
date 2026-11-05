using System.Windows;

namespace LLM_Wiki.Helpers;

/// <summary>
/// UI 线程编组统一入口:服务后台事件(Changed/Finished 等)回调到 UI 线程。
/// 已在 UI 线程直接调用,否则 Post;Dispatcher 不可用(应用关闭中)时就地执行。
/// 用法一(事件回调):Ui.Run(RefreshStatusBar);
/// 用法二(方法开头编组):if (!Ui.IsUi) { Ui.Run(() => Show(args)); return; }
/// </summary>
public static class Ui
{
    /// <summary>当前是否已在 UI 线程;Dispatcher 不可用时视为 true(就地执行)。</summary>
    public static bool IsUi => Application.Current?.Dispatcher?.CheckAccess() ?? true;

    /// <summary>在 UI 线程执行动作;已在 UI 线程直接调用,否则 Post。</summary>
    public static void Run(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
