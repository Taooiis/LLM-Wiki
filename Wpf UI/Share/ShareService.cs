using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using LLM_Wiki.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
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
    private FileBridge Files => new(config.WikisDir);
    private KbService Kbs => new(config.WikisDir);
    private IHost? _host;
    private IApplicationBuilder? _app;
    private int _port;
    private string[] _addresses = []; // 实际绑定的地址（port=0 时从服务器特性读取）
    private MdnsResponder? _mdns;     // mDNS 应答器（llmwiki.local 免手输 IP）

    public bool Running => _host != null;
    public int Port => _port;

    /// <summary>
    /// 本次分享的访问 PIN（4 位数字，每次开启重新生成）。
    /// 只读页与所有 /api 都必须带它（?pin= 或 cookie），防止同一局域网陌生人直接翻库。
    /// </summary>
    public string Pin { get; private set; } = "";

    private const string PinCookie = "llmwiki_pin";

    /// <summary>
    /// 启动服务。port=0 时系统分配一个空闲端口。
    /// </summary>
    public bool Start(int port, out string error)
    {
        if (_host != null) { error = "服务已在运行"; return false; }
        _port = port;
        // 每次开启换新 PIN：关掉再开，旧链接立即失效
        Pin = Random.Shared.Next(1000, 10000).ToString();
        try
        {
            _host = Host.CreateDefaultBuilder()
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

            // mDNS 应答器：让手机用 llmwiki.local:端口 访问分享页，免手输 IP。
            // 5353 被占（机器上另有 Bonjour 等）时静默跳过，不影响 IP/二维码路径。
            if (_mdns?.Active != true) _mdns = new MdnsResponder();

            // 从绑定的服务器地址读取真实端口（port=0 时）
            _addresses = _app?.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses?.ToArray()
                ?? [$"http://localhost:{_port}/"];
            ParseFirstAddress(_addresses.FirstOrDefault());
            error = "";
            return true;
        }
        catch (Exception e)
        {
            _host = null;
            Pin = "";
            error = e.Message;
            return false;
        }
    }

    public void Stop()
    {
        if (_host != null)
        {
            try { _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
            catch { /* 尽力而为：Dispose 兜底 */ }
            _host.Dispose();
            _host = null;
            Pin = "";
        }
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

    /// <summary>局域网可用地址（本机回环 + 所有 IPv4 局域网地址）。</summary>
    public string[] LanAddresses()
    {
        var addrs = new List<string> { LocalAddress() };
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

            // 列库（不带 root，不泄露绝对路径）
            endpoints.MapGet("/api/kbs", ctx =>
            {
                var node = new JsonObject();
                var arr = new JsonArray();
                foreach (var k in Kbs.ListKbs()) arr.Add(k);
                node["kbs"] = arr;
                return WriteJson(ctx, node);
            });

            // 目录树
            endpoints.MapGet("/api/tree", ctx =>
            {
                var kb = ctx.Request.Query["kb"];
                if (string.IsNullOrWhiteSpace(kb))
                    return Forbidden404(ctx);
                if (!Directory.Exists(Path.Combine(Wikis, kb!)))
                    return Forbidden404(ctx);
                var tree = Files.Tree(kb!);
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

    /// <summary>带 PIN 的访问地址（二维码与分享链接用）。</summary>
    public string AccessUrl(string address) =>
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
        <title>输入 PIN · LLM Wiki 只读页</title>
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
            <h1>输入 PIN</h1>
            <p>PIN 显示在电脑端「分享」页面，4 位数字</p>
            <input name="pin" inputmode="numeric" pattern="[0-9]{4}" maxlength="4" autocomplete="off" autofocus required aria-label="4 位 PIN">
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

    /// <summary>白名单文件响应：SafeJoin 校验 → 只下发可文本文件。越权返回 400。</summary>
    private Task SendFile(HttpContext ctx, string? kb, string? path, bool inline)
    {
        if (string.IsNullOrWhiteSpace(kb) || string.IsNullOrWhiteSpace(path))
            return Forbidden400(ctx);

        // 路径校验（同 PathGuard，拒绝 .. 越界）
        var safe = PathGuard.SafeJoin(Wikis, kb, path);
        if (safe == null)
            return Forbidden400(ctx);

        // 后缀白名单：只下发 md/txt 原件与 office 转换副本
        if (!FileHelper.IsTextFile(Path.GetFileName(safe)))
            return Forbidden400(ctx);

        if (!File.Exists(safe))
        {
            ctx.Response.StatusCode = 404;
            return Task.CompletedTask;
        }

        var content = File.ReadAllText(safe);
        ctx.Response.ContentType = GuessType(safe);
        if (!inline)
            ctx.Response.Headers["Content-Disposition"] =
                new ContentDispositionHeaderValue("attachment") { FileName = Path.GetFileName(safe) }.ToString();
        return ctx.Response.WriteAsync(content);
    }

    private static string GuessType(string path) =>
        Path.GetExtension(path)?.ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };

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
