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
    private readonly ConfigService _cfg;
    private readonly string _wikis;

    public BridgeDispatcherTests()
    {
        _wikis = Path.Combine(_root, "wikis");
        Directory.CreateDirectory(_wikis);
        _cfg = new ConfigService(_root);
        _cfg.SetWikisDir(_wikis, out _);
        _bridge = new BridgeDispatcher(_cfg);
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
        Assert.True(created["ok"]!.GetValue<bool>());

        var list = JsonNode.Parse(await _bridge.HandleAsync("kbs-list", new JsonObject().ToString()))!;
        Assert.Contains("桥库", list["kbs"]!.AsArray().Select(n => n!.ToString()));
    }

    [Fact]
    public async Task Kbs_Delete_RequiresValidKb()
    {
        await _bridge.HandleAsync("kbs-create", Args(("name", "删库")).ToString());
        var r = JsonNode.Parse(await _bridge.HandleAsync("kbs-delete", Args(("name", "删库")).ToString()))!;
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.True(!Directory.Exists(_wikis) || !Directory.EnumerateDirectories(_wikis).Any());
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
        Assert.True(json["ok"]!.GetValue<bool>());
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

    // ---------- 分享：PIN + 二维码 ----------

    [Fact]
    public async Task ShareStart_ReturnsPinQrAndAuthorizedUrls()
    {
        // port=0：系统分配空闲端口，测试不占用固定端口
        var json = JsonNode.Parse(await _bridge.HandleAsync("share-start", Args(("port", "0")).ToString()))!;
        try
        {
            Assert.True(json["running"]!.GetValue<bool>());
            var pin = json["pin"]!.ToString();
            Assert.Matches("^[0-9]{4}$", pin);

            // 面板直接内嵌 SVG，不需要额外请求图片
            Assert.Contains("<svg", json["qr"]!.ToString());

            // 地址都带 PIN：发给对方点开即进
            var urls = json["lanAddresses"]!.AsArray().Select(n => n!.ToString()).ToList();
            Assert.NotEmpty(urls);
            Assert.All(urls, u => Assert.Contains("pin=" + pin, u));
        }
        finally
        {
            await _bridge.HandleAsync("share-stop", "{}");
        }

        var stopped = JsonNode.Parse(await _bridge.HandleAsync("share-status", "{}"))!;
        Assert.False(stopped["running"]!.GetValue<bool>());
        Assert.Equal("", stopped["pin"]!.ToString()); // 关掉后 PIN 清空，旧链接失效
    }

    // ---------- 提示词命令（Task 7） ----------

    [Fact]
    public async Task Prompt_Query_ReturnsFilledPromptWithAbsoluteRoot()
    {
        await _bridge.HandleAsync("kbs-create", Args(("name", "提示库")).ToString());
        var json = JsonNode.Parse(await _bridge.HandleAsync("prompt",
            new JsonObject { ["kind"] = "query", ["kb"] = "提示库", ["question"] = "什么是知识库" }.ToString()))!;
        Assert.Null(json["error"]);
        var prompt = json["prompt"]!.ToString();
        Assert.Contains("什么是知识库", prompt);
        Assert.Contains(_wikis + "\\提示库", prompt);
    }

    [Fact]
    public async Task Prompt_UnknownKind_ReturnsError()
    {
        var json = JsonNode.Parse(await _bridge.HandleAsync("prompt",
            new JsonObject { ["kind"] = "nope", ["kb"] = "x" }.ToString()))!;
        Assert.NotNull(json["error"]);
    }

    [Fact]
    public async Task Prompt_Clean_ExpandsRealPath()
    {
        await _bridge.HandleAsync("kbs-create", Args(("name", "清理库")).ToString());
        var json = JsonNode.Parse(await _bridge.HandleAsync("prompt",
            new JsonObject { ["kind"] = "clean", ["kb"] = "清理库", ["file"] = "raw/sources/a.md" }.ToString()))!;
        var prompt = json["prompt"]!.ToString();
        Assert.Contains(_wikis + "\\清理库", prompt);
        Assert.DoesNotContain("{{abs_root}}", prompt);
    }

    // ---------- 回归：改存储路径后必须立即生效 ----------

    [Fact]
    public async Task Settings_Set_NewPath_TakesEffectImmediately()
    {
        await _bridge.HandleAsync("kbs-create", Args(("name", "旧库")).ToString());
        var before = JsonNode.Parse(await _bridge.HandleAsync("kbs-list", new JsonObject().ToString()))!;
        Assert.Contains("旧库", before["kbs"]!.AsArray().Select(n => n!.ToString()));

        // 切到新目录：库列表必须跟着变（服务不能绑定启动时的旧路径）
        var newDir = Path.Combine(_root, "wikis2");
        var set = JsonNode.Parse(await _bridge.HandleAsync("settings-set", Args(("wikisDir", newDir)).ToString()))!;
        Assert.True(set["ok"]!.GetValue<bool>());

        var after = JsonNode.Parse(await _bridge.HandleAsync("kbs-list", new JsonObject().ToString()))!;
        Assert.DoesNotContain("旧库", after["kbs"]!.AsArray().Select(n => n!.ToString()));
        Assert.Equal(newDir, after["root"]!.ToString());
    }

    // ---------- 原生文件夹选择器（桌面应用系统对话框） ----------

    [Fact]
    public async Task FolderPick_ReturnsChosenPath_CanceledAndNoHostFallback()
    {
        // 宿主注入后回传所选路径
        _bridge.FolderPicker = () => @"D:\知识库";
        var picked = JsonNode.Parse(await _bridge.HandleAsync("folder-pick", new JsonObject().ToString()))!;
        Assert.True(picked["ok"]!.GetValue<bool>());
        Assert.Equal(@"D:\知识库", picked["path"]!.ToString());

        // 取消：canceled=true
        _bridge.FolderPicker = () => null;
        var canceled = JsonNode.Parse(await _bridge.HandleAsync("folder-pick", new JsonObject().ToString()))!;
        Assert.False(canceled["ok"]!.GetValue<bool>());
        Assert.True(canceled["canceled"]!.GetValue<bool>());

        // 无宿主（测试/异常环境）：ok=false，前端据此回退内置选择器
        _bridge.FolderPicker = null;
        var noHost = JsonNode.Parse(await _bridge.HandleAsync("folder-pick", new JsonObject().ToString()))!;
        Assert.False(noHost["ok"]!.GetValue<bool>());
        Assert.Null(noHost["path"]);
    }
}
