using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

using LLM_Wiki.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LLM_Wiki.Share;

/// <summary>
/// 只读分享服务（spec 03 §3）：Kestrel 静态 HTTP 服务，局域网浏览只读。
/// 路由白名单仅 GET；写方法一律 405、越权路径 400、未知路由 404；
/// 根本不挂写 API，防访探测（spec 03 §4）。
/// </summary>
public class ShareService(ConfigService config)
{
    // 存储路径与服务实例每次请求现取：改路径后分享立刻跟随新目录（不再固化构造时的旧值）
    private string Wikis => config.WikisDir;
    private KbService Kbs => new(config, new ReviewService(config));
    private IHost? _host;
    private IApplicationBuilder? _app;
    private int _port;
    private string[] _addresses = []; // 实际绑定的地址（port=0 时从服务器特性读取）
    private MdnsResponder? _mdns;     // mDNS 应答器（llmwiki.local 免手输 IP）

    public bool Running => _host != null;
    public int Port => _port;
    /// <summary>本次分享绑定的知识库；切换当前库不会改变它。</summary>
    public string BoundKb { get; private set; } = "";

    /// <summary>
    /// 最近一次启动失败的真实原因（设计 04§11.2）：启动成功或主动停止前保留，
    /// 不被刷新清空——服务卡必须能持久显示失败状态而不是闪一下就变“已关闭”。
    /// </summary>
    public string LastError { get; private set; } = "";

    /// <summary>开始/停止/换 PIN 后通知 UI；状态以服务字段为准。</summary>
    public event Action? Changed;

    /// <summary>
    /// 本次分享的访问 PIN（4 位数字，每次开启重新生成）。
    /// 只读页与所有 /api 都必须带它（?pin= 或 cookie），防止同一局域网陌生人直接翻库。
    /// </summary>
    public string Pin { get; private set; } = "";

    private const string PinCookie = "llmwiki_pin";

    /// <summary>
    /// 重新生成 PIN（服务运行中）：旧 PIN 立即失效，已连接的设备需要重新输入新 PIN。
    /// 未运行时返回 false——启动时本来就会生成新 PIN。
    /// </summary>
    public bool RegeneratePin(out string error)
    {
        if (_host == null)
        {
            error = "分享服务未运行，启动时会自动生成新 PIN";
            return false;
        }

        Pin = Random.Shared.Next(1000, 10000).ToString();
        error = "";
        try { Changed?.Invoke(); } catch { }
        return true;
    }

    /// <summary>
    /// 启动服务。port=0 时系统分配一个空闲端口。
    /// </summary>
    public bool Start(string kb, int port, out string error)
    {
        if (_host != null) { error = LastError = "服务已在运行"; return false; }
        if (!Kbs.KbExists(kb)) { error = LastError = "请选择一个存在的知识库再启动分享"; return false; }
        LastError = "";
        BoundKb = kb;
        _port = port;
        // 每次开启换新 PIN：关掉再开，旧链接立即失效
        Pin = Random.Shared.Next(1000, 10000).ToString();
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IHostLifetime>();
                    services.AddSingleton<IHostLifetime, NoopHostLifetime>();
                })
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseKestrel(opt =>
                    {
                        // ListenAnyIP 在 0.0.0.0 监听，覆盖本机 + 局域网（spec 03 §3）
                        // port=0 时系统分配空闲端口（仅 AnyIP 支持动态端口）
                        opt.ListenAnyIP((ushort)port);
                    });
                    web.Configure(ConfigureApp);
                })
                .Build();
            _host.Start();
            var hostRef = _host;
            hostRef.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            {
                if (!ReferenceEquals(_host, hostRef)) return;
                _host = null;
                Pin = "";
                BoundKb = "";
                try { Changed?.Invoke(); } catch { }
            });

            // mDNS 应答器：让手机用 llmwiki.local:端口 访问分享页，免手输 IP。
            // 5353 被占（机器上另有 Bonjour 等）时静默跳过，不影响 IP/二维码路径。
            if (_mdns?.Active != true) _mdns = new MdnsResponder();

            // 从绑定的服务器地址读取真实端口（port=0 时）
            _addresses = _app?.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses?.ToArray()
                ?? [$"http://localhost:{_port}/"];
            ParseFirstAddress(_addresses.FirstOrDefault());
            error = "";
            try { Changed?.Invoke(); } catch { }
            return true;
        }
        catch (Exception e)
        {
            _host = null;
            Pin = "";
            BoundKb = "";
            error = LastError = e.Message;
            return false;
        }
    }

    public void Stop()
    {
        var host = _host;
        _host = null;
        Pin = "";
        BoundKb = "";
        LastError = "";
        if (host != null)
        {
            // Kestrel 的 Dispose 可能等待活动连接；放到后台完成，UI 最多等端口释放，不冻结窗口。
            var cleanup = Task.Run(() =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    host.StopAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch { }
                try { host.Dispose(); } catch { }
            });
            try { cleanup.Wait(TimeSpan.FromSeconds(1.5)); } catch { }
        }
        try { Changed?.Invoke(); } catch { }
        // mDNS 应答器一起收掉：不在分享就不该应答域名查询
        _mdns?.Dispose();
        _mdns = null;
    }

    /// <summary>
    /// mDNS 友好地址：http://llmwiki.local:端口/（手机输入这个就行）。
    /// 未启用（5353 被占）时返回空串，前端自动退回显示 IP 地址。
    /// </summary>
    public string FriendlyAddress() =>
        _mdns?.Active == true ? $"http://{_mdns.HostName}.local:{_port}/" : "";

    /// <summary>http://localhost:{port}/（客户端用 localhost 连，不连 0.0.0.0）。</summary>
    public string LocalAddress()
    {
        var addr = _addresses.FirstOrDefault() ?? $"http://localhost:{_port}/";
        // 0.0.0.0/[::] 是监听地址不能做客户端目标，换 localhost
        try { var uri = new Uri(addr); return $"http://localhost:{uri.Port}/"; }
        catch { return addr; }
    }

    /// <summary>
    /// 局域网可用地址：只返回真实 IPv4 局域网地址（不含回环）。
    /// 本机回环地址由 <see cref="LocalAddress"/> 单独提供——混在局域网列表里会让用户
    /// 把 localhost 当成可以发给其他设备的地址。
    /// </summary>
    public string[] LanAddresses()
    {
        var addrs = new List<string>();
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)))
                addrs.Add($"http://{ip}:{_port}/");
        }
        catch { }
        return addrs.Distinct().ToArray();
    }

    /// <summary>路由注册（白名单）。ASP.NET Core 默认对未注册方法返回 405。</summary>
    private void ConfigureApp(IApplicationBuilder app)
    {
        _app = app;
        // ConfigureApp 在服务器监听后执行：读取动态端口（port=0 时）
        _addresses = app.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses?.ToArray()
            ?? [$"http://localhost:{_port}/"];
        ParseFirstAddress(_addresses.FirstOrDefault());

        // PIN 闸门：必须在 UseRouting 之前，未授权请求直接挡掉，不进任何路由
        app.Use(async (ctx, next) =>
        {
            // 带对了 ?pin=：下发 cookie 并跳回干净 URL（后续请求靠 cookie，不再依赖 URL 里的 PIN）
            var fromQuery = ctx.Request.Query["pin"].ToString();
            if (!string.IsNullOrEmpty(fromQuery) && fromQuery == Pin)
            {
                ctx.Response.Cookies.Append(PinCookie, Pin, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                });
                var clean = new QueryString(
                    string.Join("&", ctx.Request.Query
                        .Where(kv => kv.Key != "pin")
                        .SelectMany(kv => kv.Value.Select(v => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(v ?? "")}"))));
                ctx.Response.Redirect(ctx.Request.Path + clean);
                return;
            }

            if (IsAuthorized(ctx))
            {
                await next();
                return;
            }

            // /api/* 给 401 JSON，页面给 PIN 输入页
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync("{\"error\":\"PIN 不正确或已失效\",\"needPin\":true}");
                return;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(PinGateHtml());
        });

        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            // 静态资源（只读视图）
            endpoints.MapGet("/", async ctx =>
            {
                var html = await ReadShareUiAsync();
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(html);
            });

            // 只返回本次绑定的库，不暴露根目录下的其他库
            endpoints.MapGet("/api/kbs", ctx =>
            {
                var node = new JsonObject();
                var arr = new JsonArray();
                if (!string.IsNullOrEmpty(BoundKb)) arr.Add(BoundKb);
                node["kbs"] = arr;
                node["bound"] = BoundKb;
                return WriteJson(ctx, node);
            });

            // 目录树：只允许绑定库
            endpoints.MapGet("/api/tree", ctx =>
            {
                var kb = ctx.Request.Query["kb"].ToString();
                if (string.IsNullOrWhiteSpace(kb) || !kb.Equals(BoundKb, StringComparison.OrdinalIgnoreCase))
                    return Forbidden404(ctx);
                var tree = BuildBoundTree(Path.Combine(Wikis, BoundKb));
                var node = new JsonObject { ["tree"] = JsonSerializer.SerializeToNode(tree) };
                return WriteJson(ctx, node);
            });

            // 读文件（md/txt 本体 + office 转换副本 .docx.md/.xlsx.md）
            endpoints.MapGet("/api/file", ctx =>
            {
                var kb = ctx.Request.Query["kb"];
                var path = ctx.Request.Query["path"];
                return SendFile(ctx, kb, path, inline: true);
            });

            // 下载
            endpoints.MapGet("/download", ctx =>
            {
                var kb = ctx.Request.Query["kb"];
                var path = ctx.Request.Query["path"];
                return SendFile(ctx, kb, path, inline: false);
            });
        });

        // 未注册路由 → 404（含提示词生成类 / 所有写 API / 其它）
        app.Run(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
    }

    private void ParseFirstAddress(string? addr)
    {
        if (addr is null) return;
        var m = Regex.Match(addr, @"https?://[^:]+:(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p)) _port = p;
    }

    /// <summary>干净访问地址（不含 PIN）；二维码与复制地址默认用它，PIN 单独输入。</summary>
    public string AccessUrl(string address) => address;

    public string AccessUrlWithPin(string address) =>
        string.IsNullOrEmpty(Pin) ? address : address + (address.Contains('?') ? "&" : "?") + "pin=" + Pin;

    /// <summary>请求是否已带对 PIN（只认 cookie：URL 里的 ?pin= 在上面已换成 cookie 并重定向）。</summary>
    private bool IsAuthorized(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(Pin)) return false;
        var cookie = ctx.Request.Cookies[PinCookie];
        return !string.IsNullOrEmpty(cookie) && cookie == Pin;
    }

    /// <summary>PIN 输入页：极简表单，输对就带 ?pin= 跳回原地址。</summary>
    private string PinGateHtml() => $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>输入访问码 · LLM Wiki 只读页</title>
        <style>
          :root { color-scheme: light dark; }
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: system-ui, -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif; background: #f6f7f9; }
          @media (prefers-color-scheme: dark) { body { background: #14161a; color: #e8eaed; } }
          form { background: #fff; padding: 32px 28px; border-radius: 14px; box-shadow: 0 8px 32px rgba(0,0,0,.08); width: min(88vw, 320px); text-align: center; }
          @media (prefers-color-scheme: dark) { form { background: #1e2126; box-shadow: none; } }
          h1 { font-size: 17px; margin: 0 0 6px; }
          p { font-size: 13px; opacity: .65; margin: 0 0 18px; }
          input { width: 100%; box-sizing: border-box; font-size: 26px; letter-spacing: 10px; text-align: center; padding: 10px 12px; border: 1px solid #ccc; border-radius: 10px; background: transparent; color: inherit; }
          button { margin-top: 14px; width: 100%; padding: 11px; font-size: 15px; border: 0; border-radius: 10px; background: #2563eb; color: #fff; cursor: pointer; }
        </style>
        </head>
        <body>
          <form method="get" action="/">
            <h1>输入访问码</h1>
            <p>访问码显示在电脑端「连接与分享 → 浏览分享」，4 位数字</p>
            <input name="pin" inputmode="numeric" pattern="[0-9]{4}" maxlength="4" autocomplete="off" autofocus required aria-label="4 位访问码">
            <button type="submit">进入</button>
          </form>
        </body>
        </html>
        """;

    private Task Forbidden400(HttpContext ctx)
    {
        ctx.Response.StatusCode = 400;
        return Task.CompletedTask;
    }

    private Task Forbidden404(HttpContext ctx)
    {
        ctx.Response.StatusCode = 404;
        return Task.CompletedTask;
    }

    /// <summary>白名单文件响应：只允许绑定库，只允许资料/知识文件；非文本按原字节下载。</summary>
    private Task SendFile(HttpContext ctx, string? kb, string? path, bool inline)
    {
        if (string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(path)
            || !kb.Equals(BoundKb, StringComparison.OrdinalIgnoreCase))
            return Forbidden400(ctx);

        var safe = PathGuard.SafeJoin(Wikis, kb, path);
        if (safe == null) return Forbidden400(ctx);
        var name = Path.GetFileName(safe);
        var rel = path.Replace('\\', '/');
        if (!AllowedShareFile(rel, name)) return Forbidden400(ctx);
        if (!File.Exists(safe))
        {
            ctx.Response.StatusCode = 404;
            return Task.CompletedTask;
        }

        ctx.Response.ContentType = GuessType(safe);
        if (!inline || !SourceRules.IsText(name))
            ctx.Response.Headers["Content-Disposition"] =
                new ContentDispositionHeaderValue("attachment") { FileName = name }.ToString();
        return ctx.Response.SendFileAsync(safe);
    }

    private static string GuessType(string path) =>
        Path.GetExtension(path)?.ToLowerInvariant() switch
        {
            ".md" => "text/markdown; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream",
        };

    /// <summary>浏览树只含资料、知识和规则文件；不包含隐藏状态与操作日志。</summary>
    private static TreeItem[] BuildBoundTree(string root)
    {
        return BuildDir(root, root, 0);
    }

    private static TreeItem[] BuildDir(string dir, string root, int depth)
    {
        if (depth > 6) return [];
        List<(string Name, bool IsDir, string Full)> entries;
        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(dir)
                .Select(p => (Path.GetFileName(p), Directory.Exists(p), p))
                .Where(e => e.Item1.Length > 0 && !e.Item1.StartsWith('.'))
                .Select(e => (e.Item1, e.Item2, e.p))];
        }
        catch { return []; }

        var list = new List<TreeItem>();
        foreach (var (name, isDir, full) in entries
                     .OrderBy(e => e.IsDir ? 0 : 1)
                     .ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (isDir)
            {
                list.Add(new TreeItem(name, "dir", BuildDir(full, root, depth + 1), rel));
                continue;
            }
            if (!AllowedShareFile(rel, name)) continue;
            list.Add(new TreeItem(name, "file", null, rel));
        }
        return [.. list];
    }

    private static bool AllowedShareFile(string rel, string name)
        => (rel.StartsWith("raw/sources/", StringComparison.OrdinalIgnoreCase) && SourceRules.IsSupported(name))
           || (rel.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase) && SourceRules.IsText(name)
               && !name.Equals("log.md", StringComparison.OrdinalIgnoreCase))
           || rel.Equals("purpose.md", StringComparison.OrdinalIgnoreCase)
           || rel.Equals("schema.md", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadShareUiAsync()
    {
        var full = Path.Combine(AppContext.BaseDirectory, "wwwroot-share", "index.html");
        return File.Exists(full)
            ? await File.ReadAllTextAsync(full)
            : "<html><body><h1>只读视图 · LLM Wiki</h1></body></html>";
    }

    private static Task WriteJson(HttpContext ctx, JsonObject node)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(node.ToJsonString());
    }
}
