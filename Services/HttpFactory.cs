using System.Net;
using System.Net.Sockets;

namespace ACLauncher.Services;

/// <summary>
/// 共享的 HTTP 客户端构造工具。
/// 国内不少网络环境下 IPv6 不可达，而 DNS 往往把 AAAA 记录排在前面，
/// 默认的 SocketsHttpHandler 会在 IPv6 上耗完整个 ConnectTimeout，
/// 表现为「curl 能访问、启动器一直超时」。这里按 IPv4 优先的顺序建立连接。
/// </summary>
public static class HttpFactory
{
    /// <summary>创建 IPv4 优先的 SocketsHttpHandler。</summary>
    public static SocketsHttpHandler CreateHandler(TimeSpan? connectTimeout = null)
    {
        return new SocketsHttpHandler
        {
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            ConnectCallback = ConnectIpv4FirstAsync,
        };
    }

    private static async ValueTask<Stream> ConnectIpv4FirstAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        // 字面 IP 直接连
        if (IPAddress.TryParse(host, out var literal))
        {
            return await ConnectAsync(literal, port, ct);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch
        {
            addresses = Array.Empty<IPAddress>();
        }

        var ordered = addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToList();
        if (ordered.Count == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        Exception? last = null;
        foreach (var address in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await ConnectAsync(address, port, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.ConnectionRefused);
    }

    private static async Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
