using System.Xml.Linq;

namespace ACLauncher.Services;

/// <summary>
/// 模组加载器版本解析与安装入口。
/// 版本号优先从 BMCLAPI 镜像获取（国内快），失败回退官方源：
///   Fabric:  fabric-meta /v2/versions/loader（取最新 stable）
///   Forge:   bmclapi /forge/minecraft/{mc}（取该 MC 可用的最高版本）
///   NeoForge: maven.neoforged.net maven-metadata.xml（前缀匹配 MC 版本）
/// </summary>
public class LoaderService
{
    public static LoaderService Instance { get; } = new();

    /// <summary>查询指定加载器在某个 MC 版本下的最新可用加载器版本号；不支持时返回 null。</summary>
    public async Task<string?> GetLatestLoaderVersionAsync(string mc, string kind)
    {
        try
        {
            return kind switch
            {
                "fabric" => await GetLatestFabricAsync(),
                "quilt" => await GetLatestQuiltAsync(),
                "forge" => await GetLatestForgeAsync(mc),
                "neoforge" => await GetLatestNeoForgeAsync(mc),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"查询 {kind} 加载器版本失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<string?> GetLatestFabricAsync()
    {
        foreach (var url in new[]
        {
            "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader",
            "https://meta.fabricmc.net/v2/versions/loader",
        })
        {
            try
            {
                var json = await GetStringAsync(url);
                if (string.IsNullOrWhiteSpace(json)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) continue;

                string? first = null;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var version = item.TryGetProperty("version", out var v) ? v.GetString() : null;
                    if (string.IsNullOrEmpty(version)) continue;
                    var stable = item.TryGetProperty("stable", out var s) &&
                                 s.ValueKind == System.Text.Json.JsonValueKind.True;
                    if (stable) return version;
                    first ??= version;
                }
                if (first is not null) return first;
            }
            catch { /* 换下一个源 */ }
        }
        return null;
    }

    private static async Task<string?> GetLatestQuiltAsync()
    {
        try
        {
            var json = await GetStringAsync("https://meta.quiltmc.org/v3/versions/loader");
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            // Quilt 的版本列表已按新到旧排序，取第一个非 beta 的版本优先
            string? first = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var version = item.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (string.IsNullOrEmpty(version)) continue;
                if (!version.Contains("beta", StringComparison.OrdinalIgnoreCase) &&
                    !version.Contains("alpha", StringComparison.OrdinalIgnoreCase))
                {
                    return version;
                }
                first ??= version;
            }
            return first;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetLatestForgeAsync(string mc)
    {
        // 1) BMCLAPI 的 Forge 版本列表
        try
        {
            var json = await GetStringAsync($"https://bmclapi2.bangbang93.com/forge/minecraft/{mc}");
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("empty");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                string? best = null;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var version = item.TryGetProperty("version", out var v) ? v.GetString() : null;
                    if (string.IsNullOrEmpty(version) || version is "latest" or "recommended") continue;
                    if (best is null || CompareVersions(version, best) > 0) best = version;
                }
                if (best is not null) return best;
            }
        }
        catch { /* 换兜底源 */ }

        // 2) promotions 清单（官方 / BMCLAPI），取 <mc>-latest，其次 -recommended
        foreach (var url in new[]
        {
            "https://bmclapi2.bangbang93.com/forge/promotions_slim.json",
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",
        })
        {
            try
            {
                var json = await GetStringAsync(url);
                if (string.IsNullOrWhiteSpace(json)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("promos", out var promos))
                {
                    if (promos.TryGetProperty($"{mc}-latest", out var latest) && latest.GetString() is { Length: > 0 } l)
                        return l;
                    if (promos.TryGetProperty($"{mc}-recommended", out var rec) && rec.GetString() is { Length: > 0 } r)
                        return r;
                }
            }
            catch { /* 换下一个源 */ }
        }

        return null;
    }

    /// <summary>短超时 GET（避免加载器查询长时间卡住）；失败返回 null。</summary>
    private static async Task<string?> GetStringAsync(string url)
    {
        try
        {
            using var http = new HttpClient(HttpFactory.CreateHandler(TimeSpan.FromSeconds(6))) { Timeout = TimeSpan.FromSeconds(6) };
            return await http.GetStringAsync(url);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetLatestNeoForgeAsync(string mc)
    {
        try
        {
            // NeoForge 版本号形如 20.4.237（对应 MC 1.20.4）或 21.1.80（对应 1.21.1）
            var mcParts = mc.Split('.');
            var prefix = mcParts[0] == "1" && mcParts.Length >= 2
                ? $"{mcParts[1]}.{mcParts.ElementAtOrDefault(2) ?? "0"}"
                : mc;

            using var http = new HttpClient(HttpFactory.CreateHandler()) { Timeout = TimeSpan.FromSeconds(12) };
            var xml = await http.GetStringAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
            var doc = XDocument.Parse(xml);

            string? best = null;
            foreach (var version in doc.Descendants("version").Select(e => e.Value))
            {
                if (!version.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
                if (best is null || CompareVersions(version, best) > 0) best = version;
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>数字段比较版本号（忽略非数字后缀）。</summary>
    private static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.', '-', '+')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();

        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }

    /// <summary>安装加载器（下载依赖库并生成版本目录），返回版本目录名。</summary>
    public Task<string> InstallAsync(string mc, string kind, string loaderVersion) =>
        PackInstallService.Instance.InstallModLoaderAsync(mc, kind, loaderVersion);

    /// <summary>查询指定加载器在某个 MC 版本下的可用加载器版本列表（新到旧）；不支持时返回空表。</summary>
    public async Task<List<string>> GetLoaderVersionsAsync(string mc, string kind)
    {
        try
        {
            switch (kind)
            {
                case "fabric":
                {
                    var json = await GetStringAsync("https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader")
                               ?? await GetStringAsync("https://meta.fabricmc.net/v2/versions/loader");
                    return ParseVersionArray(json);
                }
                case "quilt":
                {
                    var json = await GetStringAsync("https://meta.quiltmc.org/v3/versions/loader");
                    return ParseVersionArray(json);
                }
                case "forge":
                {
                    var json = await GetStringAsync($"https://bmclapi2.bangbang93.com/forge/minecraft/{mc}");
                    var list = ParseVersionArray(json);
                    return list.OrderByDescending(v => v, Comparer<string>.Create(CompareVersions)).ToList();
                }
                case "neoforge":
                {
                    var mcParts = mc.Split('.');
                    var prefix = mcParts[0] == "1" && mcParts.Length >= 2
                        ? $"{mcParts[1]}.{mcParts.ElementAtOrDefault(2) ?? "0"}"
                        : mc;
                    using var http = new HttpClient(HttpFactory.CreateHandler()) { Timeout = TimeSpan.FromSeconds(12) };
                    var xml = await http.GetStringAsync(
                        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
                    var doc = XDocument.Parse(xml);
                    return doc.Descendants("version").Select(e => e.Value)
                        .Where(v => v.StartsWith(prefix + ".", StringComparison.Ordinal))
                        .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
                        .ToList();
                }
                default:
                    return new List<string>();
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"查询 {kind} 加载器版本列表失败：{ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>从 [{"version": "..."}] 结构的 JSON 中提取版本号列表。</summary>
    private static List<string> ParseVersionArray(string? json)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var version = item.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (!string.IsNullOrEmpty(version) && version is not ("latest" or "recommended"))
            {
                result.Add(version);
            }
        }
        return result;
    }
}
