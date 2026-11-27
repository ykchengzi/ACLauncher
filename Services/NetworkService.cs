using System.Diagnostics;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>
/// 一个可测速的下载源（GitHub 直连或 gh-proxy 加速镜像）。
/// </summary>
public class DownloadSourceItem
{
    /// <summary>显示名。</summary>
    public string Name { get; init; } = "";

    /// <summary>镜像地址（以 / 结尾）。直连时为空字符串。</summary>
    public string Url { get; init; } = "";

    /// <summary>测速结果（毫秒），未测为 -1。</summary>
    public double LatencyMs { get; set; } = -1;

    /// <summary>测速状态：等待 / 测试中 / 可用 / 超时。</summary>
    public string State { get; set; } = "等待测试";

    /// <summary>测速显示文本。</summary>
    public string Display => $"{Name}  {LatencyText}";

    /// <summary>延迟文本（未测速时显示占位）。</summary>
    public string LatencyText => LatencyMs < 0 ? "--" : $"{LatencyMs:0} ms";

    /// <summary>是否可用（测速成功）。</summary>
    public bool IsUsable => LatencyMs >= 0;

    /// <summary>是否被选中。</summary>
    public bool IsSelected { get; set; }
}

/// <summary>
/// 网络环境服务：维护 GitHub 加速镜像列表、测试各镜像连通性与延迟。
/// 用户选择哪个镜像，后续 GitHub 相关下载（版本文件等）就使用哪个加速域名。
/// </summary>
public static class NetworkService
{
    /// <summary>
    /// 内置 GitHub 加速镜像列表（gh-proxy 类，均以 / 结尾）。
    /// 直连（空字符串）始终排在第一位；加速源仅保留 Gh-Proxy 官方镜像。
    /// </summary>
    public static readonly string[] GitHubMirrors =
    {
        // 直连
        "",
        // Gh-Proxy 官方加速（用法：【域名】/【原始 GitHub 链接】）
        "https://gh-proxy.org/",
    };

    /// <summary>
    /// 生成可测速的下载源列表。
    /// </summary>
    public static List<DownloadSourceItem> CreateSourceItems()
    {
        var items = new List<DownloadSourceItem>();
        for (var i = 0; i < GitHubMirrors.Length; i++)
        {
            var url = GitHubMirrors[i];
            var name = string.IsNullOrEmpty(url)
                ? "GitHub 直连"
                : url.TrimEnd('/');
            items.Add(new DownloadSourceItem { Name = name, Url = url });
        }
        return items;
    }

    /// <summary>
    /// 测速用的小型真实文件（GitHub 官方仓库 zip 归档，几十 KB，用于测量真实下载链路）。
    /// 直连测原地址；镜像测 {mirror}/{原地址}（gh-proxy 通用格式）。
    /// </summary>
    private const string SpeedTestFile = "https://github.com/github/gitignore/archive/refs/heads/main.zip";

    /// <summary>
    /// 对单个下载源测速：请求真实小文件，测量“连接 + 首字节”延迟。
    /// 每个源测 2 次取平均值（更稳定），任一次失败则该次不计入；全部失败返回 -1（超时）。
    /// </summary>
    public static async Task<double> TestLatencyAsync(string mirrorUrl)
    {
        var url = string.IsNullOrEmpty(mirrorUrl)
            ? SpeedTestFile
            : mirrorUrl.TrimEnd('/') + "/" + SpeedTestFile;

        var samples = new List<double>();
        for (var i = 0; i < 2; i++)
        {
            var ms = await TestOnceAsync(url);
            if (ms >= 0) samples.Add(ms);
        }
        return samples.Count == 0 ? -1 : samples.Average();
    }

    /// <summary>单次测速：GET 真实小文件，读到首个数据块即停止并计时（不下载整个文件）。</summary>
    private static async Task<double> TestOnceAsync(string url)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) return -1;

            await using var src = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[8192];
            var read = await src.ReadAsync(buffer, cts.Token);
            if (read == 0) return -1; // 流为空，链路异常
            return sw.Elapsed.TotalMilliseconds; // 首字节到达时间 = 连接 + 传输延迟
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 并发测试所有下载源（受 12 路并发限制，避免瞬时打满网络）。
    /// </summary>
    public static async Task TestAllAsync(List<DownloadSourceItem> items, IProgress<DownloadSourceItem>? progress = null)
    {
        var semaphore = new SemaphoreSlim(12);
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                item.State = "测试中";
                progress?.Report(item);
                var ms = await TestLatencyAsync(item.Url);
                item.LatencyMs = ms;
                item.State = ms >= 0 ? "可用" : "超时";
            }
            finally
            {
                semaphore.Release();
            }
            progress?.Report(item);
        }).ToList();
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 将 GitHub 原始地址转换为用户选择的加速地址。
    /// 未选择镜像（直连）时原样返回。
    /// </summary>
    public static string ApplyMirror(string url, string? mirrorUrl)
    {
        if (string.IsNullOrWhiteSpace(mirrorUrl) || !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        return mirrorUrl.TrimEnd('/') + "/" + url;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ACLauncher/1.0");
        return client;
    }
}
