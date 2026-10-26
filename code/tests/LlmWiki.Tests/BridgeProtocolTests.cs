using System.Text.Json.Nodes;
using LlmWiki.App.Bridge;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>
/// JS↔C# 桥协议契约：字段名逐字对齐 bridge.js 的 postMessage 载荷，
/// 响应里的 result 必须是 JSON 对象（不是字符串），否则前端拿不到数据。
/// </summary>
public class BridgeProtocolTests
{
    [Fact]
    public void ParseRequest_LowercaseFields_FromJsPayload()
    {
        // bridge.js 实际发送的形状：{cmd, args, requestId}
        var req = BridgeProtocol.ParseRequest("""{"cmd":"kbs-list","args":"{}","requestId":"abc123"}""");
        Assert.NotNull(req);
        Assert.Equal("kbs-list", req!.Cmd);
        Assert.Equal("{}", req.Args);
        Assert.Equal("abc123", req.RequestId);
    }

    [Fact]
    public void ParseRequest_InvalidJson_ReturnsNull()
    {
        Assert.Null(BridgeProtocol.ParseRequest("not json"));
        Assert.Null(BridgeProtocol.ParseRequest(""));
        Assert.Null(BridgeProtocol.ParseRequest(null));
    }

    [Fact]
    public void CreateResponse_EmbedsResultAsObject_NotString()
    {
        var json = JsonNode.Parse(BridgeProtocol.CreateResponse("r1", """{"wikisDir":"D:\\kb","kbs":["A"]}"""))!;
        Assert.Equal("r1", json["requestId"]!.ToString());
        // 关键：result 是对象，前端 r.json().wikisDir 才能直接取值
        Assert.IsType<JsonObject>(json["result"]);
        Assert.Equal("D:\\kb", json["result"]!["wikisDir"]!.ToString());
    }

    [Fact]
    public void CreateResponse_InvalidResult_FallsBackToEmptyObject()
    {
        var json = JsonNode.Parse(BridgeProtocol.CreateResponse("r2", "<<<not json>>>"))!;
        Assert.IsType<JsonObject>(json["result"]);
    }

    [Fact]
    public void BridgeJs_UsesObjectResult_AndHasDeleteRoute()
    {
        // bridge.js 必须按对象取 result（否则前端全是 undefined），且补齐 /api/delete 路由
        var file = Path.Combine(AppContext.BaseDirectory, "wwwroot", "bridge.js");
        Assert.True(File.Exists(file), "bridge.js 未随构建复制到输出目录：" + file);
        var js = File.ReadAllText(file);

        Assert.Contains("wrapper.result", js);
        Assert.DoesNotContain("JSON.parse(event.data).result", js); // 不能把整个包装对象当结果
        Assert.Contains("'/api/delete': 'delete'", js);
    }
}
