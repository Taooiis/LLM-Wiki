using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LLM_Wiki.Services;

/// <summary>MCP 权限档位（设计 08§4）：默认关闭，开启后先只读，写入需单独档位。</summary>
public enum McpMode
{
    Off = 0,
    ReadOnly = 1,
    ReadWrite = 2,
}

/// <summary>MCP 统一错误码（设计 08§8）。异常消息以 "CODE: 详情" 形式返回给 Agent。</summary>
public static class McpErrorCodes
{
    public const string KbNotFound = "KB_NOT_FOUND";
    public const string PathNotAllowed = "PATH_NOT_ALLOWED";
    public const string ReadOnlyMode = "READ_ONLY_MODE";
    public const string WriteNotAllowedPath = "WRITE_NOT_ALLOWED_PATH";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string FileNotFound = "FILE_NOT_FOUND";
}

/// <summary>
/// MCP 访问策略：路径校验、读写禁区、大小上限集中在此（设计 08§6），
/// HTTP 与 stdio 两种传输共用，不依赖客户端传参可信度。
/// </summary>
public sealed class McpAccessPolicy(ConfigService config)
{
    /// <summary>读文件大小上限；与预览一致：超过即拒绝，避免把整个大文件塞给 Agent。</summary>
    public const int MaxReadBytes = 2 * 1024 * 1024;

    /// <summary>写入内容上限（字符数）。</summary>
    public const int MaxWriteChars = 2 * 1024 * 1024;

    /// <summary>单条路径长度上限（设计 08§6）。</summary>
    public const int MaxPathLength = 260;

    private volatile int _mode;

    public McpMode Mode
    {
        get => (McpMode)_mode;
        set => _mode = (int)value;
    }

    public bool WriteEnabled => Mode == McpMode.ReadWrite;

    public bool ServiceEnabled => Mode != McpMode.Off;

    /// <summary>知识库标识必须是现存库名，不接受任意路径（设计 08§6.5）。</summary>
    public bool KbExists(string? kb)
    {
        if (string.IsNullOrWhiteSpace(kb) || !PathGuard.ValidKb(kb)) return false;
        var dir = Path.Combine(config.WikisDir, kb);
        return Directory.Exists(dir) && !IsReparsePoint(dir);
    }

    /// <summary>
    /// 解析只读路径：拒绝绝对/UNC/.. /空段/控制字符，拒绝点号开头内部文件与符号链接，
    /// 且默认只允许 md/txt/json（设计 08§5.3）。
    /// </summary>
    public string? ResolveReadPath(string? kb, string? relativePath, out string errorCode)
    {
        errorCode = "";
        if (!KbExists(kb))
        {
            errorCode = McpErrorCodes.KbNotFound;
            return null;
        }

        var normalized = NormalizeRelative(relativePath, out var reason);
        if (normalized == null || normalized.Length == 0)
        {
            errorCode = McpErrorCodes.PathNotAllowed;
            return null;
        }

        // 点号开头的是内部状态文件（.changes.json 等），一律不可读
        if (normalized.Split('/').Any(s => s.StartsWith('.')))
        {
            errorCode = McpErrorCodes.PathNotAllowed;
            return null;
        }

        if (!IsReadableExtension(normalized))
        {
            errorCode = McpErrorCodes.PathNotAllowed;
            return null;
        }

        return ResolveWithinKb(kb!, normalized, out errorCode);
    }

    /// <summary>解析写入路径：必须位于 wiki/ 之下，且不在写入禁区（设计 08§5.4）。</summary>
    public string? ResolveWritePath(string? kb, string? relativePath, out string errorCode)
    {
        errorCode = "";
        if (!WriteEnabled)
        {
            errorCode = McpErrorCodes.ReadOnlyMode;
            return null;
        }
        if (!KbExists(kb))
        {
            errorCode = McpErrorCodes.KbNotFound;
            return null;
        }

        var normalized = NormalizeRelative(relativePath, out _);
        if (normalized == null || normalized.Length == 0 || !IsWritableFile(normalized))
        {
            errorCode = McpErrorCodes.WriteNotAllowedPath;
            return null;
        }

        return ResolveWithinKb(kb!, normalized, out errorCode);
    }

    /// <summary>路径是否允许出现在目录浏览结果中（过滤内部文件，设计 03§5）。</summary>
    public static bool IsVisibleInTree(string relativePath)
    {
        var n = relativePath.Replace('\\', '/');
        return n.Split('/').All(s => s.Length > 0 && !s.StartsWith('.'));
    }

    /// <summary>解析目录（list_tree 用）：空路径表示库根；同样拒绝越界与符号链接。</summary>
    public string? ResolveDirectory(string? kb, string? relativeDir, out string errorCode)
    {
        errorCode = "";
        if (!KbExists(kb))
        {
            errorCode = McpErrorCodes.KbNotFound;
            return null;
        }

        if (string.IsNullOrWhiteSpace(relativeDir))
        {
            return Path.GetFullPath(Path.Combine(config.WikisDir, kb!));
        }

        var normalized = NormalizeRelative(relativeDir, out _);
        if (normalized == null || normalized.Split('/').Any(s => s.StartsWith('.')))
        {
            errorCode = McpErrorCodes.PathNotAllowed;
            return null;
        }

        var path = ResolveWithinKb(kb!, normalized, out errorCode);
        if (path == null) return null;
        if (!Directory.Exists(path))
        {
            errorCode = McpErrorCodes.PathNotAllowed;
            return null;
        }
        return path;
    }

    private static bool IsReadableExtension(string rel)
    {
        var ext = Path.GetExtension(rel);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWritableFile(string rel)
    {
        var n = rel.Replace('\\', '/');
        if (!n.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase)) return false;
        if (n.Equals("wiki/log.md", StringComparison.OrdinalIgnoreCase)) return false;
        if (n.Split('/').Any(s => s.StartsWith('.'))) return false;
        var ext = Path.GetExtension(n);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 归一化相对路径。拒绝绝对路径、盘符、UNC、..、空段、控制字符、超长；
    /// 返回使用 "/" 分隔的干净相对路径。
    /// </summary>
    public static string? NormalizeRelative(string? rel, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(rel))
        {
            reason = "路径为空";
            return null;
        }

        var s = rel.Trim();
        if (s.Length > MaxPathLength)
        {
            reason = "路径过长";
            return null;
        }

        if (s.StartsWith('/') || s.StartsWith('\\') || s.StartsWith("//") || s.StartsWith("\\\\"))
        {
            reason = "不允许绝对或 UNC 路径";
            return null;
        }

        if (Path.IsPathRooted(s) || (s.Length >= 2 && s[1] == ':'))
        {
            reason = "不允许盘符或绝对路径";
            return null;
        }

        if (s.Any(char.IsControl) || s.Contains(':'))
        {
            reason = "路径包含非法字符";
            return null;
        }

        var parts = s.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".."))
        {
            reason = "路径包含空段或上级跳转";
            return null;
        }

        return string.Join('/', parts);
    }

    /// <summary>拼接并确认解析结果仍在库内；逐段拒绝符号链接/junction（设计 08§6.3、§6.4）。</summary>
    private string? ResolveWithinKb(string kb, string relative, out string errorCode)
    {
        errorCode = "";
        var root = Path.GetFullPath(Path.Combine(config.WikisDir, kb));
        var current = root;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) || File.Exists(current))
            {
                if (IsReparsePoint(current))
                {
                    errorCode = McpErrorCodes.PathNotAllowed;
                    return null;
                }
            }
        }

        var full = Path.GetFullPath(current);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = McpErrorCodes.PathNotAllowed;
            return null;
        }

        return full;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>版本号 = 文件内容 SHA256（与 .changes.json 指纹同口径）。</summary>
    public static string ContentVersion(string content) => HashUtil.Sha256Text(content);

    public static string FileVersion(string path) => HashUtil.Sha256(path);

    /// <summary>规范化展示路径（统一 "/"）。</summary>
    public static string ToDisplayPath(string path) => path.Replace('\\', '/');
}

/// <summary>MCP 服务端上下文：当前知识库由桌面端联动，stdio 端为启动时选定的库（设计 08§12）。</summary>
public sealed class McpServerContext
{
    private volatile string _currentKb = "";

    public string CurrentKb
    {
        get => _currentKb;
        set => _currentKb = value ?? "";
    }
}
