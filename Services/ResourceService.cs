using System.Text.Json;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>
/// 游戏文件补全与完整性校验：
///  - 安装版本时一次性补齐核心 JAR、依赖库（含 natives）、资源索引与全部资源对象；
///  - 下载完成后校验所有必需文件是否存在（资源对象按大小校验）。
/// 与启动前的自动补全共用同一套规则，避免「装完能进游戏、但某些资源缺失」。
/// </summary>
public static class ResourceService
{
    /// <summary>收集缺失文件（不会下载，便于先展示总量）。资源索引缺失时会先补下载索引。</summary>
    public static async Task<List<DownloadTask>> CollectMissingAsync(VersionJson versionJson,
        string minecraftDir, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var tasks = new List<DownloadTask>();

        // ---- 1. 核心 JAR ----
        string? mainJar = null;
        try { mainJar = versionJson.GetMainJarPath(); }
        catch (FileNotFoundException) { /* 目录里没有 jar，稍后按 downloads.client 补下载 */ }

        if ((mainJar is null || !File.Exists(mainJar)) && versionJson.Downloads?.Client?.Url is { Length: > 0 } jarUrl)
        {
            var jarName = (string.IsNullOrWhiteSpace(versionJson.Jar) ? versionJson.Id : versionJson.Jar) + ".jar";
            var target = Path.Combine(versionJson.ResolvedVersionDir ?? "", jarName);
            tasks.Add(new DownloadTask
            {
                Urls = new List<string>
                {
                    jarUrl,
                    jarUrl.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com"),
                },
                LocalPath = target,
                ExpectedSha1 = versionJson.Downloads.Client.Sha1,
                DisplayName = $"核心 JAR（{jarName}）",
            });
        }

        // ---- 2. 依赖库（普通库 + natives） ----
        if (versionJson.Libraries is not null)
        {
            foreach (var lib in versionJson.Libraries)
            {
                if (!VersionParserService.RulesMatch(lib.Rules)) continue;

                // 旧版 JSON 的 classifier 里可能带 ${arch} 占位（如 natives-windows-${arch}），x64 下替换为 64
                var classifiers = new List<string>();
                if (lib.Natives is not null && lib.Natives.TryGetValue("windows", out var rawCls))
                {
                    classifiers.Add(rawCls.Replace("${arch}", "64"));
                }
                // 没有 natives 字段、但带 windows 分类器的旧格式（1.7.10 等）
                var classifierMap = lib.Downloads?.Classifiers;
                if (classifierMap is not null)
                {
                    if (classifierMap.ContainsKey("natives-windows-64")) classifiers.Add("natives-windows-64");
                    else if (classifierMap.ContainsKey("natives-windows")) classifiers.Add("natives-windows");
                    else if (classifierMap.ContainsKey("natives-windows-32")) classifiers.Add("natives-windows-32");
                }
                // 只有分类器、没有主 jar 的库（lwjgl-platform / jinput-platform / twitch-platform 等）
                var nativeOnly = lib.Downloads?.Artifact is null &&
                                 classifierMap is { Count: > 0 };

                if (IsNativeLibrary(lib) && !nativeOnly)
                {
                    classifiers.Add(""); // 新格式：natives 就是普通 artifact
                }

                foreach (var classifier in classifiers.Distinct())
                {
                    var relPath = classifier.Length == 0 ? lib.GetPath() : lib.GetPath(classifier);
                    if (string.IsNullOrEmpty(relPath)) continue;

                    var fullPath = Path.Combine(minecraftDir, "libraries",
                        relPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullPath)) continue;

                    var sha1 = classifier.Length == 0
                        ? lib.Downloads?.Artifact?.Sha1
                        : (classifierMap is not null && classifierMap.TryGetValue(classifier, out var nat)
                            ? nat?.Sha1
                            : null);

                    tasks.Add(new DownloadTask
                    {
                        Urls = BuildLibraryUrls(lib, relPath, classifier.Length == 0 ? null : classifier),
                        LocalPath = fullPath,
                        ExpectedSha1 = sha1,
                        DisplayName = relPath,
                    });
                }

                // 普通库（非 native）。只有分类器文件的库不能按 Maven 坐标硬拼主 jar 地址。
                var mainPath = nativeOnly ? null : lib.GetPath();
                if (!string.IsNullOrEmpty(mainPath))
                {
                    var mainFull = Path.Combine(minecraftDir, "libraries",
                        mainPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(mainFull))
                    {
                        tasks.Add(new DownloadTask
                        {
                            Urls = BuildLibraryUrls(lib, mainPath, null),
                            LocalPath = mainFull,
                            ExpectedSha1 = lib.Downloads?.Artifact?.Sha1,
                            DisplayName = mainPath,
                        });
                    }
                }
            }
        }

        // ---- 3. 资源索引（必须先落盘才能枚举资源对象） ----
        var assetsIndexPath = Path.Combine(minecraftDir, "assets", "indexes",
            versionJson.GetAssetIndexName() + ".json");
        var indexUrl = versionJson.AssetIndex?.Url;
        if (!File.Exists(assetsIndexPath) && !string.IsNullOrWhiteSpace(indexUrl))
        {
            onStatus?.Invoke($"正在下载资源索引（{versionJson.GetAssetIndexName()}）...");
            var indexTask = new DownloadTask
            {
                Urls = new List<string>
                {
                    indexUrl,
                    indexUrl.Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com"),
                },
                LocalPath = assetsIndexPath,
                ExpectedSha1 = versionJson.AssetIndex?.Sha1,
                DisplayName = $"资源索引（{versionJson.GetAssetIndexName()}）",
            };
            var ok = await DownloadService.Instance.DownloadOneAsync(indexTask, cancellationToken: ct);
            if (!ok) throw new IOException($"资源索引下载失败：{versionJson.GetAssetIndexName()}。");
        }

        // ---- 4. 资源对象 ----
        tasks.AddRange(await CollectMissingAssetsAsync(assetsIndexPath, minecraftDir));
        return tasks;
    }

    /// <summary>校验必需文件是否齐全（核心 JAR / 依赖库 / 资源索引 / 全部资源对象）。</summary>
    public static async Task<(bool Ok, int Total, List<string> Missing)> VerifyAsync(VersionJson versionJson,
        string minecraftDir)
    {
        var missing = new List<string>();
        var total = 0;

        // 核心 JAR
        total++;
        string? jar = null;
        try { jar = versionJson.GetMainJarPath(); } catch { /* 忽略 */ }
        if (jar is null || !File.Exists(jar)) missing.Add("核心 JAR");

        // 依赖库 + natives
        if (versionJson.Libraries is not null)
        {
            foreach (var lib in versionJson.Libraries)
            {
                if (!VersionParserService.RulesMatch(lib.Rules)) continue;

                var paths = new List<string>();
                if (lib.Natives is not null && lib.Natives.TryGetValue("windows", out var rawCls))
                {
                    var p = lib.GetPath(rawCls.Replace("${arch}", "64"));
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
                // 只有分类器文件、没有主 jar 的旧格式 native 库：与下载逻辑保持一致，只取一个 windows 分类器
                if (lib.Downloads?.Classifiers is { Count: > 0 } cmap)
                {
                    var key = cmap.ContainsKey("natives-windows-64") ? "natives-windows-64"
                        : cmap.ContainsKey("natives-windows") ? "natives-windows"
                        : cmap.ContainsKey("natives-windows-32") ? "natives-windows-32"
                        : null;
                    if (key is not null)
                    {
                        var p = lib.GetPath(key);
                        if (!string.IsNullOrEmpty(p)) paths.Add(p);
                    }
                }
                var nativeOnly = lib.Downloads?.Artifact is null && lib.Downloads?.Classifiers is { Count: > 0 };
                var mainPath = nativeOnly ? null : lib.GetPath();
                if (!string.IsNullOrEmpty(mainPath)) paths.Add(mainPath);

                foreach (var relPath in paths.Distinct())
                {
                    total++;
                    var fullPath = Path.Combine(minecraftDir, "libraries",
                        relPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(fullPath)) missing.Add(relPath);
                }
            }
        }

        // 资源索引 + 资源对象
        var assetsIndexPath = Path.Combine(minecraftDir, "assets", "indexes",
            versionJson.GetAssetIndexName() + ".json");
        total++;
        if (!File.Exists(assetsIndexPath))
        {
            missing.Add($"资源索引 {versionJson.GetAssetIndexName()}");
        }
        else
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(File.OpenRead(assetsIndexPath));
                if (doc.RootElement.TryGetProperty("objects", out var objects))
                {
                    foreach (var obj in objects.EnumerateObject())
                    {
                        if (!obj.Value.TryGetProperty("hash", out var hashEl)) continue;
                        var hash = hashEl.GetString();
                        if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

                        long size = 0;
                        if (obj.Value.TryGetProperty("size", out var sizeEl)) size = sizeEl.GetInt64();

                        total++;
                        var local = Path.Combine(minecraftDir, "assets", "objects", hash[..2], hash);
                        if (!File.Exists(local)) { missing.Add("资源 " + obj.Name); continue; }
                        if (size > 0 && new FileInfo(local).Length != size) missing.Add("资源 " + obj.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Log($"校验资源索引失败：{ex.Message}");
            }
        }

        return (missing.Count == 0, total, missing);
    }

    // =====================================================================
    // 内部辅助
    // =====================================================================

    private static List<string> BuildLibraryUrls(MinecraftLibrary lib, string relPath, string? classifier)
    {
        var urls = new List<string>();

        // native 分类器优先用 downloads.classifiers 里的地址（普通 artifact 地址指向的是主 jar）
        if (classifier is not null &&
            lib.Downloads?.Classifiers is { } classifiers &&
            classifiers.TryGetValue(classifier, out var native) &&
            !string.IsNullOrWhiteSpace(native?.Url))
        {
            urls.Add(native!.Url!);
        }
        else if (!string.IsNullOrWhiteSpace(lib.Downloads?.Artifact?.Url))
        {
            urls.Add(lib.Downloads.Artifact.Url);
        }
        else if (!string.IsNullOrWhiteSpace(lib.Url))
        {
            urls.Add(lib.Url.TrimEnd('/') + "/" + relPath);
        }
        else if (lib.Name?.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) == true)
        {
            urls.Add("https://maven.fabricmc.net/" + relPath);
        }
        else
        {
            urls.Add("https://libraries.minecraft.net/" + relPath);
        }
        urls.Add("https://bmclapi2.bangbang93.com/maven/" + relPath);
        return urls.Distinct().ToList();
    }

    private static bool IsNativeLibrary(MinecraftLibrary lib)
    {
        if (lib.Natives is not null && lib.Natives.ContainsKey("windows")) return true;
        var name = lib.Name ?? "";
        if (name.Contains(":natives-windows", StringComparison.OrdinalIgnoreCase)) return true;
        var path = lib.Downloads?.Artifact?.Path ?? "";
        return path.Contains("natives-windows", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<DownloadTask>> CollectMissingAssetsAsync(string indexPath, string minecraftDir)
    {
        var result = new List<DownloadTask>();
        if (!File.Exists(indexPath)) return result;

        try
        {
            using var doc = await JsonDocument.ParseAsync(File.OpenRead(indexPath));
            if (!doc.RootElement.TryGetProperty("objects", out var objects)) return result;

            foreach (var obj in objects.EnumerateObject())
            {
                if (!obj.Value.TryGetProperty("hash", out var hashEl)) continue;
                var hash = hashEl.GetString();
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;

                long size = 0;
                if (obj.Value.TryGetProperty("size", out var sizeEl)) size = sizeEl.GetInt64();

                var local = Path.Combine(minecraftDir, "assets", "objects", hash[..2], hash);
                if (File.Exists(local))
                {
                    if (size == 0 || new FileInfo(local).Length == size) continue;
                }

                result.Add(new DownloadTask
                {
                    Urls = new List<string>
                    {
                        $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                        $"https://bmclapi2.bangbang93.com/assets/{hash[..2]}/{hash}",
                    },
                    LocalPath = local,
                    ExpectedSha1 = hash,
                    DisplayName = obj.Name,
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"解析资源索引失败：{ex.Message}");
        }

        return result;
    }
}
