using System.Buffers.Binary;
using System.Net;

namespace LlmWiki.App.Services;

/// <summary>
/// mDNS 纯协议层：只做 DNS 报文的解析与应答构造，不碰网络（便于单测）。
/// 只认 A 记录（IPv4）与自己的域名，其它查询一律不理——不抢机器上其它 mDNS 服务的流量。
/// </summary>
public static class MdnsPacket
{
    /// <summary>把 "llmwiki.local" 编码成 DNS 名称字节（长度前缀标签 + 结束 0）。</summary>
    public static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0) continue;
            var b = System.Text.Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)b.Length);
            bytes.AddRange(b);
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    /// <summary>
    /// 收到查询 → 构造应答字节；不是问我们的 / 类型不对 / 格式非法都返回 null。
    /// wantsUnicast：询问方要单播应答（mDNS qclass 的 0x8000 位）。
    /// </summary>
    public static byte[]? BuildResponse(byte[] query, byte[] ourNameWire, IPAddress ip, out bool wantsUnicast)
    {
        wantsUnicast = false;
        if (query.Length < 12) return null;

        var flags = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2, 2));
        if ((flags & 0x8000) != 0) return null;                                   // 这是应答，不是查询
        if (BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)) != 1) return null; // 只处理单问题

        // 问题段：名称（未压缩）+ 类型(2) + 类(2)
        var nameLen = SkipName(query, 12);
        if (nameLen <= 0) return null;
        var qEnd = 12 + nameLen;
        if (qEnd + 4 > query.Length) return null;

        // 只应答自己域名的查询
        if (!query.AsSpan(12, nameLen).SequenceEqual(ourNameWire)) return null;

        var qType = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(qEnd, 2));
        if (qType != 1 && qType != 255) return null;                              // A 或 ANY
        var qClass = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(qEnd + 2, 2));
        wantsUnicast = (qClass & 0x8000) != 0;

        // 组装应答：原样复制问题 + 一条 A 记录（记录名用压缩指针指回问题里的名字）
        // 记录字段：NAME(2 压缩指针) + TYPE(2) + CLASS(2) + TTL(4) + RDLENGTH(2) + RDATA(4) = 16
        var answer = new byte[qEnd + 4 + 2 + 2 + 2 + 4 + 2 + 4];
        query.AsSpan(0, qEnd + 4).CopyTo(answer);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2, 2), 0x8400); // QR=1（应答）AA=1（权威）
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4, 2), 1);      // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(6, 2), 1);      // ANCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(8, 2), 0);      // NSCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(10, 2), 0);     // ARCOUNT

        var p = qEnd + 4;
        answer[p++] = 0xC0; answer[p++] = 0x0C;                                  // 压缩指针 → 偏移 12
        answer[p++] = 0; answer[p++] = 1;                                         // TYPE = A
        answer[p++] = 0; answer[p++] = 1;                                         // CLASS = IN
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(p, 4), 120);          // TTL 120 秒
        p += 4;
        answer[p++] = 0; answer[p++] = 4;                                         // RDLENGTH
        ip.GetAddressBytes().CopyTo(answer.AsSpan(p, 4));
        return answer;
    }

    /// <summary>这个报文是不是「应答」（DNS 头 QR 位 = 1）。查询包为 0。</summary>
    public static bool IsResponse(byte[] msg) =>
        msg.Length >= 4 && (BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(2, 2)) & 0x8000) != 0;

    /// <summary>
    /// 在一条 mDNS 应答里找我们域名的 A 记录；找到返回其 IP，没有返回 null。
    /// 用于冲突检测：别的机器也宣称同一个域名时，说明名字撞了。
    /// </summary>
    public static IPAddress? FindARecordFor(byte[] msg, byte[] nameWire)
    {
        if (msg.Length < 12) return null;
        var qd = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4, 2));
        var an = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6, 2));
        var pos = 12;

        for (var q = 0; q < qd; q++)
        {
            var n = SkipName(msg, pos);
            if (n < 0 || pos + n + 4 > msg.Length) return null;
            pos += n + 4;
        }

        for (var a = 0; a < an; a++)
        {
            if (!TryReadName(msg, pos, out var name, out var used)) return null;
            pos += used;
            if (pos + 10 > msg.Length) return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos, 2));
            pos += 8;                                  // type(2) + class(2) + ttl(4)
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos, 2));
            pos += 2;
            if (pos + rdlen > msg.Length) return null;
            if (type == 1 && rdlen == 4 && name.AsSpan().SequenceEqual(nameWire))
                return new IPAddress(msg.AsSpan(pos, 4).ToArray());
            pos += rdlen;
        }
        return null;
    }

    /// <summary>跳过一段名称，返回占用字节数；压缩指针算 2 字节。格式非法返回 -1。</summary>
    public static int SkipName(byte[] msg, int offset)
    {
        var i = offset;
        while (i < msg.Length)
        {
            var len = msg[i];
            if (len == 0) return i - offset + 1;
            if ((len & 0xC0) == 0xC0) return i - offset + 2;
            if ((len & 0xC0) != 0) return -1;
            i += 1 + len;
            if (i > msg.Length) return -1;
        }
        return -1;
    }

    /// <summary>读取名称并展开压缩指针；name 为展开后的字节，consumed 为原报文里占用的字节数。</summary>
    public static bool TryReadName(byte[] msg, int offset, out byte[] name, out int consumed)
    {
        name = [];
        consumed = 0;
        var parts = new List<byte>();
        var i = offset;
        var jumped = false;
        var guard = 0;

        while (i < msg.Length && guard++ < 128)
        {
            var len = msg[i];
            if (len == 0)
            {
                i++;
                if (!jumped) consumed = i - offset;
                parts.Add(0);                 // 结束 0，与 EncodeName 的线格式一致
                name = [.. parts];
                return true;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (i + 1 >= msg.Length) return false;
                var ptr = ((len & 0x3F) << 8) | msg[i + 1];
                if (!jumped) { consumed = i - offset + 2; jumped = true; }
                i = ptr;
                continue;
            }
            if ((len & 0xC0) != 0) return false;
            if (i + 1 + len > msg.Length) return false;
            parts.Add((byte)len);
            for (var k = 0; k < len; k++) parts.Add(msg[i + 1 + k]);
            i += 1 + len;
        }
        return false;
    }
}
