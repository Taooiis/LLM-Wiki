using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLM_Wiki.Services;

/// <summary>
/// 应用配置：保持既有 config.json 的 wikisDir 键与未知字段；新增 lastKb / lastPages 使用独立键。
/// 配置写入先保留旧内容再合并，不让 UI 偏好覆盖用户的其它字段。
/// </summary>
public class ConfigService
{
    private readonly string _configFile;
    private readonly string _appRoot;
    private JsonObject _root = new();

    public ConfigService(string appRoot)
    {
        _appRoot = Path.GetFullPath(appRoot);
        _configFile = Path.Combine(_appRoot, "config.json");
        Load();

        var configured = GetString("wikisDir");
        var resolved = string.IsNullOrWhiteSpace(configured) ? null : PathGuard.ResolveWikisDir(configured, _appRoot);
        WikisDir = resolved ?? ResolveDefaultWikisDir();
        Theme = GetString("theme") is "Light" ? "Light" : "Dark";
        LastKb = GetString("lastKb");
        LastPages = ReadLastPages();
    }

    public string WikisDir { get; private set; }
    public string Theme { get; private set; }
    public string LastKb { get; private set; }

    /// <summary>每个库上次访问的页面（知识/资料/整理），用于恢复。</summary>
    public Dictionary<string, string> LastPages { get; }

    public string DefaultWikisDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LLM Wiki", "wikis");

    private string ResolveDefaultWikisDir()
    {
        // 兼容旧版本默认位置；已有数据不自动搬移。全新的用户落到 Documents/LLM Wiki/wikis。
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki", "wikis");
        if (Directory.Exists(legacy) && Directory.EnumerateFileSystemEntries(legacy).Any())
            return legacy;
        Directory.CreateDirectory(DefaultWikisDir);
        return DefaultWikisDir;
    }

    public void SetTheme(string theme, out string error)
    {
        error = "";
        var value = theme == "Light" ? "Light" : "Dark";
        _root["theme"] = value;
        if (!Save(out error)) return;
        Theme = value;
    }

    public void SetLastKb(string kb)
    {
        LastKb = kb ?? "";
        _root["lastKb"] = LastKb;
        Save(out _);
    }

    public void SetLastPage(string kb, string page)
    {
        if (string.IsNullOrWhiteSpace(kb)) return;
        LastPages[kb] = page;
        var obj = new JsonObject();
        foreach (var (key, value) in LastPages) obj[key] = value;
        _root["lastPages"] = obj;
        Save(out _);
    }

    public string LastPageFor(string kb)
        => LastPages.TryGetValue(kb, out var page) ? page : "knowledge";

    /// <summary>切换存储根目录：只换读取位置，不搬迁/删除原目录；写入前验证读写权限。</summary>
    public bool SetWikisDir(string input, out string error)
    {
        error = "";
        var dir = PathGuard.ResolveWikisDir(input, _appRoot);
        if (dir == null)
        {
            error = "请填写完整的绝对路径（如 D:\\知识库 或 \\\\电脑\\共享）";
            return false;
        }
        if (!CanUseDirectory(dir, out error)) return false;

        var previous = _root["wikisDir"]?.GetValue<string>() ?? WikisDir;
        _root["wikisDir"] = input.Trim();
        if (!Save(out error))
        {
            _root["wikisDir"] = previous;
            return false;
        }
        WikisDir = dir;
        return true;
    }

    public static bool CanUseDirectory(string path, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".llmwiki-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "无权限访问该目录";
            return false;
        }
        catch (IOException e)
        {
            error = "目录不可达：" + e.Message;
            return false;
        }
        catch (Exception e)
        {
            error = "目录不可用：" + e.Message;
            return false;
        }
    }

    private void Load()
    {
        if (!File.Exists(_configFile)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_configFile));
            if (node is JsonObject obj) _root = obj;
        }
        catch
        {
            // 坏配置不覆盖：保留文件，运行时先使用默认值
            _root = new JsonObject();
        }
    }

    private bool Save(out string error)
    {
        error = "";
        try
        {
            var tmp = _configFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, _root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            File.Move(tmp, _configFile, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            error = "写入配置失败：" + e.Message;
            return false;
        }
    }

    private string GetString(string key)
    {
        try { return _root[key]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    private Dictionary<string, string> ReadLastPages()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_root["lastPages"] is not JsonObject obj) return map;
        foreach (var (key, value) in obj)
        {
            try
            {
                if (value != null) map[key] = value.GetValue<string>();
            }
            catch { }
        }
        return map;
    }
}
