namespace ACLauncher.Services;

/// <summary>
/// 版本图标解析：整合包优先用包内自带图标，其次是模组加载器图标，最后按版本渠道取方块图标。
/// 返回值要么是 Blocks\ 下的文件名（如 "fabric.png"），要么是整合包图标的绝对路径。
/// </summary>
public static class VersionIconService
{
    /// <summary>整合包图标在版本目录里统一使用的文件名。</summary>
    public const string PackIconFileName = "icon.png";

    /// <summary>整合包内常见的图标文件名（按优先级）。</summary>
    private static readonly string[] IconCandidates =
    {
        "icon.png", "pack.png", "logo.png", "modpack.png", "icon.jpg", "icon.jpeg",
    };

    /// <summary>
    /// 解析某个已安装版本的图标。优先级：
    ///   1. 版本目录里自带的图标（整合包安装时提取，或手动放入）；
    ///   2. 模组加载器图标；
    ///   3. 版本渠道图标（正式版 / 测试版 / 远古版 / 版本异常）。
    /// </summary>
    public static string Resolve(string versionId)
    {
        var versionsDir = ConfigService.Instance.VersionsDirectory;

        // 1. 优先用该版本自己的图标
        var builtin = FindBuiltinIcon(versionId, versionsDir);
        if (builtin is not null) return builtin;

        // 2. 模组加载器图标（NeoForge 是独立加载器，图标沿用 forge.png）
        var loaderIcon = LoaderIconFile(ConfigService.Instance.GetVersionLoaderLabel(versionId));
        if (loaderIcon is not null) return loaderIcon;

        // 3. 版本渠道图标
        return VersionChannelService.IconFile(
            VersionChannelService.DetectInstalled(versionId, versionsDir));
    }

    /// <summary>版本目录里自带的图标（按常见文件名查找，找不到返回 null）。</summary>
    public static string? FindBuiltinIcon(string versionId, string versionsDir)
    {
        try
        {
            var versionDir = Path.Combine(versionsDir, versionId);
            if (!Directory.Exists(versionDir)) return null;

            foreach (var name in IconCandidates)
            {
                var hit = Directory.EnumerateFiles(versionDir, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }
        }
        catch
        {
            // 读不到目录不影响其它图标来源
        }
        return null;
    }

    /// <summary>版本目录里的整合包图标路径（不存在返回 null）。</summary>
    public static string? GetPackIconPath(string versionId, string versionsDir)
        => FindBuiltinIcon(versionId, versionsDir);

    /// <summary>加载器 → 图标文件名。</summary>
    public static string? LoaderIconFile(string loaderLabel) => loaderLabel switch
    {
        "Fabric" => "fabric.png",
        "Forge" => "forge.png",
        "NeoForge" => "forge.png",
        "Quilt" => "quilt.png",
        _ => null,
    };

    /// <summary>
    /// 从整合包解压目录里找内置图标，复制到版本目录的 icon.png。
    /// 依次查找包根目录、.minecraft 目录与 overrides 目录。
    /// </summary>
    public static void TryCopyPackIcon(string packDir, string mcDir, string versionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(versionId)) return;

            var versionDir = Path.Combine(mcDir, "versions", versionId);
            if (!Directory.Exists(versionDir)) return;

            var target = Path.Combine(versionDir, PackIconFileName);
            foreach (var folder in new[]
                     {
                         packDir,
                         Path.Combine(packDir, ".minecraft"),
                         Path.Combine(packDir, "overrides"),
                     })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var name in IconCandidates)
                {
                    var hit = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) continue;

                    File.Copy(hit, target, true);
                    LogService.Instance.Log($"已提取整合包图标：{Path.GetFileName(hit)} → {versionId}\\{PackIconFileName}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"提取整合包图标失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 整合包里没有图标时，从 Modrinth 项目下载图标保存为版本目录的 icon.png。
    /// </summary>
    public static async Task<bool> TryDownloadIconAsync(Uri iconUri, string mcDir, string versionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(versionId)) return false;

            var versionDir = Path.Combine(mcDir, "versions", versionId);
            if (!Directory.Exists(versionDir)) return false;

            var target = Path.Combine(versionDir, PackIconFileName);
            if (File.Exists(target)) return true;

            using var client = new HttpClient(HttpFactory.CreateHandler())
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ACLauncher/1.0");

            var bytes = await client.GetByteArrayAsync(iconUri);
            if (bytes.Length < 64) return false;

            await File.WriteAllBytesAsync(target, bytes);
            LogService.Instance.Log($"已下载整合包图标：{versionId}\\{PackIconFileName}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"下载整合包图标失败：{ex.Message}");
            return false;
        }
    }
}
