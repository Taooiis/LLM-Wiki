using System.Diagnostics;
using System.IO;

namespace LlmWiki.App.Services;

/// <summary>
/// 调起系统外壳：在资源管理器中定位文件、用默认程序打开文件、打开文件夹。
/// 只接受已由 PathGuard 校验过的库内绝对路径（调用方负责）。
/// </summary>
public static class ShellOps
{
    /// <summary>在资源管理器里打开并选中文件；路径是目录时直接打开该目录。</summary>
    public static bool Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            // explorer 的 /select 参数：路径带空格要自己加引号
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>用系统默认程序打开文件（如 .md 交给 VS Code / 编辑器）。</summary>
    public static bool Open(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
