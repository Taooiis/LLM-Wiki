using System.IO;
using System.Text.Json.Nodes;
using LlmWiki.App.Bridge;
using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>桥命令分发：参数校验 / 成功路径 / Demo API 形状（server.js 路由语义）。</summary>
public class BridgeDispatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmwiki-br-" + Guid.NewGuid().ToString("N"));
    private readonly BridgeDispatcher _bridge;
    private readonly string _wikis;

    public BridgeDispatcherTests()
    {
        _wikis = Path.Combine(_root, "wikis");
        Directory.CreateDirectory(_wikis);
        var cfg = new ConfigService(_root);
        cfg.SetWikisDir(_wikis, out _);
        _bridge = new BridgeDispatcher(cfg, new KbService(_wikis), new FileBridge(_wikis), new UploadService(_wikis));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private JsonObject Args(params (string k, object? v)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = v switch
        {
            null => null,
            string s => s,
            _ => v.ToString(),
        };
        return o;
    }

    [Fact]
    public async Task Kbs_Create_RejectsInvalidName_WithDemoErrorShape()
    {
        var json = JsonNode.Parse(await _bridge.HandleAsync("kbs-create", Args(("name", "带空格 库")).ToString()))!;
        Assert.Contains("知识库名", json["error"]!.ToString());
    }

    [Fact]
    public async Task Kbs_Create_List_RoundTrip()
    {
        var created = JsonNode.Parse(await _bridge.HandleAsync("kbs-create", Args(("name", "桥库"), ("scene", "研究")).ToString()))!;
        Assert.Equal(true, created["ok"]!.GetValue<bool>());

        var list = JsonNode.Parse(await _bridge.HandleAsync("kbs-list", new JsonObject().ToString()))!;
        Assert.Contains("桥库", list["kbs"]!.AsArray().Select(n => n!.ToString()));
    }

    [Fact]
    public async Task Kbs_Delete_RequiresValidKb()
    {
        await _bridge.HandleAsync("kbs-create", Args(("name", "删库")).ToString());
        var r = JsonNode.Parse(await _bridge.HandleAsync("kbs-delete", Args(("name", "删库")).ToString()))!;
        Assert.Equal(true, r["ok"]!.GetValue<bool>());
        Assert.True(Directory.Exists(_wikis) is false || !Directory.EnumerateDirectories(_wikis).Any());
    }

    [Fact]
    public async Task FileRead_RejectsTraversal()
    {
        var json = JsonNode.Parse(await _bridge.HandleAsync("file-read",
            new JsonObject { ["kb"] = "x", ["path"] = "..\\evil.md" }.ToString()))!;
        Assert.Contains("md/txt", json["error"]!.ToString());
    }

    [Fact]
    public async Task Settings_Set_SingleBackslashFails()
    {
        var json = JsonNode.Parse(await _bridge.HandleAsync("settings-set", Args(("wikisDir", "\\foo")).ToString()))!;
        Assert.Contains("绝对路径", json["error"]!.ToString());
    }

    [Fact]
    public async Task Upload_Base64Md_LandsInSources()
    {
        await _bridge.HandleAsync("kbs-create", Args(("name", "上传库")).ToString());
        var b64 = Convert.ToBase64String("# 内容"u8.ToArray());
        var json = JsonNode.Parse(await _bridge.HandleAsync("upload",
            new JsonObject { ["kb"] = "上传库", ["filename"] = "桥上传.md", ["b64"] = b64 }.ToString()))!;
        Assert.Equal(true, json["ok"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(_wikis, "上传库", "raw", "sources", "桥上传.md")));
    }

    [Fact]
    public async Task NetPcAndNetShares_ReturnEmptyArraysOnError()
    {
        var pc = JsonNode.Parse(await _bridge.HandleAsync("net-pc", new JsonObject().ToString()))!;
        Assert.True(pc["pcs"]!.AsArray().Count >= 0); // 形状核心：有 pcs 数组（net view 失败也空数组不 5xx）
        var sh = JsonNode.Parse(await _bridge.HandleAsync("net-shares", Args(("pc", "不存在的主机xyz")).ToString()))!;
        Assert.NotNull(sh["shares"]);
    }
}
