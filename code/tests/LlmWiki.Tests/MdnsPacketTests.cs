using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using LlmWiki.App.Services;
using Xunit;

namespace LlmWiki.Tests;

/// <summary>
/// mDNS 应答器的纯协议层（RFC 6762 最小子集）：
///   - 只答自己域名的 A 记录，其它查询不理
///   - 应答含正确的 A 记录（类型/类/TTL/IP）
///   - 收到别人的同名 A 记录 → 能识别出冲突
/// </summary>
public class MdnsPacketTests
{
    private static readonly byte[] OurName = MdnsPacket.EncodeName("llmwiki.local");

    /// <summary>造一条 mDNS 查询报文：头部 + 问题（名字 + 类型 + 类）。</summary>
    private static byte[] BuildQuery(string name, ushort type, ushort @class = 1, ushort id = 0x1234)
    {
        var q = new List<byte>();
        q.AddRange([(byte)(id >> 8), (byte)(id & 0xFF)]);   // ID
        q.AddRange([0x00, 0x00]);                            // Flags: 标准查询
        q.AddRange([0x00, 0x01]);                            // QDCOUNT = 1
        q.AddRange([0x00, 0x00]);                            // ANCOUNT
        q.AddRange([0x00, 0x00]);                            // NSCOUNT
        q.AddRange([0x00, 0x00]);                            // ARCOUNT
        q.AddRange(MdnsPacket.EncodeName(name));
        q.AddRange([(byte)(type >> 8), (byte)(type & 0xFF)]);
        q.AddRange([(byte)(@class >> 8), (byte)(@class & 0xFF)]);
        return q.ToArray();
    }

    [Fact]
    public void EncodeName_ProducesLabelPrefixedWireFormat()
    {
        var wire = MdnsPacket.EncodeName("llmwiki.local");
        // 7 字节标签 "llmwiki" + 5 字节标签 "local" + 结束 0
        Assert.Equal(1 + 7 + 1 + 5 + 1, wire.Length);
        Assert.Equal(7, wire[0]);
        Assert.Equal((byte)'l', wire[1]);
        Assert.Equal(0, wire[^1]);
    }

    [Fact]
    public void BuildResponse_AnswersOurNameWithARecord()
    {
        var query = BuildQuery("llmwiki.local", 1);
        var ip = IPAddress.Parse("192.168.1.50");

        var resp = MdnsPacket.BuildResponse(query, OurName, ip, out var wantsUnicast);

        Assert.NotNull(resp);
        Assert.False(wantsUnicast);
        // 头部：QR=1 AA=1 → flags 0x8400；QDCOUNT=1，ANCOUNT=1
        Assert.Equal(0x8400, BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(2, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(4, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(6, 2)));
        // 记录名用压缩指针指回问题里的名字（记录起点 = 报文长度 - 16）
        Assert.Equal(0xC0, resp[^16]);
        Assert.Equal(0x0C, resp[^15]);
        // TYPE=A，CLASS=IN
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(resp.Length - 14, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(resp.Length - 12, 2)));
        // TTL = 120 秒
        Assert.Equal(120u, BinaryPrimitives.ReadUInt32BigEndian(resp.AsSpan(resp.Length - 10, 4)));
        // RDLENGTH = 4，末尾 4 字节是 IP
        Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(resp.Length - 6, 2)));
        Assert.Equal(ip.GetAddressBytes(), resp[^4..]);
    }

    [Fact]
    public void BuildResponse_IgnoresOtherNames()
    {
        // 别人的域名：不能抢答，否则会干扰局域网里其它 mDNS 服务
        var query = BuildQuery("someone-else.local", 1);
        Assert.Null(MdnsPacket.BuildResponse(query, OurName, IPAddress.Loopback, out _));
    }

    [Fact]
    public void BuildResponse_IgnoresNonATypeQueries()
    {
        // AAAA（IPv6）与 PTR 都不是我们要答的
        Assert.Null(MdnsPacket.BuildResponse(BuildQuery("llmwiki.local", 28), OurName, IPAddress.Loopback, out _));
        Assert.Null(MdnsPacket.BuildResponse(BuildQuery("llmwiki.local", 12), OurName, IPAddress.Loopback, out _));
    }

    [Fact]
    public void BuildResponse_HandlesAnyTypeQuery()
    {
        // ANY(255) 查询也要能答
        var resp = MdnsPacket.BuildResponse(BuildQuery("llmwiki.local", 255), OurName, IPAddress.Loopback, out _);
        Assert.NotNull(resp);
    }

    [Fact]
    public void BuildResponse_IgnoresResponses()
    {
        // QR=1 是别人的应答，不是查询 → 不理
        var query = BuildQuery("llmwiki.local", 1);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2, 2), 0x8400);
        Assert.Null(MdnsPacket.BuildResponse(query, OurName, IPAddress.Loopback, out _));
    }

    [Fact]
    public void BuildResponse_DetectsUnicastRequestBit()
    {
        // qclass 的 0x8000 位 = 要单播应答
        var query = BuildQuery("llmwiki.local", 1, @class: 0x8001);
        var resp = MdnsPacket.BuildResponse(query, OurName, IPAddress.Loopback, out var wantsUnicast);
        Assert.NotNull(resp);
        Assert.True(wantsUnicast);
    }

    [Fact]
    public void BuildResponse_IgnoresMalformedPackets()
    {
        Assert.Null(MdnsPacket.BuildResponse([], OurName, IPAddress.Loopback, out _));
        Assert.Null(MdnsPacket.BuildResponse(new byte[8], OurName, IPAddress.Loopback, out _));
        // 头部完整但问题被截断
        var truncated = BuildQuery("llmwiki.local", 1)[..20];
        Assert.Null(MdnsPacket.BuildResponse(truncated, OurName, IPAddress.Loopback, out _));
    }

    [Fact]
    public void FindARecordFor_DetectsNameConflict()
    {
        // 造一条别人的应答：llmwiki.local → 192.168.1.99
        var resp = MdnsPacket.BuildResponse(
            BuildQuery("llmwiki.local", 1), OurName, IPAddress.Parse("192.168.1.99"), out _)!;

        var found = MdnsPacket.FindARecordFor(resp, OurName);

        Assert.NotNull(found);
        Assert.Equal(IPAddress.Parse("192.168.1.99"), found);
    }

    [Fact]
    public void FindARecordFor_ReturnsNullWhenNoConflict()
    {
        // 别人的应答说的是另一个域名 → 不算冲突
        var other = MdnsPacket.EncodeName("other.local");
        var resp = MdnsPacket.BuildResponse(
            BuildQuery("other.local", 1), other, IPAddress.Parse("192.168.1.99"), out _)!;

        Assert.Null(MdnsPacket.FindARecordFor(resp, OurName));
    }

    [Fact]
    public void SkipName_HandlesLabelsAndCompressionPointers()
    {
        // 标签形式：1+7 字节 "llmwiki" + 1+5 字节 "local" + 结束 0 → 15 字节
        Assert.Equal(15, MdnsPacket.SkipName(OurName, 0));
        // 压缩指针占 2 字节
        Assert.Equal(2, MdnsPacket.SkipName([0xC0, 0x0C], 0));
    }

    [Fact]
    public void TryReadName_ExpandsCompressionPointer()
    {
        // 问题里放 "llmwiki.local"，应答里用压缩指针指回去
        var query = BuildQuery("llmwiki.local", 1);
        var resp = MdnsPacket.BuildResponse(query, OurName, IPAddress.Loopback, out _)!;
        var qEnd = 12 + MdnsPacket.SkipName(query, 12) + 4;

        Assert.True(MdnsPacket.TryReadName(resp, qEnd, out var name, out var consumed));
        Assert.Equal(2, consumed);                    // 压缩指针 2 字节
        Assert.Equal(OurName, name);                 // 展开后与原名一致
    }
}

/// <summary>
/// mDNS 应答器的真实网络路径：组播查询 → 组播应答。
/// 机器上已有别的 mDNS 服务（Bonjour 等）占着 5353 时应答器起不来，此时直接跳过断言。
/// </summary>
public class MdnsResponderNetworkTests
{
    [Fact]
    public async Task Responder_AnswersMulticastQueryWithOurAddress()
    {
        using var responder = new MdnsResponder();
        if (!responder.Active) return;   // 5353 被占：环境不支持组播，跳过

        var group = IPAddress.Parse("224.0.0.251");
        var name = MdnsPacket.EncodeName("llmwiki.local");

        // 客户端绑临时端口并要求单播应答：避开 Windows 同端口组播只投一个 socket 的坑
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var query = new List<byte>
        {
            0x12, 0x34,             // ID
            0x00, 0x00,             // Flags：查询
            0x00, 0x01,             // QDCOUNT = 1
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        query.AddRange(name);
        query.AddRange([0x00, 0x01]);   // QTYPE = A
        query.AddRange([0x80, 0x01]);   // QCLASS = IN | unicast-response
        var bytes = query.ToArray();

        await client.SendAsync(bytes, bytes.Length, new IPEndPoint(group, 5353));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        UdpReceiveResult? answer = null;
        for (var i = 0; i < 10 && answer is null; i++)
        {
            UdpReceiveResult r;
            try { r = await client.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (MdnsPacket.FindARecordFor(r.Buffer, name) != null) answer = r;
        }

        Assert.NotNull(answer);
        var ip = MdnsPacket.FindARecordFor(answer.Value.Buffer, name);
        Assert.NotNull(ip);
        // 应告的必须是本机真实局域网地址，不能是 0.0.0.0 / 回环
        Assert.False(IPAddress.IsLoopback(ip));
    }
}
