using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmWiki.App.Services;
using LlmWiki.App.Share;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>
/// 只读分享服务（spec 03 §4 验收清单）：
///   - 列库/树/看与下载：200
///   - 写操作（POST/PUT/DELETE）：405 方法不允许
///   - 越权路径（..）：400 拒绝
///   - 提示词/不存在路由：404
/// </summary>
public class ShareServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmwiki-share-" + Guid.NewGuid().ToString("N"));
    private readonly string _wikis;
    private readonly ConfigService _cfg;
    private readonly ShareService _share;
    private readonly HttpClient _client;

    public ShareServiceTests()
    {
        _wikis = Path.Combine(_root, "wikis");
        Directory.CreateDirectory(_wikis);
        _cfg = new ConfigService(_root);
        _cfg.SetWikisDir(_wikis, out _);
        new KbService(_wikis).CreateKb("分享库");
        new FileBridge(_wikis).WriteText("分享库", "raw/sources/说明.md", "# 标题\n\n正文", out _);

        _share = new ShareService(_cfg);
        _share.Start(0, out var err); // port=0 随机
        Assert.True(string.IsNullOrEmpty(err), err);
        _client = new HttpClient();
        _client.BaseAddress = new Uri(_share.LocalAddress());
        // 分享服务要求 4 位 PIN：测试统一用 cookie 认证（等价于扫码进过一次之后的状态）
        _client.DefaultRequestHeaders.Add("Cookie", "llmwiki_pin=" + _share.Pin);
    }

    public void Dispose()
    {
        _client.Dispose();
        _share.Stop();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Kbs_GET_ReturnsList()
    {
        var r = await _client.GetAsync("/api/kbs");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var data = await r.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("分享库", data!["kbs"]!.AsArray().Select(n => n!.ToString()));
        // 绝对路径不泄露给前端（白名单不带 root）
        Assert.Null(data!["root"]);
    }

    [Fact]
    public async Task Root_GET_ReturnsShareUi()
    {
        var r = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("只读视图", html);
        Assert.Contains("编辑请到主机", html);
    }

    [Fact]
    public async Task Tree_GET_ReturnsTree()
    {
        var r = await _client.GetAsync("/api/tree?kb=分享库");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var data = await r.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(data!["tree"]!.AsArray().Count > 0);
    }

    [Fact]
    public async Task File_GET_ReturnsContent()
    {
        var r = await _client.GetAsync("/api/file?kb=分享库&path=raw/sources/说明.md");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/markdown", r.Content.Headers.ContentType!.ToString());
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("# 标题", text);
    }

    [Fact]
    public async Task File_GET_Traversal_Returns400()
    {
        var r = await _client.GetAsync("/api/file?kb=分享库&path=..\\..\\evil.md");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task File_GET_OutsideKb_Returns400()
    {
        var r = await _client.GetAsync("/api/file?kb=分享库&path=raw/original/../../../evil.md");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task PromptRoute_Returns404()
    {
        var r = await _client.GetAsync("/api/prompt?kind=query&kb=分享库");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task WriteMethods_Return405()
    {
        var methods = new[] { "POST", "PUT", "DELETE" };
        foreach (var m in methods)
        {
            var req = new HttpRequestMessage(new HttpMethod(m), "/api/kbs");
            var r = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
        }
    }

    [Fact]
    public async Task Download_GET_ReturnsFileDisposition()
    {
        var r = await _client.GetAsync("/download?kb=分享库&path=raw/sources/说明.md");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cd = r.Content.Headers.ContentDisposition;
        Assert.NotNull(cd);
        Assert.Equal("说明.md", cd!.FileNameStar?.Trim('"') ?? cd.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Kbs_FollowLiveWikisDir()
    {
        // 回归：分享服务曾把存储路径固化在构造时，改路径后局域网看到的还是旧目录
        var other = Path.Combine(_root, "wikis2");
        Directory.CreateDirectory(other);
        new KbService(other).CreateKb("第二库");
        _cfg.SetWikisDir(other, out _);

        var r = await _client.GetAsync("/api/kbs");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var data = await r.Content.ReadFromJsonAsync<JsonObject>();
        var names = data!["kbs"]!.AsArray().Select(n => n!.ToString()).ToList();
        Assert.Contains("第二库", names);
        Assert.DoesNotContain("分享库", names);
    }

    [Fact]
    public async Task File_GET_ServesWikiPagesByRelativePath()
    {
        // 回归：分享页目录树可点 wiki/ 下页面（此前前端写死 raw/sources/）
        new FileBridge(_wikis).WriteText("分享库", "wiki/index.md", "# 目录页", out _);
        var r = await _client.GetAsync("/api/file?kb=分享库&path=wiki/index.md");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("目录页", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public void LanAddresses_DoesNotLoopbackOnly()
    {
        var addrs = _share.LanAddresses();
        Assert.NotNull(addrs);
        // 至少返回 LocalAddress
        Assert.Contains(_share.LocalAddress(), addrs);
    }

    // ---------- PIN 鉴权 ----------

    /// <summary>不带 PIN 的客户端（模拟同网段陌生人）。</summary>
    private HttpClient Anonymous()
    {
        var c = new HttpClient { BaseAddress = new Uri(_share.LocalAddress()) };
        return c;
    }

    [Fact]
    public async Task NoPin_ApiReturns401()
    {
        using var c = Anonymous();
        var r = await c.GetAsync("/api/kbs");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("needPin", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WrongPin_ApiReturns401()
    {
        using var c = Anonymous();
        var r = await c.GetAsync("/api/kbs?pin=0000");
        // 0000 几乎不可能等于随机 PIN；万一撞上就跳过（概率 1/9000）
        if (_share.Pin == "0000") return;
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task NoPin_RootServesPinGate()
    {
        using var c = Anonymous();
        var r = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("输入 PIN", html);
        // 没验 PIN 前不能拿到只读页内容
        Assert.DoesNotContain("编辑请到主机", html);
    }

    [Fact]
    public async Task QueryPin_SetsCookieAndRedirectsToCleanUrl()
    {
        // 关掉自动跳转：要断言的是「带 PIN 会 302 到干净 URL」
        using var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(_share.LocalAddress()),
        };
        var r = await c.GetAsync("/?pin=" + _share.Pin);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location!.ToString());
        var cookie = r.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("llmwiki_pin=" + _share.Pin, cookie);
    }

    [Fact]
    public async Task Pin_IsRegeneratedOnRestart()
    {
        var first = _share.Pin;
        Assert.Matches("^[0-9]{4}$", first);

        _share.Stop();
        Assert.Equal("", _share.Pin);

        Assert.True(_share.Start(0, out var err), err);
        Assert.Matches("^[0-9]{4}$", _share.Pin);
        Assert.NotEqual(first, _share.Pin);
    }

    [Fact]
    public void QrSvg_RendersSvgForUrl()
    {
        var svg = QrSvg.Render(_share.AccessUrl(_share.LocalAddress()));
        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.Contains("<path", svg);
        // 二维码内容里带 PIN，扫码即带授权
        Assert.Contains("pin=" + _share.Pin, _share.AccessUrl(_share.LocalAddress()));
    }
}
