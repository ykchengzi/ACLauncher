using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACLauncher.Services;

/// <summary>更新清单（托管在 Cloudflare Pages / Worker 上的 latest.json）。</summary>
public class UpdateInfo
{
    /// <summary>最新版本号，如 "0.99.2"。</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>更新说明（可换行）。</summary>
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    /// <summary>安装包下载地址（.exe）。</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>安装包 SHA-256（可选；提供则校验）。</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    /// <summary>是否强制更新（可选）。</summary>
    [JsonPropertyName("mandatory")] public bool Mandatory { get; set; }
}

/// <summary>
/// 在线更新服务：
///   1. 从更新清单地址（Cloudflare Pages/Worker 托管的 latest.json）获取最新版本；
///   2. 与当前程序版本比较，有新版本时返回更新信息；
///   3. 下载安装包并按 SHA-256 校验；
///   4. 静默运行安装器（Inno Setup），安装器会自动关闭旧进程并覆盖，随后启动新版。
/// 清单与安装包均为纯静态文件，无需 Worker 逻辑，免费版 Pages/Worker 即可承载。
/// </summary>
public class UpdateService
{
    public static UpdateService Instance { get; } = new();

    /// <summary>
    /// 更新清单地址。把 latest.json 放到 Cloudflare Pages（或 Worker 静态路由）后，
    /// 将默认值改成你的实际地址。示例：https://aclauncher.pages.dev/latest.json
    /// </summary>
    public static string ManifestUrl { get; set; } = "https://aclauncher.pages.dev/latest.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>当前程序版本（读取 exe 文件版本，如 0.99.3）。</summary>
    public string CurrentVersion
    {
        get
        {
            try
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "ACLauncher.exe");
                var fv = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                return string.IsNullOrWhiteSpace(fv) ? "0.0.0" : fv;
            }
            catch
            {
                return "0.0.0";
            }
        }
    }

    /// <summary>检查更新；无新版本或网络失败返回 null（不抛异常）。</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(ManifestUrl, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var info = JsonSerializer.Deserialize<UpdateInfo>(json);
            if (info is null || string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.Url))
            {
                return null;
            }

            return CompareVersion(info.Version, CurrentVersion) > 0 ? info : null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"检查更新失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 下载安装包并运行安装器。返回 false 表示下载失败。
    /// 下载完成后按清单的 SHA-256 校验（清单提供时）。
    /// </summary>
    public async Task<bool> DownloadAndRunAsync(UpdateInfo info,
        IProgress<(long received, long total)>? progress = null,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ACLauncher-Update");
        Directory.CreateDirectory(dir);
        var fileName = $"ACLauncher-Setup-{info.Version}.exe";
        var target = Path.Combine(dir, fileName);

        try
        {
            // 只用清单里给出的地址，不做任何镜像替换
            using (var request = new HttpRequestMessage(HttpMethod.Get, info.Url))
            {
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return false;

                var total = response.Content.Headers.ContentLength ?? -1;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    var read = await src.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    progress?.Report((received, total));
                }
                await dst.FlushAsync(ct);
            }

            // SHA-256 校验
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                var actual = await ComputeSha256Async(target);
                if (!string.Equals(actual, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Instance.Log($"更新包校验失败（SHA-256 不匹配）：期望 {info.Sha256}，实际 {actual}");
                    try { File.Delete(target); } catch { }
                    return false;
                }
            }

            // 静默安装：Inno Setup 会自动关闭运行中的旧进程并覆盖安装
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"下载更新失败：{ex.Message}");
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>比较版本号（按数字段）；a &gt; b 返回正数。</summary>
    private static int CompareVersion(string a, string b)
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
}
