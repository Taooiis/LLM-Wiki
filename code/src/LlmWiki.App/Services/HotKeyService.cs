using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LlmWiki.App.Services;

/// <summary>
/// 全局快捷键：通过 Win32 RegisterHotKey 注册系统级热键。
/// 目前注册 Ctrl+Shift+L 唤出/隐藏主窗口。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    // Win32 常量
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_L = 0x4C;

    private HwndSource? _source;
    private bool _registered;

    /// <summary>热键按下时触发。</summary>
    public event Action? HotKeyPressed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>挂到指定窗口的消息循环上。</summary>
    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        // 确保窗口已创建句柄
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        TryRegister(helper.Handle);
    }

    /// <summary>注册 Ctrl+Shift+L。</summary>
    private void TryRegister(IntPtr handle)
    {
        // id=1；失败一般是被别的程序占了，不抛异常
        _registered = RegisterHotKey(handle, 1, MOD_CONTROL | MOD_SHIFT, VK_L);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == 1)
        {
            HotKeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            if (_registered) UnregisterHotKey(_source.Handle, 1);
            _source = null;
        }
        _registered = false;
    }
}