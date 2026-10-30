using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using LLM_Wiki.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LLM_Wiki.Share;

/// <summary>一次 MCP 工具调用记录（不含参数与正文，设计 08§9）。</summary>
public record McpCallRecord(DateTime Time, string Tool, bool IsError)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string StatusText => IsError ? "失败" : "成功";
}

/// <summary>
/// MCP HTTP 宿主（设计 08§7）：独立 Kestrel 端点，Bearer 令牌 + Origin 校验，
/// 能力实现全部在 Core；本类只做协议与生命周期适配。
/// </summary>
public sealed class McpService
{
    public const int DefaultPort = 8766;

    private readonly ConfigService _config;
    private readonly FileBridge _files;
    private readonly KbService _kbs;
    private readonly WorkflowPromptService _prompts;
    private readonly object _gate = new();
    private readonly List<McpCallRecord> _recentCalls = new();
    private WebApplication? _app;
    private string _token = "";

    public McpService(
        ConfigService config,
        FileBridge files,
        KbService kbs,
        WorkflowPromptService prompts,
        McpAccessPolicy policy,
        McpServerContext context)
    {
        _config = config;
        _files = files;
        _kbs = kbs;
        _prompts = prompts;
        Policy = policy;
        Context = context;
        _token = LoadOrCreateToken();
    }

    public McpAccessPolicy Policy { get; }

    public McpServerContext Context { get; }

    public bool Running => _app != null;

    public int Port { get; private set; } = DefaultPort;

    /// <summary>是否监听局域网。默认仅 127.0.0.1（设计 08§7.1）。</summary>
    public bool AllowLan { get; private set; }

    public string Token => _token;

    public string LastError { get; private set; } = "";

    public string LocalEndpoint => Running ? $"http://127.0.0.1:{Port}/mcp" : "";

    public event Action? Changed;

    public IReadOnlyList<McpCallRecord> RecentCalls
    {
        get
        {
            lock (_gate) return _recentCalls.ToArray();
        }
    }

    /// <summary>切换档位；关闭即停止服务，开启失败时回退关闭（设计 08§4）。</summary>
    public bool SetMode(McpMode mode, out string error)
    {
        error = "";
        if (mode == McpMode.Off)
        {
            Stop();
            Policy.Mode = McpMode.Off;
            RaiseChanged();
            return true;
        }

        var previous = Policy.Mode;
        Policy.Mode = mode;
        if (!Running && !Start(out error))
        {
            Policy.Mode = previous == McpMode.Off ? McpMode.Off : previous;
            return false;
        }

        RaiseChanged();
        return true;
    }

    public bool Start(out string error)
    {
        error = "";
        if (_app != null) return true;
        if (Policy.Mode == McpMode.Off)
        {
            error = "请先选择只读或写入档";
            return false;
        }

        LastError = "";
        Port = DefaultPort;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory,
            });

            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(opt =>
            {
                if (AllowLan)
                {
                    opt.ListenAnyIP((ushort)Port);
                }
                else
                {
                    opt.ListenLocalhost((ushort)Port);
                }
            });

            builder.Services.AddSingleton(_config);
            builder.Services.AddSingleton(_files);
            builder.Services.AddSingleton(_kbs);
            builder.Services.AddSingleton(_prompts);
            builder.Services.AddSingleton(Policy);
            builder.Services.AddSingleton(Context);
            builder.Services.AddSingleton<McpKnowledgeService>();
            builder.Services.AddMcpServer()
                .WithHttpTransport()
                .WithTools<McpKnowledgeTools>()
                .WithPrompts<McpKnowledgePrompts>()
                .WithResources<McpKnowledgeResources>()
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
                {
                    var result = await next(context, ct);
                    RecordCall(context.Params?.Name ?? "", result.IsError == true);
                    return result;
                }));

            var app = builder.Build();
            app.Use(async (ctx, next) =>
            {
                if (!ctx.Request.Path.StartsWithSegments("/mcp"))
                {
                    await next();
                    return;
                }

                var origin = ctx.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(origin))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (!IsAuthorized(ctx.Request.Headers.Authorization.ToString()))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }

                await next();
            });
            app.MapMcp("/mcp");
            app.StartAsync().GetAwaiter().GetResult();
            _app = app;
            LastError = "";
            RaiseChanged();
            return true;
        }
        catch (Exception e)
        {
            _app = null;
            LastError = Summarize(e);
            error = LastError;
            return false;
        }
    }

    public void Stop()
    {
        if (_app == null) return;
        try { _app.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch { /* Dispose 兜底 */ }
        try { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { }
        _app = null;
        LastError = "";
        RaiseChanged();
    }

    /// <summary>局域网开关变化后需要重建监听（设计 08§7.1）。</summary>
    public bool SetAllowLan(bool allow, out string error)
    {
        error = "";
        if (AllowLan == allow) return true;
        var wasRunning = Running;
        if (wasRunning) Stop();
        AllowLan = allow;
        if (wasRunning && !Start(out error))
        {
            AllowLan = false;
            return false;
        }
        RaiseChanged();
        return true;
    }

    /// <summary>重新生成令牌：旧令牌立即失效（设计 08§7.1）。</summary>
    public string RegenerateToken()
    {
        _token = NewToken();
        SaveToken(_token);
        RaiseChanged();
        return _token;
    }

    public string[] LanEndpoints()
    {
        if (!Running || !AllowLan) return [];
        var list = new List<string>();
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList.Where(ip =>
                         ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)))
            {
                list.Add($"http://{ip}:{Port}/mcp");
            }
        }
        catch { }
        return list.Distinct().ToArray();
    }

    public string TokenMasked()
    {
        var t = _token;
        if (t.Length <= 8) return "••••••••";
        return $"{t[..4]}••••••••{t[^4..]}";
    }

    /// <summary>客户端配置片段（HTTP 与 stdio 两种，设计 08§9）。</summary>
    public string ClientConfigJson()
    {
        var endpoint = Running ? LocalEndpoint : $"http://127.0.0.1:{DefaultPort}/mcp";
        var http = $$"""
            {
              "mcpServers": {
                "llm-wiki": {
                  "type": "http",
                  "url": "{{endpoint}}",
                  "headers": { "Authorization": "Bearer {{_token}}" }
                }
              }
            }
            """;
        var exe = Path.Combine(AppContext.BaseDirectory, "LlmWiki.McpStdio.exe");
        var stdio = $$"""
            {
              "mcpServers": {
                "llm-wiki-stdio": {
                  "command": "{{exe.Replace("\\", "\\\\")}}",
                  "args": ["--kb", "{{Context.CurrentKb}}"]
                }
              }
            }
            """;
        return http + "\n\n// stdio（只读）\n" + stdio;
    }

    private void RecordCall(string tool, bool isError)
    {
        lock (_gate)
        {
            _recentCalls.Insert(0, new McpCallRecord(DateTime.Now, string.IsNullOrEmpty(tool) ? "unknown" : tool, isError));
            if (_recentCalls.Count > 20) _recentCalls.RemoveAt(_recentCalls.Count - 1);
        }
        RaiseChanged();
    }

    private bool IsAuthorized(string header)
    {
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var provided = header[prefix.Length..].Trim();
        if (provided.Length == 0) return false;
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(_token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private bool IsAllowedOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Port != Port) return false;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki", "mcp.token");

    private static string LoadOrCreateToken()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var protectedBytes = File.ReadAllBytes(TokenPath);
                var raw = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                var token = Encoding.UTF8.GetString(raw);
                if (token.Length >= 32) return token;
            }
        }
        catch { /* 令牌损坏则重新生成，不落明文 */ }

        var created = NewToken();
        SaveToken(created);
        return created;
    }

    private static void SaveToken(string token)
    {
        try
        {
            var dir = Path.GetDirectoryName(TokenPath)!;
            Directory.CreateDirectory(dir);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath, bytes);
        }
        catch { /* DPAPI 不可用时仅保留在内存，不写普通配置明文 */ }
    }

    private static string Summarize(Exception e)
    {
        var inner = e;
        while (inner.InnerException != null) inner = inner.InnerException;
        var msg = inner.Message.Trim();
        if (msg.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("被占用", StringComparison.OrdinalIgnoreCase))
        {
            return $"端口 {DefaultPort} 已被占用，请关闭占用程序后重试";
        }
        return msg.Length > 240 ? msg[..240] : msg;
    }
}
