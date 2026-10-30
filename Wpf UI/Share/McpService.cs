using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

using LLM_Wiki.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private readonly PromptComposer _composer;
    private readonly ReviewService _reviews;
    private readonly SourceService _sources;
    private readonly object _gate = new();
    private readonly List<McpCallRecord> _recentCalls = new();
    private WebApplication? _app;
    private string _token = "";
    private string _writeToken = "";

    public McpService(
        ConfigService config,
        FileBridge files,
        KbService kbs,
        PromptComposer composer,
        ReviewService reviews,
        SourceService sources,
        McpAccessPolicy policy,
        McpServerContext context)
    {
        _config = config;
        _files = files;
        _kbs = kbs;
        _composer = composer;
        _reviews = reviews;
        _sources = sources;
        Policy = policy;
        Context = context;
        _token = LoadOrCreateToken();
        _writeToken = LoadOrCreateWriteToken();
    }

    public McpAccessPolicy Policy { get; }

    public McpServerContext Context { get; }

    public bool Running => _app != null;

    /// <summary>本次 MCP 会话绑定的库；不随桌面当前库切换。</summary>
    public string BoundKb { get; private set; } = "";

    public int Port { get; private set; } = DefaultPort;

    /// <summary>是否监听局域网。默认仅 127.0.0.1（设计 08§7.1）。</summary>
    public bool AllowLan { get; private set; }

    public string Token => _token;

    /// <summary>写权限独立令牌（设计 04§4.2）；只在写入档随请求头发送。</summary>
    public string WriteToken => _writeToken;

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

    /// <summary>切换档位；关闭即停止服务，开启失败时回退关闭（设计 08§4）。失败原因记入 <see cref="LastError"/>。</summary>
    public bool SetMode(string kb, McpMode mode, out string error)
    {
        var ok = SetModeAsync(kb, mode).GetAwaiter().GetResult();
        error = ok ? "" : LastError;
        return ok;
    }

    /// <summary><see cref="SetMode"/> 的异步版本：供 UI await，不在 UI 线程阻塞等待 Kestrel 启停。</summary>
    public async Task<bool> SetModeAsync(string kb, McpMode mode)
    {
        if (mode == McpMode.Off)
        {
            await StopAsync().ConfigureAwait(false);
            Policy.Mode = McpMode.Off;
            RaiseChanged();
            return true;
        }

        if (Running && !BoundKb.Equals(kb, StringComparison.OrdinalIgnoreCase)) await StopAsync().ConfigureAwait(false);
        var previous = Policy.Mode;
        Policy.Mode = mode;
        if (!Running && !await StartAsync(kb).ConfigureAwait(false))
        {
            Policy.Mode = previous == McpMode.Off ? McpMode.Off : previous;
            return false;
        }

        Policy.Mode = mode;
        RaiseChanged();
        return true;
    }

    public bool Start(string kb, out string error)
    {
        var ok = StartAsync(kb).GetAwaiter().GetResult();
        error = ok ? "" : LastError;
        return ok;
    }

    /// <summary>
    /// 启动服务的异步核心（设计 04§11.2）：Kestrel StartAsync 直接 await，
    /// 由 UI 层以区域 Busy 呈现等待，不再 GetAwaiter().GetResult() 阻塞界面。
    /// 失败时 LastError 保留真实原因直到下一次成功启动或停止。
    /// </summary>
    public async Task<bool> StartAsync(string kb)
    {
        if (_app != null) return true;
        if (Policy.Mode == McpMode.Off)
        {
            LastError = "请先选择只读或写入档";
            return false;
        }
        if (string.IsNullOrWhiteSpace(kb) || !Directory.Exists(Path.Combine(_config.WikisDir, kb)))
        {
            LastError = "请选择一个存在的知识库再开启 MCP 连接";
            return false;
        }
        BoundKb = kb;
        Policy.AuthorizedKb = kb;
        Context.CurrentKb = kb;

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
            builder.Services.RemoveAll<IHostLifetime>();
            builder.Services.AddSingleton<IHostLifetime, NoopHostLifetime>();
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
            builder.Services.AddSingleton(_composer);
            builder.Services.AddSingleton(_reviews);
            builder.Services.AddSingleton(_sources);
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
                if (Policy.WriteEnabled && !IsWriteAuthorized(ctx.Request.Headers["X-LLM-Wiki-Write-Token"].ToString()))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }

                await next();
            });
            app.MapMcp("/mcp");
            await app.StartAsync().ConfigureAwait(false);
            _app = app;
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                if (!ReferenceEquals(_app, app)) return;
                _app = null;
                BoundKb = "";
                Policy.AuthorizedKb = "";
                RaiseChanged();
            });
            LastError = "";
            RaiseChanged();
            return true;
        }
        catch (Exception e)
        {
            _app = null;
            BoundKb = "";
            Policy.AuthorizedKb = "";
            LastError = Summarize(e);
            return false;
        }
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    /// <summary><see cref="Stop"/> 的异步版本：等待后台清理完成，不阻塞调用方 UI 线程。</summary>
    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        BoundKb = "";
        Policy.AuthorizedKb = "";
        LastError = "";
        if (app != null)
        {
            var cleanup = Task.Run(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    app.StopAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch { }
                try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            });
            try { await cleanup.ConfigureAwait(false); } catch { }
        }
        RaiseChanged();
    }

    /// <summary>局域网开关变化后需要重建监听（设计 08§7.1）。失败回滚到原监听档位（设计 04§11.1 D16）。</summary>
    public bool SetAllowLan(bool allow, out string error)
    {
        var ok = SetAllowLanAsync(allow).GetAwaiter().GetResult();
        error = ok ? "" : LastError;
        return ok;
    }

    /// <summary><see cref="SetAllowLan"/> 的异步版本：重启监听不阻塞 UI；失败时 AllowLan 回滚为原值。</summary>
    public async Task<bool> SetAllowLanAsync(bool allow)
    {
        if (AllowLan == allow) return true;
        var wasRunning = Running;
        var kb = BoundKb;
        var previous = AllowLan;
        if (wasRunning) await StopAsync().ConfigureAwait(false);
        AllowLan = allow;
        if (wasRunning && !await StartAsync(string.IsNullOrWhiteSpace(kb) ? Context.CurrentKb : kb).ConfigureAwait(false))
        {
            // D16：切 LAN 失败回原监听档位；服务此时是停止态，LastError 已带真实原因，由 UI 显示。
            AllowLan = previous;
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

    public string RegenerateWriteToken()
    {
        _writeToken = NewToken();
        SaveWriteToken(_writeToken);
        RaiseChanged();
        return _writeToken;
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

    /// <summary>客户端配置：一个有效的 JSON，包含 remote HTTP 与 stdio 两个条目。写入档额外带独立写令牌头。</summary>
    public string ClientConfigJson()
    {
        var endpoint = Running ? LocalEndpoint : $"http://127.0.0.1:{DefaultPort}/mcp";
        var headers = new JsonObject
        {
            ["Authorization"] = $"Bearer {_token}",
        };
        if (Policy.WriteEnabled)
        {
            headers["X-LLM-Wiki-Write-Token"] = _writeToken;
        }

        var http = new JsonObject
        {
            ["type"] = "remote",
            ["url"] = endpoint,
            ["oauth"] = false,
            ["headers"] = headers,
        };

        var exe = Path.Combine(AppContext.BaseDirectory, "LlmWiki.McpStdio.exe");
        var stdio = new JsonObject
        {
            ["command"] = exe,
            ["args"] = new JsonArray
            {
                "--kb",
                string.IsNullOrWhiteSpace(BoundKb) ? Context.CurrentKb : BoundKb,
            },
        };

        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["llm-wiki"] = http,
                ["llm-wiki-stdio"] = stdio,
            },
        };
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
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

    private bool IsWriteAuthorized(string provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var a = Encoding.UTF8.GetBytes(provided.Trim());
        var b = Encoding.UTF8.GetBytes(_writeToken);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
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

    private static string WriteTokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LLM_Wiki", "mcp.write.token");

    private static string LoadOrCreateWriteToken()
    {
        try
        {
            if (File.Exists(WriteTokenPath))
            {
                var protectedBytes = File.ReadAllBytes(WriteTokenPath);
                var raw = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                var token = Encoding.UTF8.GetString(raw);
                if (token.Length >= 32) return token;
            }
        }
        catch { }

        var created = NewToken();
        SaveWriteToken(created);
        return created;
    }

    private static void SaveWriteToken(string token)
    {
        try
        {
            var dir = Path.GetDirectoryName(WriteTokenPath)!;
            Directory.CreateDirectory(dir);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(WriteTokenPath, bytes);
        }
        catch { }
    }

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
