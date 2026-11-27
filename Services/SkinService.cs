using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACLauncher.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace ACLauncher.Services;

/// <summary>
/// 账户头像服务：为账户生成 64×64 的“头部”头像（缓存到本地）。
///  - 微软 / 外置登录：读取该角色当前皮肤，裁出头部（含帽子图层叠加）；
///  - 离线账户：按 Minecraft 的默认皮肤规则（UUID 哈希取余）从内置 9 款默认皮肤中选择；
///  - 皮肤获取失败时回退到默认皮肤，保证每个账户都有头像。
/// </summary>
public class SkinService
{
    public static SkinService Instance { get; } = new();

    private static readonly HttpClient Http = new(HttpFactory.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Minecraft 默认皮肤顺序（Java 版 DefaultSkinHelper 的取值顺序）。</summary>
    private static readonly string[] DefaultSkinNames =
    {
        "steve", "alex", "ari", "efe", "kai", "makena", "noor", "sunny", "zuri",
    };

    /// <summary>渲染后的头像缓存目录（用户本地目录，升级安装不会丢失）。</summary>
    private static string CacheDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ACLauncher", "heads");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>随程序分发的默认头像目录。</summary>
    private static string BundledSkinsDirectory => Path.Combine(AppContext.BaseDirectory, "Skins");

    /// <summary>同一账户同时被多处请求时共用一次生成任务，避免重复下载与写文件冲突。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> Inflight = new();

    /// <summary>
    /// 获取账户头像图片的本地路径；失败返回内置默认皮肤（不会返回 null，除非资源缺失）。
    /// </summary>
    public Task<string?> GetHeadImagePathAsync(Account account, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(CacheDirectory, CacheKey(account) + ".png");
        if (File.Exists(cacheFile)) return Task.FromResult<string?>(cacheFile);

        var key = cacheFile;
        var task = Inflight.GetOrAdd(key, _ => GenerateHeadAsync(account, cacheFile, ct));
        // 完成后移除，失败时下次可以重试
        _ = task.ContinueWith(t => Inflight.TryRemove(key, out _), TaskScheduler.Default);
        return task;
    }

    private async Task<string?> GenerateHeadAsync(Account account, string cacheFile, CancellationToken ct)
    {
        try
        {
            if (account.Type == AccountType.Offline)
            {
                var fallback = await ResolveDefaultHeadAsync(account.Uuid);
                if (fallback is not null)
                {
                    try { File.Copy(fallback, cacheFile, overwrite: true); } catch { return fallback; }
                    return cacheFile;
                }
                return null;
            }

            // 正版 / 外置登录：取纹理地址
            var skinUrl = await GetSkinUrlAsync(account, ct);
            if (string.IsNullOrEmpty(skinUrl)) return await ResolveDefaultHeadAsync(account.Uuid);

            var skinBytes = await Http.GetByteArrayAsync(skinUrl, ct);
            var ok = await RenderHeadAsync(skinBytes, cacheFile);
            return ok ? cacheFile : await ResolveDefaultHeadAsync(account.Uuid);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"获取账户头像失败（{account.PlayerName}）：{ex.Message}");
            return await ResolveDefaultHeadAsync(account.Uuid);
        }
    }

    /// <summary>按账户类型查询当前角色的皮肤纹理地址。</summary>
    private static async Task<string?> GetSkinUrlAsync(Account account, CancellationToken ct)
    {
        string profileUrl;
        if (account.Type == AccountType.Microsoft)
        {
            profileUrl = $"https://sessionserver.mojang.com/session/minecraft/profile/{StripDashes(account.Uuid)}";
        }
        else if (account.Type == AccountType.Yggdrasil && !string.IsNullOrWhiteSpace(account.AuthServer))
        {
            var root = account.AuthServer.TrimEnd('/');
            profileUrl = $"{root}/sessionserver/session/minecraft/profile/{StripDashes(account.Uuid)}";
        }
        else
        {
            return null;
        }

        var json = await Http.GetStringAsync(profileUrl, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("properties", out var props) ||
            props.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var prop in props.EnumerateArray())
        {
            if (prop.TryGetProperty("name", out var nameEl) && nameEl.GetString() != "textures") continue;
            if (!prop.TryGetProperty("value", out var valueEl)) continue;
            var base64 = valueEl.GetString();
            if (string.IsNullOrEmpty(base64)) continue;

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                using var texDoc = JsonDocument.Parse(decoded);
                if (texDoc.RootElement.TryGetProperty("textures", out var textures) &&
                    textures.TryGetProperty("SKIN", out var skin) &&
                    skin.TryGetProperty("url", out var urlEl))
                {
                    return urlEl.GetString();
                }
            }
            catch
            {
                // 纹理数据损坏，跳过
            }
        }

        return null;
    }

    /// <summary>把皮肤 PNG 裁剪成 64×64 的头部（脸部 + 帽子叠加）。</summary>
    private static async Task<bool> RenderHeadAsync(byte[] skinBytes, string destPath)
    {
        using var inStream = new InMemoryRandomAccessStream();
        await inStream.WriteAsync(skinBytes.AsBuffer());
        inStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inStream);
        using var source = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width < 64 || height < 32) return false;

        var pixels = new byte[width * height * 4];
        source.CopyToBuffer(pixels.AsBuffer());

        var scale = Math.Max(1, width / 64);      // 高清皮肤（128×128 等）
        var hasHat = height >= 64 * scale;
        var faceX = 8 * scale;
        var faceY = 8 * scale;
        var hatX = 40 * scale;

        var output = new byte[64 * 64 * 4];
        for (var oy = 0; oy < 64; oy++)
        {
            for (var ox = 0; ox < 64; ox++)
            {
                var sx = faceX + ox / 8;
                var sy = faceY + oy / 8;
                var si = (sy * width + sx) * 4;
                var di = (oy * 64 + ox) * 4;

                // 脸部（预乘 alpha）
                byte b = pixels[si], g = pixels[si + 1], r = pixels[si + 2], a = pixels[si + 3];

                if (hasHat)
                {
                    var hi = (sy * width + (hatX + ox / 8)) * 4;
                    var hb = pixels[hi];
                    var hg = pixels[hi + 1];
                    var hr = pixels[hi + 2];
                    var ha = pixels[hi + 3];
                    if (ha > 0)
                    {
                        // 预乘 alpha 叠加：out = over + under * (1 - alphaOver)
                        var inv = (255 - ha) / 255.0;
                        b = (byte)Math.Min(255, hb + b * inv);
                        g = (byte)Math.Min(255, hg + g * inv);
                        r = (byte)Math.Min(255, hr + r * inv);
                        a = (byte)Math.Min(255, ha + a * inv);
                    }
                }

                output[di] = b;
                output[di + 1] = g;
                output[di + 2] = r;
                output[di + 3] = a;
            }
        }

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 64, 64, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(output.AsBuffer());

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        outStream.Seek(0);
        var buffer = new byte[outStream.Size];
        using (var reader = new DataReader(outStream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(buffer);
        }

        // 先写临时文件再替换，避免半截文件被后续读取（同一账户可能被多处并发请求）
        var temp = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, buffer);
        try
        {
            File.Move(temp, destPath, overwrite: true);
        }
        catch (IOException) when (File.Exists(destPath))
        {
            // 另一个请求已经写好了目标文件，删掉本次临时文件即可
            try { File.Delete(temp); } catch { /* 忽略 */ }
        }
        return true;
    }

    /// <summary>按 UUID 选择内置默认皮肤头像（与游戏内默认皮肤规则一致）。</summary>
    private static string? DefaultSkinName(string uuid)
    {
        try
        {
            var index = (int)(JavaUuidHash(uuid) % DefaultSkinNames.Length + DefaultSkinNames.Length)
                        % DefaultSkinNames.Length;
            return DefaultSkinNames[index];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析默认头像：优先使用程序自带图片；否则从玩家本地的客户端 JAR 中提取（不随安装包分发 Mojang 素材）。
    /// </summary>
    private static async Task<string?> ResolveDefaultHeadAsync(string uuid)
    {
        var name = DefaultSkinName(uuid);
        if (name is null) return null;

        var bundled = Path.Combine(BundledSkinsDirectory, name + ".png");
        if (File.Exists(bundled)) return bundled;

        var cache = Path.Combine(CacheDirectory, "default-" + name + ".png");
        if (File.Exists(cache)) return cache;

        foreach (var jar in EnumerateClientJars())
        {
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(jar);
                var entry = zip.GetEntry($"assets/minecraft/textures/entity/player/wide/{name}.png");
                if (entry is null) continue;
                using var ms = new MemoryStream();
                await using (var es = entry.Open())
                {
                    await es.CopyToAsync(ms);
                }
                if (await RenderHeadAsync(ms.ToArray(), cache)) return cache;
            }
            catch
            {
                // 某个 jar 读不了就换下一个
            }
        }

        return null;
    }

    /// <summary>枚举本地客户端 JAR（体积较大的才可能是完整客户端）。</summary>
    private static IEnumerable<string> EnumerateClientJars()
    {
        var versionsDir = ConfigService.Instance.VersionsDirectory;
        if (!Directory.Exists(versionsDir)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            foreach (var jar in Directory.EnumerateFiles(dir, "*.jar"))
            {
                long length;
                try { length = new FileInfo(jar).Length; } catch { continue; }
                if (length > 5_000_000) yield return jar;
            }
        }
    }

    /// <summary>Java 版 UUID.hashCode()：高 64 位与低 64 位异或后折叠成 int。</summary>
    private static int JavaUuidHash(string uuid)
    {
        var hex = StripDashes(uuid);
        if (hex.Length < 32) return 0;
        if (!ulong.TryParse(hex[..16], System.Globalization.NumberStyles.HexNumber, null, out var msb)) return 0;
        if (!ulong.TryParse(hex[16..32], System.Globalization.NumberStyles.HexNumber, null, out var lsb)) return 0;

        var hilo = msb ^ lsb;
        return unchecked((int)(hilo >> 32) ^ (int)hilo);
    }

    private static string StripDashes(string uuid) => (uuid ?? "").Replace("-", "").Trim();

    /// <summary>缓存键：账户类型 + 服务器 + UUID。</summary>
    private static string CacheKey(Account account)
    {
        var raw = $"{account.Type}|{account.AuthServer}|{StripDashes(account.Uuid)}|{account.PlayerName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }
}
