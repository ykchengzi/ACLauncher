using System.Text.Json;

namespace ACLauncher.Services;

/// <summary>
/// 原版 Minecraft 下载源定义与清单获取。
/// 源分两类（用户在"网络设置"中选择，保存在 settings.json 的 VanillaDownloadSource）：
///   - mojang：Mojang 官方（piston-meta.mojang.com）
///   - bmclapi：BMCLAPI 镜像（bmclapi2.bangbang93.com）
/// 清单与版本 JSON 的获取：按用户所选源优先，另一源自动兜底，均带 10 秒超时，
/// 避免直连官方卡死（原"卡在连接下载源"的根因就是 GetStringAsync 无候选无超时回退）。
/// 核心包 / 依赖库 / 资源文件的下载仍由 DownloadService 处理（其任务列表已内置镜像候选）。
/// </summary>
public static class VanillaSourceService
{
    /// <summary>Mojang 官方 meta 域名。</summary>
    public const string MojangMetaBase = "https://piston-meta.mojang.com";

    /// <summary>BMCLAPI 镜像 meta 域名。</summary>
    public const string BmclapiMetaBase = "https://bmclapi2.bangbang93.com";

    /// <summary>官方版本清单 v2。</summary>
    public const string ManifestPath = "/mc/game/version_manifest_v2.json";

    /// <summary>原版下载源标识（settings.json 存储）。</summary>
    public const string SourceMojang = "mojang";
    public const string SourceBmclapi = "bmclapi";

    /// <summary>清单 URL：primary 优先，失败自动换兜底源。</summary>
    public static List<string> GetManifestUrls(string source) => source switch
    {
        SourceBmclapi => new List<string> { BmclapiMetaBase + ManifestPath, MojangMetaBase + ManifestPath },
        _ => new List<string> { MojangMetaBase + ManifestPath, BmclapiMetaBase + ManifestPath },
    };

    /// <summary>把 piston-meta 官方地址替换为用户所选源的地址（两源都保留，官方兜底）。</summary>
    public static List<string> GetVersionJsonUrls(string source, string officialUrl)
    {
        var primary = source switch
        {
            SourceBmclapi => officialUrl.Replace(MojangMetaBase, BmclapiMetaBase),
            _ => officialUrl,
        };
        var fallback = source == SourceBmclapi ? officialUrl : officialUrl.Replace(MojangMetaBase, BmclapiMetaBase);
        var urls = new List<string> { primary };
        if (fallback != primary) urls.Add(fallback);
        return urls;
    }

    /// <summary>
    /// 下载版本清单文本：按候选顺序尝试，单个候选 10 秒超时，失败自动换下一个。
    /// </summary>
    public static async Task<string> GetStringWithFallbackAsync(IEnumerable<string> urls)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var client = new HttpClient(HttpFactory.CreateHandler(TimeSpan.FromSeconds(10))) { Timeout = TimeSpan.FromSeconds(10) };
                return await client.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                last = ex;
                LogService.Instance.Log($"下载源不可用，正在切换：{url}（{ex.Message}）");
            }
        }
        throw new InvalidOperationException(
            $"所有下载源均不可用，请检查网络或稍后重试。{last?.Message}");
    }

    /// <summary>从版本清单中找到目标版本的 JSON 地址。</summary>
    public static string? FindVersionUrl(string manifestJson, string versionId)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (v.GetProperty("id").GetString() == versionId)
            {
                return v.GetProperty("url").GetString();
            }
        }
        return null;
    }
}
