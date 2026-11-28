using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ACLauncher.Helpers;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>整合包格式（检测依据参考 PCL2 的整合包识别方案）。</summary>
public enum PackFormat
{
    /// <summary>无法识别。</summary>
    Unknown,

    /// <summary>Modrinth（根目录含 modrinth.index.json）。</summary>
    Modrinth,

    /// <summary>CurseForge（根目录含 manifest.json 且无 addons 字段）。</summary>
    CurseForge,

    /// <summary>HMCL（根目录含 modpack.json）。</summary>
    Hmcl,

    /// <summary>MultiMC（根目录含 mmc-pack.json / instance.cfg）。</summary>
    MultiMc,

    /// <summary>MCBBS（根目录含 mcbbs.packmeta）。</summary>
    Mcbbs,

    /// <summary>通用整合包 / 懒人包（直接是 .minecraft 目录或平铺结构）。</summary>
    Generic,
}

/// <summary>
/// 整合包安装服务（学习自 PCL2 / Modrinth App 的安装流程）：
///  1. 检测压缩包格式（Modrinth / CurseForge / HMCL / MultiMC / MCBBS / 通用懒人包）；
///  2. Modrinth：解析 modrinth.index.json → 安装加载器版本（Fabric/Quilt 走 Meta API，
///     Forge/NeoForge 走官方安装器）→ 并行下载依赖文件（SHA1 校验）→ 复制 overrides；
///  3. 其他格式：由调用方按"通用合并进 .minecraft"处理（懒人包通常自带版本目录）。
/// </summary>
public class PackInstallService
{
    public static PackInstallService Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    // =====================================================================
    // 对外入口
    // =====================================================================

    /// <summary>
    /// 尝试安装整合包。
    /// </summary>
    /// <param name="extractDir">已解压的整合包目录。</param>
    /// <param name="displayName">列表中的展示标题（用于生成版本名）。</param>
    /// <returns>安装生成的版本目录名；非 Modrinth 等需要联网安装的格式返回 null（调用方走通用合并）。</returns>
    public async Task<string?> TryInstallPackAsync(string extractDir, string displayName)
    {
        var format = DetectFormat(extractDir);
        LogService.Instance.Log($"识别到整合包格式：{FormatName(format)}");

        switch (format)
        {
            case PackFormat.Modrinth:
                return await InstallModrinthAsync(extractDir, displayName);
            case PackFormat.CurseForge:
                LogService.Instance.Log("提示：CurseForge 整合包的模组依赖需从 CurseForge 服务器下载，暂不支持自动获取；将按通用方式合并包内文件。");
                return null;
            case PackFormat.Hmcl:
            case PackFormat.MultiMc:
            case PackFormat.Mcbbs:
            case PackFormat.Generic:
            default:
                // 懒人包 / HMCL / MMC 通常自带完整版本目录，交给调用方通用合并
                return null;
        }
    }

    // =====================================================================
    // 格式检测
    // =====================================================================

    /// <summary>在解压目录上检测整合包格式（参考 PCL2 的签名检测顺序）。</summary>
    public PackFormat DetectFormat(string packDir)
    {
        var root = packDir;
        if (File.Exists(Path.Combine(root, "modrinth.index.json"))) return PackFormat.Modrinth;
        if (File.Exists(Path.Combine(root, "modpack.json"))) return PackFormat.Hmcl;
        if (File.Exists(Path.Combine(root, "mmc-pack.json")) || File.Exists(Path.Combine(root, "instance.cfg")))
            return PackFormat.MultiMc;
        if (File.Exists(Path.Combine(root, "mcbbs.packmeta"))) return PackFormat.Mcbbs;
        if (File.Exists(Path.Combine(root, "manifest.json")))
        {
            // CurseForge manifest.json 无 "addons" 字段；MCBBS 版 manifest 带 addons
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
                return doc.RootElement.TryGetProperty("addons", out _) ? PackFormat.Mcbbs : PackFormat.CurseForge;
            }
            catch
            {
                return PackFormat.CurseForge;
            }
        }

        // 通用：内含 .minecraft 目录，或直接是 versions/mods/config 平铺结构
        if (Directory.Exists(Path.Combine(root, ".minecraft"))) return PackFormat.Generic;
        if (Directory.Exists(Path.Combine(root, "versions"))
            || Directory.Exists(Path.Combine(root, "mods"))
            || Directory.Exists(Path.Combine(root, "config")))
        {
            return PackFormat.Generic;
        }

        return PackFormat.Unknown;
    }

    private static string FormatName(PackFormat format) => format switch
    {
        PackFormat.Modrinth => "Modrinth",
        PackFormat.CurseForge => "CurseForge",
        PackFormat.Hmcl => "HMCL",
        PackFormat.MultiMc => "MultiMC",
        PackFormat.Mcbbs => "MCBBS",
        PackFormat.Generic => "通用懒人包",
        _ => "未知格式",
    };

    // =====================================================================
    // Modrinth 安装
    // =====================================================================

    private async Task<string> InstallModrinthAsync(string packDir, string displayName)
    {
        var indexPath = Path.Combine(packDir, "modrinth.index.json");
        var index = JsonHelper.Deserialize<ModrinthIndex>(File.ReadAllText(indexPath))
            ?? throw new InvalidDataException("modrinth.index.json 解析失败");

        var deps = index.Dependencies;
        LogService.Instance.Log($"整合包名称：{index.Name}（Minecraft {deps.Minecraft ?? "未知"}）");

        var mcDir = ConfigService.Instance.MinecraftDirectory;
        Directory.CreateDirectory(mcDir);

        // ---- 1. 安装 Minecraft + 加载器，生成版本目录 ----
        var versionName = await EnsureLoaderVersionAsync(deps, displayName);

        // ---- 2. 并行下载依赖文件（mods / config / 资源包等，SHA1 校验） ----
        await DownloadIndexFilesAsync(index, mcDir);

        // ---- 3. 复制 overrides 与 client-overrides 到 .minecraft ----
        CopyOverrides(packDir, "overrides", mcDir);
        CopyOverrides(packDir, "client-overrides", mcDir);

        // ---- 4. 提取整合包自带图标（版本卡片 / 版本管理页显示） ----
        VersionIconService.TryCopyPackIcon(packDir, mcDir, versionName);

        LogService.Instance.Log("Modrinth 整合包安装完成。");
        WritePackMarker(mcDir, versionName);
        return versionName;
    }

    /// <summary>整合包安装成功标记文件名（"已安装"判定依据，避免残留目录误报）。</summary>
    public const string PackMarkerFileName = ".aclauncher_pack";

    /// <summary>在版本目录写入整合包安装标记（内容为整合包显示名）。</summary>
    public static void WritePackMarker(string mcDir, string versionName, string content = "")
    {
        try
        {
            var versionDir = Path.Combine(mcDir, "versions", versionName);
            if (!Directory.Exists(versionDir)) return;
            File.WriteAllText(Path.Combine(versionDir, PackMarkerFileName), content);
            LogService.Instance.Log($"已写入整合包安装标记：{versionName}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"写入整合包安装标记失败：{ex.Message}");
        }
    }

    /// <summary>版本目录是否带有整合包安装标记。</summary>
    public static bool HasPackMarker(string mcDir, string versionName)
    {
        if (string.IsNullOrEmpty(versionName)) return false;
        return File.Exists(Path.Combine(mcDir, "versions", versionName, PackMarkerFileName));
    }

    /// <summary>下载 modrinth.index.json 中声明的全部文件（并行、按用户线程数）。</summary>
    private async Task DownloadIndexFilesAsync(ModrinthIndex index, string mcDir)
    {
        var settings = SettingsService.Instance.Load();
        var tasks = new List<DownloadTask>();

        foreach (var file in index.Files)
        {
            // 防路径穿越：拒绝绝对路径与 .. 片段（Modrinth 官方文档的安全要求）
            var rel = file.Path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(rel) || rel.Split(Path.DirectorySeparatorChar).Contains(".."))
            {
                LogService.Instance.Log($"警告：跳过不安全的文件路径：{file.Path}");
                continue;
            }

            var local = Path.Combine(mcDir, rel);
            if (File.Exists(local) && !string.IsNullOrEmpty(file.Hashes?.Sha1) && DownloadService.VerifySha1(local, file.Hashes.Sha1))
            {
                continue; // 已存在且校验通过
            }

            tasks.Add(new DownloadTask
            {
                Urls = file.Downloads.Count > 0 ? file.Downloads : new List<string>(),
                LocalPath = local,
                ExpectedSha1 = file.Hashes?.Sha1,
                DisplayName = file.Path,
            });
        }

        if (tasks.Count == 0)
        {
            LogService.Instance.Log("依赖文件均已存在，无需下载。");
            return;
        }

        LogService.Instance.Log($"开始下载整合包依赖文件（{tasks.Count} 个，{settings.DownloadThreads} 线程）...");
        var failed = await DownloadService.Instance.DownloadManyAsync(tasks, settings.DownloadThreads,
            new Progress<(int done, int total, string current)>(p =>
            {
                if (p.done % 10 == 0 || p.done == p.total)
                {
                    LogService.Instance.Log($"整合包依赖下载：{p.done}/{p.total}  {p.current}");
                }
            }));

        if (failed.Count == 0) return;

        // 分级处理：材质包 / 光影包属于可选美化资源，下载失败不阻断安装（进游戏后可自行补下）；
        // 其余文件（mods、核心库、配置等）失败则判定为安装失败，避免缺模组导致游戏崩溃。
        bool IsOptional(string path) =>
            path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("screenshots/", StringComparison.OrdinalIgnoreCase);

        var optionalFailed = failed.Where(f => IsOptional(f.DisplayName)).ToList();
        var criticalFailed = failed.Where(f => !IsOptional(f.DisplayName)).ToList();

        foreach (var f in optionalFailed)
        {
            LogService.Instance.Log($"警告：可选资源下载失败，已跳过（不影响游戏启动）：{f.DisplayName}");
        }

        if (criticalFailed.Count > 0)
        {
            var names = string.Join("、", criticalFailed.Take(5).Select(f => f.DisplayName));
            throw new IOException($"有 {criticalFailed.Count} 个依赖文件下载失败（{names}{(criticalFailed.Count > 5 ? " 等" : "")}）。请检查网络后重试。");
        }
    }

    // =====================================================================
    // 加载器安装（生成 versions 目录下的可启动版本）
    // =====================================================================

    /// <summary>
    /// 根据依赖声明安装 Minecraft + 加载器，返回版本目录名。
    /// Fabric / Quilt：走官方 Meta API 获取完整版本 JSON（无需运行安装器）；
    /// Forge / NeoForge：下载官方 installer.jar 并用 Java 运行 --installClient（PCL2 同款做法）。
    /// </summary>
    private async Task<string> EnsureLoaderVersionAsync(ModrinthDependencies deps, string displayName)
    {
        var mcDir = ConfigService.Instance.MinecraftDirectory;
        var mc = string.IsNullOrWhiteSpace(deps.Minecraft) ? null : deps.Minecraft.Trim();

        // 各加载器优先级：NeoForge > Forge > Fabric > Quilt（一般只声明一个）
        if (!string.IsNullOrWhiteSpace(deps.NeoForge))
            return await InstallInstallerAsync(mc, deps.NeoForge.Trim(), "neoforge", displayName);
        if (!string.IsNullOrWhiteSpace(deps.Forge))
            return await InstallInstallerAsync(mc, deps.Forge.Trim(), "forge", displayName);
        if (!string.IsNullOrWhiteSpace(deps.FabricLoader))
            return await InstallMetaLoaderAsync(mc, deps.FabricLoader.Trim(), "fabric", displayName);
        if (!string.IsNullOrWhiteSpace(deps.QuiltLoader))
            return await InstallMetaLoaderAsync(mc, deps.QuiltLoader.Trim(), "quilt", displayName);

        // 没有加载器（纯原版 Modrinth 包）：尝试已有版本目录，否则下载官方版本
        if (!string.IsNullOrWhiteSpace(mc))
        {
            if (Directory.Exists(Path.Combine(mcDir, "versions", mc))) return mc;
            await InstallVanillaVersionAsync(mc);
            return mc;
        }

        throw new InvalidDataException("整合包未声明 Minecraft 版本，无法安装。");
    }

    /// <summary>
    /// Fabric / Quilt：先安装原版 Minecraft（父版本，提供核心 JAR / 原版库 / 资源），
    /// 再写入加载器 profile JSON（含 inheritsFrom）并下载加载器自身依赖库。
    /// 注意：Meta 返回的 profile 只含加载器库与 mainClass，libraries 用 "name"+"url" 坐标格式，
    /// 真正启动时由 VersionParserService 合并父版本。
    /// </summary>
    private async Task<string> InstallMetaLoaderAsync(string? mc, string loaderVer, string kind, string displayName)
    {
        if (string.IsNullOrWhiteSpace(mc))
            throw new InvalidDataException($"{kind} 整合包缺少 Minecraft 版本声明。");

        var name = $"{mc}-{kind}-{loaderVer}";
        var mcDir = ConfigService.Instance.MinecraftDirectory;
        var versionDir = Path.Combine(mcDir, "versions", name);
        var profileJsonPath = Path.Combine(versionDir, name + ".json");

        // 1) 先安装原版（父版本）——必须先有，否则合并 inheritsFrom 会失败
        LogService.Instance.Log($"先安装原版 Minecraft {mc}（加载器基础版本）...");
        await InstallVanillaVersionAsync(mc);

        // 2) 获取加载器 profile（已存在则复用，避免重复联网）
        if (!File.Exists(profileJsonPath))
        {
            Directory.CreateDirectory(versionDir);

            // profile 地址：BMCLAPI 镜像优先（国内访问快），失败回退官方 Meta
            var metaUrls = kind == "quilt"
                ? new List<string>
                {
                    $"https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader/{mc}/{loaderVer}/profile/json",
                    $"https://meta.quiltmc.org/v3/versions/loader/{mc}/{loaderVer}/profile/json",
                }
                : new List<string>
                {
                    $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{mc}/{loaderVer}/profile/json",
                    $"https://meta.fabricmc.net/v2/versions/loader/{mc}/{loaderVer}/profile/json",
                };
            LogService.Instance.Log($"正在获取 {kind} 加载器配置：{metaUrls[0]}");

            string json;
            try
            {
                json = await VanillaSourceService.GetStringWithFallbackAsync(metaUrls);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"获取 {kind} 加载器配置失败：{ex.Message}", ex);
            }
            var versionJson = JsonHelper.Deserialize<VersionJson>(json)
                ?? throw new InvalidDataException($"{kind} 加载器配置解析失败。");

            // 显式写入继承关系（个别 Meta 响应缺省，兜底补上），确保父版本指向原版
            if (string.IsNullOrWhiteSpace(versionJson.InheritsFrom))
            {
                versionJson.InheritsFrom = mc;
                json = JsonHelper.Serialize(versionJson);
            }
            File.WriteAllText(profileJsonPath, json);
        }
        else
        {
            LogService.Instance.Log($"加载器配置已存在：{name}");
        }

        var profile = JsonHelper.Deserialize<VersionJson>(File.ReadAllText(profileJsonPath))
            ?? throw new InvalidDataException($"{kind} 加载器配置解析失败。");

        // 3) 下载加载器依赖库（Fabric/Quilt 的 libraries 只有 name+url，需从 Maven 坐标推导路径）
        var libTasks = new List<DownloadTask>();
        if (profile.Libraries is not null)
        {
            foreach (var lib in profile.Libraries)
            {
                if (!VersionParserService.RulesMatch(lib.Rules)) continue;

                // 优先 downloads.artifact.path，否则从 Maven 坐标（name）推导
                var relPath = lib.GetPath();
                if (string.IsNullOrWhiteSpace(relPath)) continue;

                var fullPath = Path.Combine(mcDir, "libraries", relPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath)) continue;

                var urls = new List<string>();
                if (!string.IsNullOrWhiteSpace(lib.Downloads?.Artifact?.Url))
                {
                    urls.Add(lib.Downloads!.Artifact!.Url!);
                }
                if (!string.IsNullOrWhiteSpace(lib.Url))
                {
                    urls.Add(lib.Url!.TrimEnd('/') + "/" + relPath);
                }
                if (kind == "quilt")
                {
                    urls.Add("https://maven.quiltmc.org/repository/release/" + relPath);
                }
                else
                {
                    urls.Add("https://maven.fabricmc.net/" + relPath);
                }
                urls.Add("https://bmclapi2.bangbang93.com/maven/" + relPath); // BMCLAPI 兜底

                libTasks.Add(new DownloadTask
                {
                    Urls = urls.Distinct().ToList(),
                    LocalPath = fullPath,
                    ExpectedSha1 = lib.Downloads?.Artifact?.Sha1,
                    DisplayName = relPath,
                });
            }
        }
        if (libTasks.Count > 0)
        {
            LogService.Instance.Log($"正在下载 {kind} 加载器依赖库（{libTasks.Count} 个）...");
            var failed = await DownloadService.Instance.DownloadManyAsync(libTasks, 8, null);
            if (failed.Count > 0)
            {
                var names = string.Join("、", failed.Take(3).Select(f => f.DisplayName));
                throw new IOException($"加载器依赖库下载失败（{names}{(failed.Count > 3 ? " 等" : "")}）。");
            }
        }

        await CompleteAndVerifyAsync(name);
        LogService.Instance.Log($"{kind} 加载器版本已生成：{name}");
        return name;
    }

    /// <summary>Forge / NeoForge：下载官方 installer.jar 并用 Java 运行（生成完整版本目录）。</summary>
    private async Task<string> InstallInstallerAsync(string? mc, string loaderVer, string kind, string displayName)
    {
        if (string.IsNullOrWhiteSpace(mc))
            throw new InvalidDataException($"{kind} 整合包缺少 Minecraft 版本声明。");

        var name = kind == "forge" ? $"{mc}-forge-{loaderVer}" : $"{mc}-neoforge-{loaderVer}";
        var mcDir = ConfigService.Instance.MinecraftDirectory;
        if (Directory.Exists(Path.Combine(mcDir, "versions", name)))
        {
            LogService.Instance.Log($"加载器版本已存在：{name}，检查并补全文件...");
            await CompleteAndVerifyAsync(name);
            return name;
        }

        var installerUrl = kind == "forge"
            ? $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{loaderVer}/forge-{mc}-{loaderVer}-installer.jar"
            : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVer}/neoforge-{loaderVer}-installer.jar";

        var installerPath = Path.Combine(Path.GetTempPath(), $"acl_{kind}_{loaderVer}_{Guid.NewGuid():N}.jar");
        LogService.Instance.Log($"正在下载 {kind} 安装器：{loaderVer}...");
        var ok = await DownloadService.Instance.DownloadOneAsync(new DownloadTask
        {
            Urls = new List<string> { installerUrl, "https://bmclapi2.bangbang93.com/maven/" + installerUrl[(installerUrl.IndexOf("/net/", StringComparison.Ordinal) + 1)..] },
            LocalPath = installerPath,
            DisplayName = Path.GetFileName(installerUrl),
        });
        if (!ok)
        {
            try { File.Delete(installerPath); } catch { }
            throw new IOException($"{kind} 安装器下载失败。");
        }

        // 需要 Java 运行安装器
        var javaPath = await ResolveJavaPathAsync();
        LogService.Instance.Log($"正在运行 {kind} 安装器（{javaPath}），请稍候...");
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = mcDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add("--installDir");
        psi.ArgumentList.Add(mcDir);

        using var process = Process.Start(psi);
        if (process is null)
        {
            try { File.Delete(installerPath); } catch { }
            throw new IOException("无法启动 Java 运行安装器。");
        }
        var output = await process.StandardOutput.ReadToEndAsync();
        var err = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        try { File.Delete(installerPath); } catch { }

        if (process.ExitCode != 0)
        {
            LogService.Instance.Log(output.Trim());
            LogService.Instance.Log(err.Trim());
            throw new IOException($"{kind} 安装器运行失败（退出码 {process.ExitCode}）。请检查 Java 版本与网络后重试。");
        }

        if (!Directory.Exists(Path.Combine(mcDir, "versions", name)))
        {
            // 部分安装器版本命名不同，扫描 versions 目录找新生成的
            var versionsDir = Path.Combine(mcDir, "versions");
            var newest = Directory.Exists(versionsDir)
                ? Directory.GetDirectories(versionsDir).Select(Path.GetFileName).OrderByDescending(x => x).FirstOrDefault()
                : null;
            if (newest is not null)
            {
                await CompleteAndVerifyAsync(newest);
                return newest!;
            }
            throw new IOException($"{kind} 安装完成但未找到版本目录。");
        }

        await CompleteAndVerifyAsync(name);
        LogService.Instance.Log($"{kind} 加载器版本已生成：{name}");
        return name;
    }

    /// <summary>
    /// 纯原版安装：从 Mojang 官方 / BMCLAPI 清单下载版本 JSON、游戏 JAR 与依赖库（无加载器）。幂等：已安装则跳过。
    /// 版本下载页"官方版本"条目直接使用。
    /// </summary>
    /// <param name="mc">版本号。</param>
    /// <param name="onStatus">阶段状态回调（供下载对话框显示当前步骤）。</param>
    /// <param name="downloadProgress">字节级进度回调（核心 JAR 下载用）。</param>
    public async Task InstallVanillaVersionAsync(string mc, Action<string>? onStatus = null,
        IProgress<DownloadProgress>? downloadProgress = null)
    {
        var mcDir = ConfigService.Instance.MinecraftDirectory;
        var versionDir = Path.Combine(mcDir, "versions", mc);

        Directory.CreateDirectory(versionDir);

        var jsonPath = Path.Combine(versionDir, mc + ".json");
        var jarPath = Path.Combine(versionDir, mc + ".jar");

        // 已安装过也继续往下走：安装流程负责「补全 + 校验」，重复安装时应把缺失文件补齐
        var corePresent = File.Exists(jsonPath) && File.Exists(jarPath);
        if (corePresent)
        {
            LogService.Instance.Log($"原版 {mc} 核心文件已存在，跳过重复下载，直接补全其余文件。");
            onStatus?.Invoke("版本已存在，正在补全游戏文件...");
        }
        else
        {
            // 原版下载源（网络设置可选 Mojang 官方 / BMCLAPI 镜像），清单与版本 JSON 均带超时与候选回退
            var source = SettingsService.Instance.Load().VanillaDownloadSource;
            if (source != VanillaSourceService.SourceBmclapi && source != VanillaSourceService.SourceMojang)
            {
                source = VanillaSourceService.SourceBmclapi;
            }

            LogService.Instance.Log($"正在下载原版 {mc} 版本信息（源：{(source == VanillaSourceService.SourceBmclapi ? "BMCLAPI 镜像" : "Mojang 官方")}）...");
            onStatus?.Invoke($"正在获取版本清单（{(source == VanillaSourceService.SourceBmclapi ? "BMCLAPI 镜像" : "Mojang 官方")}）...");
            var manifest = await VanillaSourceService.GetStringWithFallbackAsync(
                VanillaSourceService.GetManifestUrls(source));
            var versionUrl = VanillaSourceService.FindVersionUrl(manifest, mc)
                ?? throw new InvalidDataException($"未找到 Minecraft 版本 {mc}。");

            onStatus?.Invoke("正在获取版本信息...");
            var json = await VanillaSourceService.GetStringWithFallbackAsync(
                VanillaSourceService.GetVersionJsonUrls(source, versionUrl));
            File.WriteAllText(jsonPath, json);
            var versionJson = JsonHelper.Deserialize<VersionJson>(json)
                ?? throw new InvalidDataException("原版版本信息解析失败。");

            // 下载核心 JAR（按所选源排候选顺序）
            var clientUrl = versionJson.Downloads?.Client?.Url;
            if (!string.IsNullOrWhiteSpace(clientUrl))
            {
                LogService.Instance.Log($"正在下载原版 {mc} 游戏核心 JAR...");
                var jarSizeMb = (versionJson.Downloads?.Client?.Size ?? 0) / 1024.0 / 1024.0;
                onStatus?.Invoke(jarSizeMb > 0
                    ? $"正在下载游戏核心 JAR（约 {jarSizeMb:F0} MB）..."
                    : "正在下载游戏核心 JAR...");
                var jarUrls = source == VanillaSourceService.SourceBmclapi
                    ? new List<string>
                    {
                        clientUrl.Replace("https://piston-data.mojang.com", VanillaSourceService.BmclapiMetaBase),
                        clientUrl,
                    }
                    : new List<string>
                    {
                        clientUrl,
                        clientUrl.Replace("https://piston-data.mojang.com", VanillaSourceService.BmclapiMetaBase),
                    };
                var ok = await DownloadService.Instance.DownloadOneAsync(new DownloadTask
                {
                    Urls = jarUrls,
                    LocalPath = jarPath,
                    ExpectedSha1 = versionJson.Downloads?.Client?.Sha1,
                    DisplayName = mc + ".jar",
                }, downloadProgress);
                if (!ok) throw new IOException($"游戏核心 JAR 下载失败：{mc}.jar");
            }
        }

        // 安装阶段一次性补齐：依赖库（含 natives）、资源索引、全部资源对象，最后校验完整性
        await CompleteAndVerifyAsync(mc, onStatus);
        onStatus?.Invoke("校验完成，正在设为当前版本...");
        LogService.Instance.Log($"原版 {mc} 安装完成，完整性校验通过。");
    }

    /// <summary>
    /// 补全指定版本所需的全部文件并校验完整性。
    /// 安装阶段（原版 / 加载器 / 整合包）统一走这里，避免「装完启动时才临时补文件」。
    /// </summary>
    private async Task CompleteAndVerifyAsync(string versionId, Action<string>? onStatus = null)
    {
        var mcDir = ConfigService.Instance.MinecraftDirectory;

        var merged = await VersionParserService.Instance.ParseAsync(versionId);
        onStatus?.Invoke("正在检查需要补全的游戏文件...");
        var missing = await ResourceService.CollectMissingAsync(merged, mcDir, onStatus);
        if (missing.Count > 0)
        {
            LogService.Instance.Log($"版本 {versionId} 需要补全 {missing.Count} 个文件。");
            onStatus?.Invoke($"正在下载游戏文件（共 {missing.Count} 个）...");
            var failed = await DownloadService.Instance.DownloadManyAsync(missing, 8,
                new Progress<(int done, int total, string current)>(p =>
                    onStatus?.Invoke($"正在下载：{p.current}（{p.done}/{p.total}）")));
            if (failed.Count > 0)
            {
                var names = string.Join("、", failed.Take(5).Select(f => f.DisplayName));
                throw new IOException($"游戏文件下载失败（{failed.Count} 个）：{names}{(failed.Count > 5 ? " 等" : "")}");
            }
        }

        onStatus?.Invoke("正在校验文件完整性...");
        var (ok, total, broken) = await ResourceService.VerifyAsync(merged, mcDir);
        if (!ok)
        {
            throw new IOException(
                $"文件完整性校验未通过（缺失 {broken.Count}/{total} 个）：{string.Join("、", broken.Take(5))}");
        }

        LogService.Instance.Log($"版本 {versionId} 完整性校验通过（共 {total} 个文件）。");
    }

    /// <summary>
    /// 单独安装模组加载器（供下载页"游戏"安装后询问添加加载器使用）。
    /// kind: fabric / quilt / forge / neoforge；返回生成的版本目录名。
    /// </summary>
    public async Task<string> InstallModLoaderAsync(string mc, string kind, string loaderVer)
    {
        var deps = new ModrinthDependencies
        {
            Minecraft = mc,
            FabricLoader = kind == "fabric" ? loaderVer : null,
            QuiltLoader = kind == "quilt" ? loaderVer : null,
            Forge = kind == "forge" ? loaderVer : null,
            NeoForge = kind == "neoforge" ? loaderVer : null,
        };
        return await EnsureLoaderVersionAsync(deps, $"原版 {mc} + {kind}");
    }

    /// <summary>解析可用 Java 路径（手动配置优先，其次自动检测）。</summary>
    private async Task<string> ResolveJavaPathAsync()
    {
        var saved = SettingsService.Instance.Load().JavaPath;
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return saved;

        var list = await JavaService.Instance.DetectAsync();
        var first = list.FirstOrDefault(j => !string.IsNullOrWhiteSpace(j.Path));
        if (first is not null) return first.Path;

        throw new FileNotFoundException("未找到可用的 Java。请在“设置”中手动指定 java.exe / javaw.exe 路径。");
    }

    // =====================================================================
    // overrides 复制
    // =====================================================================

    /// <summary>把整合包内的 overrides 目录内容复制合并到 .minecraft（防路径穿越）。</summary>
    private void CopyOverrides(string packDir, string folderName, string mcDir)
    {
        var src = Path.Combine(packDir, folderName);
        if (!Directory.Exists(src)) return;

        LogService.Instance.Log($"正在复制 {folderName} ...");
        CopyDirectorySafe(src, mcDir);
    }

    /// <summary>递归复制目录内容到目标（自动创建目录，覆盖同名文件）。</summary>
    private static void CopyDirectorySafe(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name is "." or "..") continue;
            CopyDirectorySafe(dir, Path.Combine(destDir, name));
        }
    }
}
