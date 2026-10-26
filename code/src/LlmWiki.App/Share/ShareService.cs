using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmWiki.App.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LlmWiki.App.Share;

/// <summary>
/// 只读分享服务（spec 03 §3）：Kestrel 静态 HTTP 服务，局域网浏览只读。
/// 路由白名单仅 GET；写方法一律 405、越权路径 400、未知路由 404；
/// 根本不挂写 API，防访探测（spec 03 §4）。
/// </summary>
public class ShareService(ConfigService config, FileBridge files, KbService kbs)
{
    private readonly string _wikis = config.WikisDir;
    private IHost? _host;
    private IApplicationBuilder? _app;
    private int _port;
    private string[] _addresses = []; // 实际绑定的地址（port=0 时从服务器特性读取）

    public bool Running => _host != null;
    public int Port => _port;

    /// <summary>
    /// 启动服务。port=0 时系统分配一个空闲端口。
    /// </summary>
    public bool Start(int port, out string error)
    {
        if (_host != null) { error = "服务已在运行"; return false; }
        _port = port;
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
            error = e.Message;
            return false;
        }
    }

    public void Stop()
    {
        if (_host != null)
        {
            _host.Dispose();
            _host = null;
        }
    }

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
                foreach (var k in kbs.ListKbs()) arr.Add(k);
                node["kbs"] = arr;
                return WriteJson(ctx, node);
            });

            // 目录树
            endpoints.MapGet("/api/tree", ctx =>
            {
                var kb = ctx.Request.Query["kb"];
                if (string.IsNullOrWhiteSpace(kb) || !Directory.Exists(Path.Combine(_wikis, kb)))
                    return Forbidden404(ctx);
                var tree = files.Tree(kb!);
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
        var m = System.Text.RegularExpressions.Regex.Match(addr, @"https?://[^:]+:(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p)) _port = p;
    }

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
        var safe = PathGuard.SafeJoin(_wikis, kb, path);
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
