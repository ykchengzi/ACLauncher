using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using ACLauncher.Helpers;

namespace ACLauncher.Services;

/// <summary>
/// Java 运行时自动下载服务。
/// 当本机没有版本所需的 Java（如 26.2 要求 Java 25）时，从 Mojang 官方运行时分发清单
/// （piston-meta / piston-data）按 manifest 逐文件下载对应 JRE 到 .minecraft/runtime/ 下，
/// 并以 BMCLAPI 镜像兜底。布局与官方启动器一致：runtime/&lt;component&gt;/&lt;platform&gt;/。
/// </summary>
public class JavaRuntimeService
{
    public static JavaRuntimeService Instance { get; } = new();

    /// <summary>Mojang 官方运行时总清单（各平台 → 组件 → manifest）。</summary>
    private const string RuntimeManifestUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// 确保指定 Mojang 运行时组件已安装，返回其 javaw.exe 完整路径。
    /// 已安装则直接复用（幂等）。
    /// </summary>
    /// <param name="component">Mojang 组件名，如 java-runtime-epsilon。</param>
    /// <param name="progress">下载进度（已完成, 总数, 当前文件）。</param>
    public async Task<string> EnsureRuntimeAsync(string component,
        IProgress<(int done, int total, string current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var platform = GetPlatformName();
        var root = Path.Combine(ConfigService.Instance.MinecraftDirectory, "runtime", component, platform);
        var javaw = Path.Combine(root, "bin", "javaw.exe");
        var marker = Path.Combine(root, ".installed");

        // 幂等：安装标记 + 主程序存在
        if (File.Exists(javaw) && File.Exists(marker))
        {
            LogService.Instance.Log($"Java 运行时已就绪：{component}（{File.ReadAllText(marker).Trim()}）");
            return javaw;
        }

        Directory.CreateDirectory(root);

        // 1. 下载总清单
        LogService.Instance.Log($"正在准备 Java 运行时 {component} ...");
        var allText = await DownloadStringWithMirrorAsync(RuntimeManifestUrl, cancellationToken);
        using var all = JsonDocument.Parse(allText);
        if (!all.RootElement.TryGetProperty(platform, out var platEl) ||
            !platEl.TryGetProperty(component, out var compEl) ||
            compEl.ValueKind != JsonValueKind.Array ||
            compEl.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"运行时清单中未找到 {platform} 平台的 {component}。");
        }

        var entry = compEl[0];
        var manifestUrl = entry.GetProperty("manifest").GetProperty("url").GetString()
            ?? throw new InvalidOperationException("运行时 manifest 地址为空。");
        var versionName = entry.TryGetProperty("version", out var ve) && ve.TryGetProperty("name", out var vn)
            ? vn.GetString() ?? ""
            : "";

        // 2. 下载文件 manifest
        LogService.Instance.Log($"正在下载 Java 运行时清单（{versionName}）...");
        var manifestText = await DownloadStringWithMirrorAsync(manifestUrl, cancellationToken);
        using var manifest = JsonDocument.Parse(manifestText);

        // 3. 构造逐文件下载任务
        var tasks = new List<DownloadTask>();
        var files = manifest.RootElement.GetProperty("files");
        foreach (var fileProp in files.EnumerateObject())
        {
            var rel = fileProp.Name.Replace('/', Path.DirectorySeparatorChar);
            var info = fileProp.Value;
            var type = info.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "directory")
            {
                Directory.CreateDirectory(Path.Combine(root, rel));
                continue;
            }
            if (type != "file")
            {
                // link（符号链接）等其它类型在 Windows 上跳过
                continue;
            }

            if (!info.TryGetProperty("downloads", out var downloads) ||
                !downloads.TryGetProperty("raw", out var raw))
            {
                continue;
            }

            var url = raw.GetProperty("url").GetString()!;
            var sha1 = raw.TryGetProperty("sha1", out var sh) ? sh.GetString() : null;
            long size = raw.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            var local = Path.Combine(root, rel);

            // 已存在且大小一致则跳过（运行时目录由启动器管理，内容不可变）
            if (File.Exists(local) && size > 0 && new FileInfo(local).Length == size)
            {
                continue;
            }

            tasks.Add(new DownloadTask
            {
                Urls = BuildRuntimeUrls(url),
                LocalPath = local,
                ExpectedSha1 = sha1,
                DisplayName = $"Java {Path.GetFileName(rel)}",
            });
        }

        // 4. 多线程下载
        var maxThreads = SettingsService.Instance.Load().DownloadThreads;
        if (tasks.Count > 0)
        {
            LogService.Instance.Log($"正在下载 Java 运行时（{tasks.Count} 个文件，{maxThreads} 线程）...");
            var failed = await DownloadService.Instance.DownloadManyAsync(tasks, maxThreads, progress, cancellationToken);
            if (failed.Count > 0)
            {
                throw new IOException($"Java 运行时有 {failed.Count} 个文件下载失败，请检查网络或稍后重试。");
            }
        }

        // 5. 完成标记
        if (!File.Exists(javaw))
        {
            throw new FileNotFoundException($"Java 运行时安装完成但缺少可执行文件：{javaw}");
        }
        File.WriteAllText(marker, versionName);
        LogService.Instance.Log($"Java 运行时安装完成：{component}（{versionName}）");

        return javaw;
    }

    /// <summary>为运行时文件构造候选 URL（官方源 + BMCLAPI 镜像）。</summary>
    private static List<string> BuildRuntimeUrls(string officialUrl)
    {
        var urls = new List<string> { officialUrl };
        var mirror = officialUrl
            .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com")
            .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com");
        if (mirror != officialUrl) urls.Add(mirror);
        return urls;
    }

    /// <summary>下载清单 JSON 文本（官方 + BMCLAPI 顺序尝试）。</summary>
    private static async Task<string> DownloadStringWithMirrorAsync(string url, CancellationToken ct)
    {
        var candidates = new List<string>
        {
            url,
            url.Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com")
                .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com"),
        }.Distinct().ToList();

        Exception? last = null;
        foreach (var u in candidates)
        {
            try
            {
                using var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadAsStringAsync(ct);
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new IOException($"无法下载运行时清单：{url}", last);
    }

    /// <summary>当前系统对应的 Mojang 运行时平台键。</summary>
    private static string GetPlatformName()
    {
        if (!OperatingSystem.IsWindows()) return "linux";
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "windows-arm64",
            Architecture.X86 => "windows-x86",
            _ => "windows-x64",
        };
    }
}
