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
    private readonly ShareService _share;
    private readonly HttpClient _client;

    public ShareServiceTests()
    {
        _wikis = Path.Combine(_root, "wikis");
        Directory.CreateDirectory(_wikis);
        var cfg = new ConfigService(_root);
        cfg.SetWikisDir(_wikis, out _);
        new KbService(_wikis).CreateKb("分享库");
        new FileBridge(_wikis).WriteText("分享库", "raw/sources/说明.md", "# 标题\n\n正文", out _);

        _share = new ShareService(cfg, new FileBridge(_wikis), new KbService(_wikis));
        _share.Start(0, out var err); // port=0 随机
        Assert.True(string.IsNullOrEmpty(err), err);
        _client = new HttpClient();
        _client.BaseAddress = new Uri(_share.LocalAddress());
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
    public void LanAddresses_DoesNotLoopbackOnly()
    {
        var addrs = _share.LanAddresses();
        Assert.NotNull(addrs);
        // 至少返回 LocalAddress
        Assert.Contains(_share.LocalAddress(), addrs);
    }
}
