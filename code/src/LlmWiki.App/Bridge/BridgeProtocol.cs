using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LlmWiki.App.Bridge;

/// <summary>JS→C# 请求载荷；字段名全小写，与 bridge.js 的 postMessage 载荷逐字对齐。</summary>
public sealed record BridgeRequest
{
    [JsonPropertyName("cmd")] public string Cmd { get; init; } = "";
    [JsonPropertyName("args")] public string Args { get; init; } = "";
    [JsonPropertyName("requestId")] public string RequestId { get; init; } = "";
}

/// <summary>
/// 桥协议（JS↔C# 契约的唯一权威定义）。
/// 约定：
///   请求：JS 发 {"cmd","args","requestId"}；cmd=命令名，args=JSON 参数字符串。
///   响应：C# 回 {"requestId","result"}；result 直接是 JSON 对象（不是字符串），
///         前端拿到即可 r.json() 后取字段，不再二次解析。
/// </summary>
public static class BridgeProtocol
{
    /// <summary>解析 JS 请求；空/非法 JSON 返回 null（宿主忽略该消息）。</summary>
    public static BridgeRequest? ParseRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<BridgeRequest>(json); }
        catch { return null; }
    }

    /// <summary>构造响应：把分发器返回的 JSON 字符串嵌成对象放进 result。</summary>
    public static string CreateResponse(string requestId, string resultJson)
    {
        JsonNode? result = null;
        try { result = JsonNode.Parse(resultJson); } catch { result = null; }
        return new JsonObject
        {
            ["requestId"] = requestId,
            ["result"] = result ?? new JsonObject(),
        }.ToJsonString();
    }
}
