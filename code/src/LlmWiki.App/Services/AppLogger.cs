using System.IO;

namespace LlmWiki.App.Services;

/// <summary>
/// 应用级运行时日志：异常、事件、收集到 %APPDATA%\LlmWiki\logs.txt。
/// 写失败不影响主流程（try 吞错），日志太大会被截断或自动归档。
/// </summary>
public static class AppLogger
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LlmWiki", "logs.txt");

    /// <summary>追加一行日志。会自动创建目录。</summary>
    public static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level}: {message}";
            File.AppendAllText(LogFile, line + "\n");
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}