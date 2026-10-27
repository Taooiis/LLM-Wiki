using System.IO;
using Microsoft.Win32;

namespace LlmWiki.App.Services;

/// <summary>
/// 文件关联注册 / 解除：双击 .md/.txt/.docx/.xlsx 打开本程序，
/// 右键菜单「发送到 LLM Wiki」把文件导入当前知识库。
/// 纯 Win32 注册表 API，无需额外依赖包。
/// </summary>
public static class FileAssociation
{
    private const string AppName = "LLM Wiki";

    private static string ExePath
    {
        get
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "LlmWiki.App.exe"); }
            catch { return Path.Combine(AppContext.BaseDirectory, "LlmWiki.App.exe"); }
        }
    }

    public static readonly string[] Extensions = { ".md", ".txt", ".docx", ".xlsx" };

    /// <summary>是否已注册过（检查任意扩展名是否有关联）。</summary>
    public static bool IsRegistered()
    {
        foreach (var ext in Extensions)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}");
                if (k != null)
                {
                    var expected = "LlmWiki." + ext.TrimStart('.');
                    if (k.GetValue("")?.ToString() == expected) return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>注册：双击打开 + 右键「发送到」。</summary>
    public static bool Register()
    {
        try
        {
            // 程序入口
            using (var appKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\LlmWiki.App.exe\shell\open\command"))
            {
                appKey.SetValue(null, $"\"{ExePath}\" \"%1\"");
            }

            // 逐个扩展名
            foreach (var ext in Extensions)
            {
                using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
                extKey.SetValue(null, $"LlmWiki.{ext.TrimStart('.')}");

                using var typeKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\LlmWiki.{ext.TrimStart('.')}");
                typeKey.SetValue(null, AppName + " " + ext.TrimStart('.'));
                typeKey.SetValue("AppUserModel.ApplyNow", 1, RegistryValueKind.DWord);

                using var openKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\LlmWiki.{ext.TrimStart('.')}\shell\open\command");
                openKey.SetValue(null, $"\"{ExePath}\" \"%1\"");
            }

            // 右键菜单「发送到 LLM Wiki」（文件夹背景）
            using (var sendToKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Directory\Background\shell\LlmWikiSendTo"))
            {
                sendToKey.SetValue(null, AppName);
                sendToKey.SetValue("Icon", ExePath);
            }
            using (var sendToCmd = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Directory\Background\shell\LlmWikiSendTo\command"))
            {
                sendToCmd.SetValue(null, $"\"{ExePath}\" \"%V\"");
            }

            // 文件右键菜单
            using (var fileSendTo = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\shell\LlmWikiSendTo"))
            {
                fileSendTo.SetValue(null, "发送到 " + AppName);
                fileSendTo.SetValue("Icon", ExePath);
            }
            using (var fileCmd = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\shell\LlmWikiSendTo\command"))
            {
                fileCmd.SetValue(null, $"\"{ExePath}\" \"%1\"");
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>解除所有关联。</summary>
    public static bool Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\LlmWiki.App.exe", false);
            foreach (var ext in Extensions)
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}", false);
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\LlmWiki.{ext.TrimStart('.')}", false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\LlmWikiSendTo", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\shell\LlmWikiSendTo", false);
            return true;
        }
        catch { return false; }
    }
}
