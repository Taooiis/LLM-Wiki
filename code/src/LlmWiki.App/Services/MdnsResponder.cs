using System.Net;
using System.Net.Sockets;

namespace LlmWiki.App.Services;

/// <summary>
/// mDNS 应答器：分享服务开启时，让手机/平板用 llmwiki.local:端口 访问分享页，免手输 IP。
///
/// 约定（RFC 6762 最小子集）：
/// - 监听 UDP 5353 的组播组 224.0.0.251；
/// - 只答自己域名的 A 记录，其它查询不理（不抢机器上 Bonjour 等服务的流量）；
/// - 查询要单播就单播回，否则组播回；
/// - 别人抢了同一个域名就自动改名 llmwiki-2 / llmwiki-3…（否则手机会连到别人的机器）。
///
/// 端口 5353 被占用（机器上另有 mDNS 服务）时静默失效——二维码与直输 IP 仍是可用路径。
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    public const string DefaultHostName = "llmwiki";

    private const int MdnsPort = 5353;
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    private readonly UdpClient? _client;
    private readonly CancellationTokenSource _cts = new();
    private byte[] _nameWire;
    private string _hostName;
    private int _conflictCount;

    /// <summary>当前对外的主机名（不含 .local 后缀）。冲突改名后会变。</summary>
    public string HostName => _hostName;

    /// <summary>mDNS 是否可用（5353 抢占失败时为 false，不影响其它分享能力）。</summary>
    public bool Active => _client != null;

    public MdnsResponder(string hostName = DefaultHostName)
    {
        _hostName = hostName;
        _nameWire = MdnsPacket.EncodeName(hostName + ".local");
        try
        {
            var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            client.JoinMulticastGroup(MdnsGroup);
            // 注意：不要关 MulticastLoopback。Windows 上关掉它连「收到的」组播也一起收不到
            // （实测：开启后才能收到局域网其它设备的 mDNS 查询）。自己发出的应答会被
            // QR 位过滤掉、冲突检测也会认出自己的 IP，不会自问自答。
            _client = client;
        }
        catch
        {
            _client = null;   // 5353 被占：mDNS 不可用，IP/二维码路径照常
            return;
        }
        _ = Task.Run(() => ListenAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var client = _client;
        if (client == null) return;

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await client.ReceiveAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            // 别人宣称了我们这个名字 → 让名（llmwiki → llmwiki-2 → …）
            // 只在收到「应答」时才判冲突：查询包里的问题段不是宣称
            if (MdnsPacket.IsResponse(received.Buffer)
                && MdnsPacket.FindARecordFor(received.Buffer, _nameWire) is { } claimed
                && !IsOwnAddress(claimed))
            {
                RenameOnConflict();
                continue;
            }

            var response = MdnsPacket.BuildResponse(
                received.Buffer, _nameWire, PickAddress(received.RemoteEndPoint.Address), out var wantsUnicast);
            if (response == null) continue;

            var target = wantsUnicast
                ? received.RemoteEndPoint
                : new IPEndPoint(MdnsGroup, MdnsPort);
            try { await client.SendAsync(response, response.Length, target); }
            catch { /* 单次发送失败忽略，等下一次查询 */ }
        }
    }

    /// <summary>名字冲突：加后缀重新起名，并重新编码查询名。</summary>
    private void RenameOnConflict()
    {
        _conflictCount++;
        _hostName = $"{DefaultHostName}-{_conflictCount + 1}";
        _nameWire = MdnsPacket.EncodeName(_hostName + ".local");
    }

    /// <summary>这个 IP 是不是本机自己的（自己回自己的包不算冲突）。</summary>
    private static bool IsOwnAddress(IPAddress ip)
    {
        foreach (var own in LocalAddresses())
            if (own.Equals(ip)) return true;
        return false;
    }

    /// <summary>本机 IPv4 局域网地址（不含回环）。缓存起来：mDNS 流量密时每次重解析太慢。</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses() => _localAddresses.Value;

    private static readonly Lazy<IReadOnlyList<IPAddress>> _localAddresses = new(() =>
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    list.Add(ip);
        }
        catch { /* 取不到就当没有局域网地址 */ }
        return list;
    });

    /// <summary>
    /// 选要通告的 IP：优先与询问方同 /24 的本机地址（多网卡时更准），否则取第一个局域网地址。
    /// </summary>
    private static IPAddress PickAddress(IPAddress remote)
    {
        var candidates = LocalAddresses();
        foreach (var ip in candidates)
        {
            var a = ip.GetAddressBytes();
            var b = remote.GetAddressBytes();
            if (a[0] == b[0] && a[1] == b[1] && a[2] == b[2]) return ip;
        }
        return candidates.Count > 0 ? candidates[0] : IPAddress.Loopback;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _client?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
