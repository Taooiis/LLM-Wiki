using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLM_Wiki.Services;

/// <summary>
/// 存储路径配置：AppRoot/config.json，键 wikisDir 与 Demo 同键同义（spec 04 §1 互导）。
/// </summary>
public class ConfigService
{
    private readonly string _configFile;
    private readonly string _appRoot;

    /// <summary>默认库目录：%APPDATA%\LlmWiki\wikis（免丢）</summary>
    private static readonly string DefaultWikis = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki", "wikis");

    public ConfigService(string appRoot)
    {
        appRoot = Path.GetFullPath(appRoot);
        _appRoot = appRoot;
        _configFile = Path.Combine(appRoot, "config.json");

        // 实例级初始化：读取配置 → 若位置在 exe 目录下（旧默认），自动搬到 %APPDATA%
        string? configured = null;
        string theme = "Dark";
        if (File.Exists(_configFile))
        {
            try
            {
                var c = JsonSerializer.Deserialize<WikisConfig>(File.ReadAllText(_configFile));
                if (!string.IsNullOrWhiteSpace(c?.WikisDir)) configured = c.WikisDir;
                if (!string.IsNullOrWhiteSpace(c?.Theme)) theme = c.Theme;
            }
            catch { /* 坏 JSON 回退默认 */ }
        }

        // 解析后得到实际目录
        string? dir = configured == null ? null : PathGuard.ResolveWikisDir(configured, appRoot);
        // 仅旧默认目录（appRoot\wikis）才搬走到 %APPDATA%；用户自定义的目录留原地
        string oldDefault = Path.Combine(appRoot, "wikis");
        if (dir != null && Path.GetFullPath(dir).Equals(oldDefault, StringComparison.OrdinalIgnoreCase))
        {
            dir = MigrateFromBinDir(dir, DefaultWikis);
        }

        WikisDir = dir ?? (Directory.Exists(DefaultWikis) ? DefaultWikis : CreateDefaultWikis());
        Theme = theme;
    }

    /// <summary>搬库：从旧目录复制到 %APPDATA%，写配置并返回新路径。</summary>
    private static string MigrateFromBinDir(string oldDir, string newDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newDir) ?? newDir);
            CopyDirectory(oldDir, newDir);
            return newDir;
        }
        catch { return newDir; }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var subDir in Directory.GetDirectories(src))
            CopyDirectory(subDir, dst + Path.DirectorySeparatorChar + Path.GetFileName(subDir));
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, dst + Path.DirectorySeparatorChar + Path.GetFileName(file), true);
    }

    private static string CreateDefaultWikis()
    {
        Directory.CreateDirectory(DefaultWikis);
        File.WriteAllText(Path.Combine(DefaultWikis, ".gitkeep"), "LLM Wiki 库目录 / LLM Wiki Knowledge Base\n");
        return DefaultWikis;
    }

    // 当前存储根目录（构造时读取，SetWikisDir 更新）
    public string WikisDir { get; private set; }

    /// <summary>主题偏好："Dark" / "Light"，默认 Dark（设计文档 06§5）。</summary>
    public string Theme { get; private set; } = "Dark";

    /// <summary>记住主题选择：写 config.json（与 wikisDir 共存，不互相覆盖）。</summary>
    public bool SetTheme(string theme, out string error)
    {
        error = "";
        var v = theme == "Light" ? "Light" : "Dark";
        try
        {
            WriteConfig(v, WikisDir);
            Theme = v;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>改存储路径：校验合法 → 建目录 → 写 config.json → 立即生效。</summary>
    public bool SetWikisDir(string v, out string error)
    {
        error = "";
        var dir = PathGuard.ResolveWikisDir(v, _appRoot);
        if (dir == null)
        {
            error = "请填完整的绝对路径（如 D:\\知识库 或 \\\\电脑\\共享）或相对路径";
            return false;
        }
        try { Directory.CreateDirectory(dir); }
        catch (Exception e)
        {
            error = "路径不可用，无法创建目录：" + e.Message;
            return false;
        }
        try { WriteConfig(Theme, v.Trim()); }
        catch (Exception e)
        {
            error = "写入配置失败：" + e.Message;
            return false;
        }
        WikisDir = dir;
        return true;
    }

    /// <summary>写 config.json：wikisDir 保持用户输入原文（与 Demo 互导），theme 一并保存。</summary>
    private void WriteConfig(string theme, string wikisDir)
    {
        File.WriteAllText(_configFile, JsonSerializer.Serialize(
            new WikisConfig { WikisDir = wikisDir, Theme = theme },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private sealed class WikisConfig
    {
        // 键名与 Demo config.json 逐字一致（小写 wikisDir），保证互导
        [JsonPropertyName("wikisDir")]
        public string WikisDir { get; set; } = "";

        [JsonPropertyName("theme")]
        public string Theme { get; set; } = "Dark";
    }
}
