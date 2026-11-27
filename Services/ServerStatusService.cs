using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ACLauncher.Services;

/// <summary>
/// 一次服务器状态查询的结果。
/// </summary>
public record ServerStatus
{
    /// <summary>服务器是否在线（TCP 连接 + 状态握手成功）。</summary>
    public bool Online { get; init; }

    /// <summary>往返延迟（毫秒）。离线时为 -1。</summary>
    public int LatencyMs { get; init; } = -1;

    /// <summary>服务器 MOTD（描述文本，已取纯文本）。离线时为空。</summary>
    public string Motd { get; init; } = "";

    /// <summary>在线玩家数。离线时为 -1。</summary>
    public int PlayersOnline { get; init; } = -1;

    /// <summary>玩家上限。离线时为 -1。</summary>
    public int PlayersMax { get; init; } = -1;

    /// <summary>服务器游戏版本名（如 "26.2"）。</summary>
    public string VersionName { get; init; } = "";
}

/// <summary>
/// Minecraft 服务器状态查询服务（Server List Ping 协议）。
/// 纯 .NET Socket 实现，无 UI 依赖：
///   1. TCP 连接 host:port（限时）；
///   2. 发送 Handshake 包（协议版本 -1， nextState=1 状态查询）；
///   3. 发送空的 Status Request 包；
///   4. 读取 JSON 响应解析人数 / MOTD / 版本。
/// 任何一步失败都返回 Online=false 的结果，不抛异常。
/// </summary>
public class ServerStatusService
{
    public static ServerStatusService Instance { get; } = new();

    /// <summary>状态查询默认端点（内部使用；界面仅展示 mc.ambercat.top，不展示查询地址）。</summary>
    public const string DefaultQueryHost = "103.85.86.151";
    public const int DefaultQueryPort = 30411;

    /// <summary>查询服务器状态。离线或网络异常时返回 Online=false，不抛异常。</summary>
    public async Task<ServerStatus> QueryAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            await client.ConnectAsync(host, port, timeoutCts.Token);
            var latencyMs = (int)sw.ElapsedMilliseconds;
            if (latencyMs < 1) latencyMs = 1;

            await using var stream = client.GetStream();

            // ---- Handshake 包：协议版本 -1（状态查询专用），nextState=1 ----
            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);          // 包 ID：Handshake
            WriteVarInt(handshake, -1);            // 协议版本：-1（status）
            WriteString(handshake, host);          // 服务器地址
            handshake.WriteByte((byte)(port >> 8)); // 端口（ushort 大端）
            handshake.WriteByte((byte)(port & 0xFF));
            WriteVarInt(handshake, 0x01);          // next state：1 = Status

            await WritePacketAsync(stream, handshake.ToArray(), timeoutCts.Token);

            // ---- Status Request 包：只有包 ID，无内容 ----
            await WritePacketAsync(stream, new[] { (byte)0x00 }, timeoutCts.Token);

            // ---- 读响应：包 ID 0x00 + JSON 字符串 ----
            var body = await ReadPacketBodyAsync(stream, timeoutCts.Token);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var status = new ServerStatus
            {
                Online = true,
                LatencyMs = latencyMs,
                Motd = ExtractMotd(root),
                VersionName = root.TryGetProperty("version", out var ver) &&
                              ver.TryGetProperty("name", out var verName)
                    ? verName.GetString() ?? ""
                    : "",
            };

            if (root.TryGetProperty("players", out var players))
            {
                if (players.TryGetProperty("online", out var onlineEl) && onlineEl.TryGetInt32(out var online))
                    status = status with { PlayersOnline = online };
                if (players.TryGetProperty("max", out var maxEl) && maxEl.TryGetInt32(out var max))
                    status = status with { PlayersMax = max };
            }

            return status;
        }
        catch
        {
            // 连接失败 / 超时 / 响应解析失败：一律按离线处理
            return new ServerStatus { Online = false };
        }
    }

    // ===================== 协议底层（VarInt / 包读写） =====================

    /// <summary>写入一个 VarInt（Minecraft 协议的变长整数，支持负数）。</summary>
    private static void WriteVarInt(Stream stream, int value)
    {
        var v = (uint)value;
        while (true)
        {
            if ((v & 0xFFFFFF80u) == 0)
            {
                stream.WriteByte((byte)v);
                return;
            }
            stream.WriteByte((byte)((v & 0x7Fu) | 0x80u));
            v >>= 7;
        }
    }

    /// <summary>写入 VarInt 前缀字符串（UTF-8）。</summary>
    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte[] body, CancellationToken ct)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, body.Length);
        packet.Write(body);
        var bytes = packet.ToArray();
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>读取一个完整数据包的包体（包 ID + 内容），返回去掉包 ID 后的文本。</summary>
    private static async Task<string> ReadPacketBodyAsync(NetworkStream stream, CancellationToken ct)
    {
        // 包总长（VarInt）
        var length = await ReadVarIntAsync(stream, ct);
        if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("状态响应长度异常。");

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct);
            if (n <= 0) throw new EndOfStreamException("服务器提前断开连接。");
            read += n;
        }

        using var body = new MemoryStream(buffer);
        var packetId = ReadVarInt(body);
        if (packetId != 0x00) throw new InvalidDataException($"状态响应包 ID 异常：{packetId}");

        // JSON 字符串：VarInt 长度 + UTF-8 内容
        var jsonLength = ReadVarInt(body);
        if (jsonLength <= 0 || jsonLength > body.Length - (int)body.Position)
            throw new InvalidDataException("状态响应 JSON 长度异常。");

        var jsonBytes = new byte[jsonLength];
        body.ReadExactly(jsonBytes);
        return Encoding.UTF8.GetString(jsonBytes);
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        var result = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = await stream.ReadByteAsync(ct);
            result |= (b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("VarInt 过长。");
    }

    /// <summary>从已缓冲的内存流读取一个 VarInt（同步，避免假异步）。</summary>
    private static int ReadVarInt(MemoryStream stream)
    {
        var result = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("VarInt 过长。");
    }

    /// <summary>
    /// 从状态 JSON 提取 MOTD 纯文本。
    /// description 可能是字符串，也可能是 {"text":...,"extra":[...]} 结构；
    /// 同时清理 §x 颜色码，避免 UI 显示乱码。
    /// </summary>
    private static string ExtractMotd(JsonElement root)
    {
        if (!root.TryGetProperty("description", out var desc)) return "";

        var sb = new StringBuilder();
        AppendMotd(desc, sb);
        return Regex.Replace(sb.ToString(), "§.", "").Trim();
    }

    private static void AppendMotd(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append(element.GetString());
                break;
            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    sb.Append(text.GetString());
                }
                if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in extra.EnumerateArray())
                    {
                        AppendMotd(item, sb);
                    }
                }
                break;
        }
    }
}

/// <summary>NetworkStream 的按字节读取扩展（异步）。</summary>
internal static class NetworkStreamExtensions
{
    public static async Task<byte> ReadByteAsync(this NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var n = await stream.ReadAsync(buffer, ct);
        if (n <= 0) throw new EndOfStreamException("服务器提前断开连接。");
        return buffer[0];
    }
}
