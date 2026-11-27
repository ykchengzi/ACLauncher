using System.Text.Json;
using System.Text.Json.Serialization;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>
/// 一个可下载的版本项（一行一个版本）。
/// </summary>
public class DownloadableVersion
{
    /// <summary>版本 ID（展示用，从下载地址文件名推导；对应 versions/ 目录名）。</summary>
    public string Id { get; init; } = "";

    /// <summary>附加标签（可选，仅作展示，不参与版本 ID 与安装判定）。</summary>
    public string Build { get; init; } = "";

    /// <summary>展示标题（可选；优先于 Id/Build 显示，如"SadEPa的整合包 (26.2)"）。</summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// 原始下载地址（GitHub Release 的 .zip 完整地址，不带加速前缀）。
    /// 下载时自动使用"加速域名 + 原地址"拼接。
    /// </summary>
    public string DownloadUrl { get; init; } = "";

    /// <summary>说明（如"占位（未配置下载地址）"）。</summary>
    public string Note { get; init; } = "";

    /// <summary>版本类型（Mojang 清单 type 字段：release / snapshot / old_beta / old_alpha）。</summary>
    public string VersionType { get; init; } = "";

    /// <summary>版本分类中文名（正式版 / 快照 / 旧版）。</summary>
    [JsonIgnore]
    public string Category => VersionType switch
    {
        "snapshot" => "快照",
        "old_beta" or "old_alpha" => "旧版",
        _ => "正式版",
    };

    /// <summary>是否可下载（有真实下载地址）。</summary>
    public bool CanDownload => !string.IsNullOrWhiteSpace(DownloadUrl);

    /// <summary>渠道标签（正式版 / 测试版 / 远古版；无类型信息的条目为空）。</summary>
    [JsonIgnore]
    public string ChannelLabel => VersionType switch
    {
        "snapshot" => "测试版",
        "old_beta" or "old_alpha" => "远古版",
        "release" => "正式版",
        _ => "",
    };

    /// <summary>是否显示渠道标签。</summary>
    [JsonIgnore]
    public bool HasChannelLabel => ChannelLabel.Length > 0;

    /// <summary>渠道图标文件名（Blocks\ 下的方块贴图）。</summary>
    [JsonIgnore]
    public string IconFile => VersionType switch
    {
        "snapshot" => "command_block.png",
        "old_beta" or "old_alpha" => "cobblestone.png",
        _ => "grass_block.png",
    };

    /// <summary>是否已安装到本地。</summary>
    [JsonIgnore]
    public bool Installed { get; set; }

    /// <summary>"已安装"标记的可见性。</summary>
    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility InstalledVisibility =>
        Installed ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>构建号展示文本。</summary>
    [JsonIgnore]
    public string BuildText => string.IsNullOrEmpty(Build) ? "" : $"v{Build}";

    /// <summary>列表主文本（优先用标题，如"SadEPa的整合包 (26.2)"；否则用版本号 + 构建号）。</summary>
    [JsonIgnore]
    public string MainText => string.IsNullOrEmpty(Title)
        ? $"{Id}{(string.IsNullOrEmpty(Build) ? "" : "  " + Build)}"
        : Title;

    /// <summary>列表展示文本（优先用标题，否则用 ID + 构建号）。</summary>
    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Title)
        ? $"{Id}{(string.IsNullOrEmpty(Build) ? "" : "  " + Build)}{(Installed ? "（已安装）" : "")}"
        : $"{Title}{(Installed ? "（已安装）" : "")}";
}

/// <summary>
/// 版本下载服务：
///  1. 从网络设置读取一个或多个 GitHub Release 下载地址（.zip）；
///  2. 下载时使用"用户选择的加速域名 + 原始地址"拼接（未选加速域名则直连）；
///  3. 下载完成后用 7-Zip 内核自动解压，删除原 zip；
///  4. 解压内容合并进 .minecraft，自动识别版本并设为当前版本。
/// </summary>
public class VersionDownloadService
{
    public static VersionDownloadService Instance { get; } = new();

    /// <summary>内置下载条目：整合包（zip + 安装流程）或官方原版（Mojang 清单直装）。</summary>
    private sealed record BuiltInDownloadEntry(
        string Url,
        string Title,
        string? Id = null,
        string? InstalledVersionId = null,
        bool IsVanilla = false,
        string Note = "");

    /// <summary>
    /// 内置的版本下载地址列表（GitHub Release 的 .zip 完整地址）。
    /// 由启动器开发者维护，用户无需（也不允许）在界面中手动配置。
    /// 新增整合包：把 GitHub Release 地址填进 Url，Title 为列表展示标题，
    /// Id 为整合包在 versions 目录中的版本名（缺省时从 zip 文件名推导），
    /// InstalledVersionId 为安装后的版本目录名（整合包安装产物，如 26.2-fabric-0.19.5）。
    /// 官方原版：IsVanilla=true，Id 为原版版本号（URL 仅作占位不下载）。
    /// </summary>
    private static readonly BuiltInDownloadEntry[] BuiltInDownloadUrls =
    {
        new(
            "http://gh-proxy.org/https://github.com/ykchengzi/ACLauncher/releases/download/%E6%95%B4%E5%90%88%E5%8C%85/26.2_By_SadEPa.zip",
            "SadEPa的整合包 (26.2)",
            "26.2",
            "26.2-fabric-0.19.5",
            IsVanilla: false,
            Note: "由 SadEPa 制作与维护。"),
        new("", "1.20.1", "1.20.1", IsVanilla: true, Note: "Mojang 官方原版。"),
        new("", "1.20.4", "1.20.4", IsVanilla: true, Note: "Mojang 官方原版。"),
        new("", "1.21.11", "1.21.11", IsVanilla: true, Note: "Mojang 官方原版。"),
        new("", "26.2", "26.2", IsVanilla: true, Note: "Mojang 官方原版。"),
        new("", "26.3", "26.3", IsVanilla: true, Note: "Mojang 官方原版。"),
    };

    /// <summary>未配置下载地址时展示的占位版本列表（1.20.1、26.2、1.21.11 均为独立的 MC 版本号）。</summary>
    private static readonly (string Id, string Build)[] PlaceholderVersions =
    {
        ("1.20.1", ""),
        ("26.2", ""),
        ("1.21.11", ""),
    };

    /// <summary>
    /// 获取可下载版本列表：
    /// 整合包来自内置条目；官方版本从 Mojang / BMCLAPI 版本清单获取全部版本（含正式版 / 快照 / 旧版）。
    /// </summary>
    public async Task<List<DownloadableVersion>> GetVersionListAsync()
    {
        var result = new List<DownloadableVersion>();
        var mcDir = ConfigService.Instance.MinecraftDirectory;
        var versionsDir = Path.Combine(mcDir, "versions");
        var installed = Directory.Exists(versionsDir)
            ? Directory.GetDirectories(versionsDir).Select(d => Path.GetFileName(d) ?? "").ToHashSet()
            : new HashSet<string>();

        var urls = ParseDownloadUrls();
        if (urls.Count == 0)
        {
            // 等待配置：展示待定版本（暂不可下载）
            foreach (var (id, build) in PlaceholderVersions)
            {
                result.Add(new DownloadableVersion
                {
                    Id = id,
                    Build = build,
                    Note = "等待配置下载地址",
                    Installed = installed.Contains(id),
                });
            }
            return result;
        }

        // ---- 整合包条目 ----
        foreach (var entry in urls.Where(e => !e.IsVanilla))
        {
            var id = string.IsNullOrEmpty(entry.Id) ? DeriveVersionId(entry.Url) : entry.Id;
            result.Add(new DownloadableVersion
            {
                Id = id,
                Title = entry.Title,
                DownloadUrl = entry.Url,
                Note = entry.Note,
                Installed = IsEntryInstalled(mcDir, installed, id, entry.InstalledVersionId),
            });
        }

        // ---- 官方版本：全量清单（正式版 / 快照 / 旧版），失败时回退内置条目 ----
        var source = SettingsService.Instance.Load().VanillaDownloadSource == VanillaSourceService.SourceMojang
            ? VanillaSourceService.SourceMojang
            : VanillaSourceService.SourceBmclapi;
        try
        {
            var manifest = await VanillaSourceService.GetStringWithFallbackAsync(
                VanillaSourceService.GetManifestUrls(source));
            using var doc = System.Text.Json.JsonDocument.Parse(manifest);
            foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
            {
                var id = v.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                var type = v.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "" : "";
                var releaseDate = "";
                if (v.TryGetProperty("releaseTime", out var rtEl) &&
                    DateTime.TryParse(rtEl.GetString(), out var rt))
                {
                    releaseDate = rt.ToString("yyyy-MM-dd");
                }

                result.Add(new DownloadableVersion
                {
                    Id = id,
                    Title = id,
                    DownloadUrl = "vanilla://" + id,
                    VersionType = type,
                    Note = releaseDate,
                    Installed = installed.Contains(id),
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"获取官方版本清单失败，使用内置版本列表：{ex.Message}");
            foreach (var entry in urls.Where(e => e.IsVanilla))
            {
                var id = entry.Id ?? "";
                if (string.IsNullOrEmpty(id)) continue;
                result.Add(new DownloadableVersion
                {
                    Id = id,
                    Title = entry.Title,
                    DownloadUrl = "vanilla://" + id,
                    Note = entry.Note,
                    Installed = installed.Contains(id),
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 判断内置整合包是否已安装：以版本目录内的安装标记（.aclauncher_pack）为准，
    /// 只有真正通过启动器安装成功的版本才算，避免残留 / 手动放置的版本目录误报。
    /// 1. 显式产物目录（InstalledVersionId）带标记；
    /// 2. 或存在以 "{版本ID}-" 开头且带标记的加载器版本目录。
    /// </summary>
    private static bool IsEntryInstalled(string mcDir, HashSet<string> installedDirs, string baseId, string? installedVersionId)
    {
        if (PackInstallService.HasPackMarker(mcDir, installedVersionId ?? ""))
        {
            return true;
        }
        var prefix = baseId + "-";
        return installedDirs.Any(d =>
            d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            PackInstallService.HasPackMarker(mcDir, d));
    }

    /// <summary>
    /// 下载所选版本（zip）→ 7-Zip 解压 → 删除原 zip → 整合包安装 / 合并进 .minecraft → 自动识别版本。
    /// </summary>
    /// <param name="version">要下载的版本。</param>
    /// <param name="progress">下载进度回调（可选，供进度条与预计剩余时间使用）。</param>
    /// <param name="cancellationToken">取消令牌（可选，用户点击取消时终止下载）。</param>
    public async Task DownloadVersionAsync(DownloadableVersion version,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken? cancellationToken = null,
        Action<string>? onStatus = null)
    {
        // 记录下载前的版本目录快照：取消 / 失败时清理本次新建的半成品版本目录，
        // 避免“下载取消后选择版本页仍出现该版本”。
        var versionsDir = Path.Combine(ConfigService.Instance.MinecraftDirectory, "versions");
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(versionsDir))
            {
                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    if (Path.GetFileName(dir) is { Length: > 0 } name) before.Add(name);
                }
            }
        }
        catch { /* 快照失败则跳过清理 */ }

        try
        {
            await DownloadVersionCoreAsync(version, progress, cancellationToken, onStatus);
        }
        catch
        {
            CleanupPartialVersions(versionsDir, before);
            throw;
        }
    }

    /// <summary>清理本次下载新产生（下载前不存在）的版本目录。</summary>
    private static void CleanupPartialVersions(string versionsDir, HashSet<string> before)
    {
        try
        {
            if (!Directory.Exists(versionsDir)) return;
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || before.Contains(name)) continue;
                try
                {
                    Directory.Delete(dir, true);
                    LogService.Instance.Log($"已清理未完成安装的版本目录：{name}");
                }
                catch { /* 文件占用等，忽略 */ }
            }
        }
        catch { /* 忽略 */ }
    }

    private async Task DownloadVersionCoreAsync(DownloadableVersion version,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken? cancellationToken = null,
        Action<string>? onStatus = null)
    {
        if (!version.CanDownload)
        {
            throw new InvalidOperationException("该版本暂不可下载。");
        }

        // ---- 官方原版分支：走 Mojang / BMCLAPI 清单安装（幂等），完成后直接设为当前版本 ----
        if (version.DownloadUrl.StartsWith("vanilla://", StringComparison.OrdinalIgnoreCase))
        {
            await PackInstallService.Instance.InstallVanillaVersionAsync(version.Id, onStatus, progress);

            var cfg = await ConfigService.Instance.LoadAsync();
            cfg.GameVersion = version.Id;
            await ConfigService.Instance.SaveAsync(cfg);
            LogService.Instance.Log($"原版 {version.Id} 安装完成，已设为当前版本。");
            return;
        }

        var settings = SettingsService.Instance.Load();
        var mirror = settings.DownloadSource;
        var mcDir = ConfigService.Instance.MinecraftDirectory;
        Directory.CreateDirectory(mcDir);

        // ---- 1. 加速链接下载 zip ----
        var fileName = GetUrlFileName(version.DownloadUrl);
        if (string.IsNullOrEmpty(fileName)) fileName = version.Id + ".zip";
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) fileName += ".zip";

        var downloadDir = Path.Combine(mcDir, ".aclauncher_cache");
        var zipPath = Path.Combine(downloadDir, fileName);
        Directory.CreateDirectory(downloadDir);

        var finalUrl = NetworkService.ApplyMirror(version.DownloadUrl, mirror);
        LogService.Instance.Log($"开始下载：{finalUrl}");

        var task = new DownloadTask
        {
            Urls = new List<string> { finalUrl, version.DownloadUrl }, // 加速优先，失败回退直连
            LocalPath = zipPath,
            DisplayName = fileName,
        };
        var ok = await DownloadService.Instance.DownloadOneAsync(task, progress, cancellationToken);
        if (!ok)
        {
            throw new InvalidOperationException($"下载失败：{fileName}");
        }

        // ---- 2. 7-Zip 解压 ----
        var extractDir = Path.Combine(mcDir, ".aclauncher_install");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        var method = await SevenZipService.Instance.ExtractAsync(zipPath, extractDir);

        // ---- 3. 删除原 zip ----
        try { File.Delete(zipPath); } catch { /* 忽略清理失败 */ }
        try { if (Directory.Exists(downloadDir) && !Directory.EnumerateFileSystemEntries(downloadDir).Any()) Directory.Delete(downloadDir); }
        catch { /* 忽略 */ }

        // ---- 4. 检测整合包格式并安装（Modrinth 完整安装：下载依赖 + 安装加载器；其余格式通用合并） ----
        var packVersion = await PackInstallService.Instance.TryInstallPackAsync(extractDir, version.Title);
        if (!string.IsNullOrEmpty(packVersion))
        {
            // Modrinth 整合包已生成独立版本目录并完成安装
            try { Directory.Delete(extractDir, true); } catch { /* 忽略 */ }

            var cfgService = ConfigService.Instance;
            var cfg = await cfgService.LoadAsync();
            cfg.GameVersion = packVersion;
            await cfgService.SaveAsync(cfg);
            LogService.Instance.Log($"整合包安装完成，已设为当前版本：{packVersion}");
            return;
        }

        // ---- 5. 通用合并进 .minecraft（兼容 zip 内含 .minecraft 前缀） ----
        MergeIntoMinecraft(extractDir, mcDir);

        // 清理解压临时目录
        try { Directory.Delete(extractDir, true); } catch { /* 忽略 */ }

        LogService.Instance.Log($"版本包解压完成（{method}），文件已合并到 .minecraft。");

        // ---- 5. 自动识别版本并设为当前版本 ----
        var configService = ConfigService.Instance;
        var auto = configService.AutoDetectVersion();
        var config = await configService.LoadAsync();
        if (auto is not null)
        {
            config.GameVersion = auto;
            await configService.SaveAsync(config);
            // 通用合并的整合包同样写入安装标记（"已安装"判定依据）
            PackInstallService.WritePackMarker(mcDir, auto, version.Title);
            LogService.Instance.Log($"已自动识别版本：{auto}");
        }
        else
        {
            LogService.Instance.Log("警告：未在 versions 目录识别到版本，请检查压缩包内容。");
        }
    }

    /// <summary>
    /// 只下载整合包源文件（zip）到用户指定路径，不解压、不安装。
    /// </summary>
    public async Task SaveSourceFileAsync(DownloadableVersion version, string destFilePath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken? cancellationToken = null,
        Action<string>? onStatus = null)
    {
        if (!version.CanDownload)
        {
            throw new InvalidOperationException("该整合包暂不可下载。");
        }
        if (version.DownloadUrl.StartsWith("vanilla://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("官方原版没有可保存的整合包源文件。");
        }

        var settings = SettingsService.Instance.Load();
        var finalUrl = NetworkService.ApplyMirror(version.DownloadUrl, settings.DownloadSource);

        var destDir = Path.GetDirectoryName(destFilePath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        onStatus?.Invoke("正在下载源文件...");
        LogService.Instance.Log($"开始下载整合包源文件：{finalUrl}");

        var task = new DownloadTask
        {
            Urls = new List<string> { finalUrl, version.DownloadUrl },
            LocalPath = destFilePath,
            DisplayName = Path.GetFileName(destFilePath),
        };
        var ok = await DownloadService.Instance.DownloadOneAsync(task, progress, cancellationToken);
        if (!ok)
        {
            throw new IOException("整合包源文件下载失败。");
        }
    }

    // =====================================================================
    // 辅助方法
    // =====================================================================

    /// <summary>解析下载条目：原版条目始终提供；整合包条目内置优先，兼容旧版设置中已保存的地址。</summary>
    private static List<BuiltInDownloadEntry> ParseDownloadUrls()
    {
        var entries = new List<BuiltInDownloadEntry>();

        // 兼容：旧版本可能在 settings.json 中保存过整合包 zip 地址
        var raw = SettingsService.Instance.Load().VersionDownloadUrls ?? "";
        if (!string.IsNullOrWhiteSpace(raw))
        {
            entries.AddRange(raw
                .Split(new[] { ';', '；', ',', '，', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .Select(u => new BuiltInDownloadEntry(u, "")));
        }

        // 内置条目（整合包 + 全部原版），按 URL 去重避免旧设置重复
        entries.AddRange(BuiltInDownloadUrls);

        return entries
            .DistinctBy(e => e.IsVanilla ? "vanilla://" + e.Id : e.Url)
            .ToList();
    }

    /// <summary>从 URL 推导版本 ID（取文件名去扩展名；失败则用主机名+文件名摘要）。</summary>
    private static string DeriveVersionId(string url)
    {
        var name = GetUrlFileName(url);
        if (!string.IsNullOrEmpty(name))
        {
            return Path.GetFileNameWithoutExtension(name);
        }
        // 兜底：取地址最后一段
        var trimmed = url.TrimEnd('/');
        var last = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return string.IsNullOrWhiteSpace(last) ? "未知版本" : last;
    }

    /// <summary>从 URL 提取文件名（去掉查询参数）。</summary>
    private static string? GetUrlFileName(string url)
    {
        try
        {
            var withoutQuery = url;
            var q = withoutQuery.IndexOf('?');
            if (q >= 0) withoutQuery = withoutQuery[..q];

            var trimmed = withoutQuery.TrimEnd('/');
            var last = trimmed[(trimmed.LastIndexOf('/') + 1)..];
            return string.IsNullOrWhiteSpace(last) ? null : Uri.UnescapeDataString(last);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把解压目录内容合并进 .minecraft（兼容 zip 内含 .minecraft 子目录的情况）。</summary>
    private static void MergeIntoMinecraft(string sourceDir, string mcDir)
    {
        // 解压内容根：优先 .minecraft 子目录，否则视为整合包内容直接合并
        var root = sourceDir;
        var innerMc = Path.Combine(sourceDir, ".minecraft");
        if (Directory.Exists(innerMc)) root = innerMc;

        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, dir);
            Directory.CreateDirectory(Path.Combine(mcDir, rel));
        }
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var dest = Path.Combine(mcDir, rel);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
