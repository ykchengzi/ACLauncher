using ACLauncher.Helpers;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>
/// 配置服务：读写启动器根目录下的 config.json，并提供 .minecraft 目录路径。
/// 启动时如果 config.json 不存在，自动生成一个默认配置。
/// 如果 game_version 为空，自动扫描 versions 目录取最新版本。
/// </summary>
public class ConfigService
{
    public static ConfigService Instance { get; } = new();

    /// <summary>config.json 绝对路径（与 ACLauncher.exe 同级）。</summary>
    public string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>.minecraft 目录绝对路径（默认 %AppData%\.minecraft，即官方启动器的标准位置）。</summary>
    public string MinecraftDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>versions 目录绝对路径。</summary>
    public string VersionsDirectory => Path.Combine(MinecraftDirectory, "versions");

    /// <summary>
    /// 读取配置。
    /// - 文件不存在：自动生成默认配置（game_version 留空，触发自动识别）。
    /// - game_version 为空：自动扫描 versions 目录取最新版本。
    /// </summary>
    public async Task<Config> LoadAsync()
    {
        // 配置不存在时自动生成默认配置
        if (!File.Exists(ConfigPath))
        {
            var defaultConfig = new Config { GameVersion = "" };
            await SaveAsync(defaultConfig);
            LogService.Instance.Log($"未找到 config.json，已自动生成：{ConfigPath}");
        }

        Config? config;
        try
        {
            config = await JsonHelper.DeserializeFromFileAsync<Config>(ConfigPath);
        }
        catch (Exception ex)
        {
            throw new FormatException($"配置文件格式错误：{ConfigPath}\n{ex.Message}", ex);
        }

        if (config is null)
        {
            throw new FormatException($"配置文件内容为空：{ConfigPath}");
        }

        // game_version 为空 → 自动扫描 versions 目录选最新版本
        if (string.IsNullOrWhiteSpace(config.GameVersion))
        {
            var auto = AutoDetectVersion();
            if (auto is not null)
            {
                config.GameVersion = auto;
                // 把识别到的版本号写回 config.json，下次启动直接用
                await SaveAsync(config);
                LogService.Instance.Log($"未指定 game_version，自动识别到版本：{auto}");
            }
            else
            {
                LogService.Instance.Log("versions 目录下未找到任何版本");
            }
        }

        return config;
    }

    /// <summary>
    /// 自动扫描 versions 目录，按文件夹名（语义化版本）排序，返回最新版本名。
    /// 识别失败返回 null。
    /// </summary>
    public string? AutoDetectVersion()
    {
        try
        {
            if (!Directory.Exists(VersionsDirectory)) return null;

            var versions = Directory.GetDirectories(VersionsDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToList();

            if (versions.Count == 0) return null;

            // 按语义化版本排序（优先解析为 Version 比较，解析失败的按字符串排序排后面）
            var parsed = new List<(string Name, Version? Ver)>();
            foreach (var v in versions)
            {
                // Minecraft 版本号可能带前缀如 "1.20.4-forge-49.0"，取第一段尝试解析
                var firstPart = v.Split('-')[0].Split('_')[0];
                if (Version.TryParse(firstPart, out var ver))
                    parsed.Add((v, ver));
                else
                    parsed.Add((v, null));
            }

            var best = parsed
                .OrderByDescending(p => p.Ver, Comparer<Version?>.Create((a, b) =>
                {
                    if (a is null && b is null) return 0;
                    if (a is null) return -1;
                    if (b is null) return 1;
                    return a.CompareTo(b);
                }))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .First();

            return best.Name;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"自动识别版本失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>保存配置。</summary>
    public async Task SaveAsync(Config config)
    {
        await JsonHelper.SerializeToFileAsync(ConfigPath, config);
    }

    /// <summary>检查 .minecraft 目录是否存在。</summary>
    public bool IsMinecraftDirectoryValid()
        => Directory.Exists(MinecraftDirectory);

    /// <summary>
    /// 某个版本实际使用的 mods 目录（随版本隔离生效）：
    /// 隔离时为 versions/&lt;版本&gt;/mods，否则为共享的 .minecraft/mods。
    /// </summary>
    public string GetModsDirectory(string versionName) =>
        VersionIsolationService.GetModsDirectory(versionName);

    /// <summary>
    /// 获取本地已安装的所有版本名（versions 目录下每个非空子目录为一个版本）。
    /// </summary>
    public List<string> GetInstalledVersions()
    {
        if (!Directory.Exists(VersionsDirectory)) return new List<string>();

        return Directory.GetDirectories(VersionsDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name =>
            {
                try
                {
                    // 目录内存在同名 json（标准版本结构）或至少有一个文件（整合包/加载器结构）
                    var dir = Path.Combine(VersionsDirectory, name!);
                    if (File.Exists(Path.Combine(dir, name! + ".json"))) return true;
                    return Directory.EnumerateFileSystemEntries(dir).Any();
                }
                catch
                {
                    return false;
                }
            })
            .Select(name => name!)
            .ToList();
    }

    /// <summary>
    /// 识别某个已安装版本使用的模组加载器：原版 / Fabric / Quilt / Forge / NeoForge。
    /// 先看版本目录名，再读版本 JSON 的库列表（fabric-loader / quilt-loader / minecraftforge / neoforge）。
    /// </summary>
    public string GetVersionLoaderLabel(string versionName)
    {
        var lower = versionName.ToLowerInvariant();
        if (lower.Contains("neoforge")) return "NeoForge";
        if (lower.Contains("fabric")) return "Fabric";
        if (lower.Contains("quilt")) return "Quilt";
        if (lower.Contains("forge")) return "Forge";

        try
        {
            var jsonPath = Path.Combine(VersionsDirectory, versionName, versionName + ".json");
            if (!File.Exists(jsonPath)) return "原版";
            var json = File.ReadAllText(jsonPath);
            if (json.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
            if (json.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase)) return "Quilt";
            if (json.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)) return "Fabric";
            if (json.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase)) return "Forge";
            return "原版";
        }
        catch
        {
            return "原版";
        }
    }

    /// <summary>
    /// 取版本对应的 Minecraft 版本号（不是文件夹名）。
    /// 加载器版本的 JSON 里 inheritsFrom 指向原版版本号；原版版本取 id 字段。
    /// 读不到时回退到目录名。
    /// </summary>
    public string GetVersionMinecraftVersion(string versionName)
    {
        try
        {
            var jsonPath = Path.Combine(VersionsDirectory, versionName, versionName + ".json");
            if (File.Exists(jsonPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var parent) &&
                    parent.GetString() is { Length: > 0 } p)
                {
                    return p;
                }
                if (doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } v)
                {
                    return v;
                }
            }
        }
        catch { /* 回退目录名 */ }
        return versionName;
    }

    /// <summary>计算版本目录占用的磁盘大小（MB）。</summary>
    public double GetVersionSizeMB(string versionName)
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(VersionsDirectory, versionName));
            if (!dir.Exists) return 0;
            var bytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return bytes / 1024.0 / 1024.0;
        }
        catch
        {
            return 0;
        }
    }
}
