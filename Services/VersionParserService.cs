using System.Text.Json;
using ACLauncher.Helpers;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>
/// 版本 JSON 解析服务。
/// 定位版本目录下的 JSON 文件并解析，兼容新旧版本 JSON 结构差异。
/// </summary>
public class VersionParserService
{
    public static VersionParserService Instance { get; } = new();

    /// <summary>
    /// 定位并解析指定版本的版本 JSON。
    /// 优先找 &lt;version&gt;.json；找不到则自动扫描版本目录下任意 .json 文件。
    /// </summary>
    /// <param name="version">版本号（对应 versions/ 下的文件夹名）。</param>
    /// <exception cref="FileNotFoundException">版本文件不存在。</exception>
    /// <exception cref="FormatException">版本 JSON 格式错误或缺少关键字段。</exception>
    public async Task<VersionJson> ParseAsync(string version)
    {
        return await LoadMergedAsync(version, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 递归加载并合并版本 JSON（处理 Fabric / Quilt / Forge 等加载器的 inheritsFrom 继承）。
    /// </summary>
    /// <param name="visited">已处理过的版本链，防止循环继承。</param>
    private async Task<VersionJson> LoadMergedAsync(string version, HashSet<string> visited)
    {
        if (!visited.Add(version))
        {
            throw new FormatException($"版本继承出现循环：{string.Join(" → ", visited)}");
        }

        var child = await LoadRawAsync(version);

        // 无继承：直接返回该版本
        if (string.IsNullOrWhiteSpace(child.InheritsFrom) ||
            string.Equals(child.InheritsFrom, version, StringComparison.OrdinalIgnoreCase))
        {
            return child;
        }

        // 递归加载父版本（原版），缺失时给出明确提示
        var parentName = child.InheritsFrom!.Trim();
        var minecraftDir = ConfigService.Instance.MinecraftDirectory;
        var parentDir = Path.Combine(minecraftDir, "versions", parentName);
        if (!Directory.Exists(parentDir))
        {
            throw new FileNotFoundException(
                $"版本 \"{version}\" 依赖基础版本 \"{parentName}\"，但本地未安装。\n请重新安装该整合包，或先安装原版 {parentName}。\n缺失目录：{parentDir}");
        }

        LogService.Instance.Log($"版本 \"{version}\" 继承自 \"{parentName}\"，正在合并版本信息...");
        var parent = await LoadMergedAsync(parentName, visited);
        MergeVersion(parent, child);
        return child;
    }

    /// <summary>
    /// 把父版本（原版）的内容合并进子版本（加载器）。
    /// 规则（与官方启动器 / HMCL / Prism 一致）：
    ///  - 库：子版本优先（同名坐标保留子版本），且子版本排在 classpath 前面以覆盖父版本；
    ///  - JVM / 游戏参数：父在前、子在后（追加）；
    ///  - 主类、资源索引、下载信息、核心 JAR 名等：子版本缺失则继承父版本。
    /// </summary>
    private static void MergeVersion(VersionJson parent, VersionJson child)
    {
        // ---- 合并依赖库：子在前、父在后，同名坐标保留子 ----
        if (parent.Libraries is { Count: > 0 })
        {
            var merged = new List<MinecraftLibrary>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddLibs(IEnumerable<MinecraftLibrary> libs)
            {
                foreach (var lib in libs)
                {
                    var key = !string.IsNullOrWhiteSpace(lib.Name)
                        ? lib.Name!
                        : (lib.Downloads?.Artifact?.Path ?? Guid.NewGuid().ToString("N"));
                    if (seen.Add(key)) merged.Add(lib);
                }
            }

            if (child.Libraries is { Count: > 0 }) AddLibs(child.Libraries); // 子优先
            AddLibs(parent.Libraries);
            child.Libraries = merged;
        }

        // ---- 合并 JVM / 游戏参数模板：父在前、子在后 ----
        MergeArguments(parent.Arguments, child.Arguments);

        // ---- 标量字段：子缺失则继承父 ----
        if (string.IsNullOrWhiteSpace(child.MainClass)) child.MainClass = parent.MainClass;
        if (string.IsNullOrWhiteSpace(child.MinecraftArguments)) child.MinecraftArguments = parent.MinecraftArguments;
        if (string.IsNullOrWhiteSpace(child.Assets)) child.Assets = parent.Assets;
        if (child.AssetIndex is null) child.AssetIndex = parent.AssetIndex;
        if (child.Downloads is null) child.Downloads = parent.Downloads;
        if (child.JavaVersion is null) child.JavaVersion = parent.JavaVersion;
        if (string.IsNullOrWhiteSpace(child.Jar)) child.Jar = string.IsNullOrWhiteSpace(parent.Jar) ? parent.Id : parent.Jar;
    }

    /// <summary>合并新旧两种参数结构（arguments.jvm / arguments.game，均为 JsonElement 列表）。</summary>
    private static void MergeArguments(Arguments? parent, Arguments? child)
    {
        if (parent is null) return;
        child ??= new Arguments();

        if (parent.Jvm is { Count: > 0 })
        {
            child.Jvm ??= new List<JsonElement>();
            child.Jvm = parent.Jvm.Concat(child.Jvm).ToList();
        }
        if (parent.Game is { Count: > 0 })
        {
            child.Game ??= new List<JsonElement>();
            child.Game = parent.Game.Concat(child.Game).ToList();
        }
        if (parent.DefaultUserJvm is { Count: > 0 })
        {
            child.DefaultUserJvm ??= new List<JsonElement>();
            child.DefaultUserJvm = parent.DefaultUserJvm.Concat(child.DefaultUserJvm).ToList();
        }
    }

    /// <summary>从磁盘加载单个版本 JSON（不做继承合并）。</summary>
    private async Task<VersionJson> LoadRawAsync(string version)
    {
        var minecraftDir = ConfigService.Instance.MinecraftDirectory;
        if (!Directory.Exists(minecraftDir))
        {
            throw new DirectoryNotFoundException($"未找到 .minecraft 目录：{minecraftDir}");
        }

        var versionDir = Path.Combine(minecraftDir, "versions", version);
        if (!Directory.Exists(versionDir))
        {
            throw new DirectoryNotFoundException($"未找到版本目录：{versionDir}");
        }

        // 优先找 <version>.json，找不到就扫描目录下任意 .json
        var jsonPath = Path.Combine(versionDir, $"{version}.json");
        if (!File.Exists(jsonPath))
        {
            var candidates = Directory.GetFiles(versionDir, "*.json", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 0)
            {
                throw new FileNotFoundException($"版本目录下未找到任何 JSON 文件：{versionDir}");
            }

            // 逐个尝试解析，选能成功读出 mainClass 或 inheritsFrom 的那个（排除 usercache.json 等无关文件）
            jsonPath = "";
            foreach (var candidate in candidates)
            {
                try
                {
                    var quick = await JsonHelper.DeserializeFromFileAsync<VersionJson>(candidate);
                    if (quick is not null &&
                        (!string.IsNullOrWhiteSpace(quick.MainClass) || !string.IsNullOrWhiteSpace(quick.InheritsFrom)))
                    {
                        jsonPath = candidate;
                        LogService.Instance.Log($"未找到 {version}.json，自动识别版本文件：{Path.GetFileName(candidate)}");
                        break;
                    }
                }
                catch { /* 不是版本 JSON，跳过 */ }
            }

            if (string.IsNullOrEmpty(jsonPath))
            {
                throw new FileNotFoundException($"版本目录下未找到有效的版本 JSON（含 mainClass 字段）：{versionDir}");
            }
        }

        VersionJson? versionJson;
        try
        {
            versionJson = await JsonHelper.DeserializeFromFileAsync<VersionJson>(jsonPath);
        }
        catch (Exception ex)
        {
            throw new FormatException($"版本文件解析失败：{jsonPath}\n{ex.Message}", ex);
        }

        if (versionJson is null)
        {
            throw new FormatException($"版本文件内容为空：{jsonPath}");
        }

        if (string.IsNullOrWhiteSpace(versionJson.MainClass) && string.IsNullOrWhiteSpace(versionJson.InheritsFrom))
        {
            throw new FormatException($"版本文件缺少 mainClass 字段：{jsonPath}");
        }

        // 记录实际使用的 JSON 路径和 JAR 路径，供 GameLaunchService 使用
        versionJson.ResolvedJsonPath = jsonPath;
        versionJson.ResolvedVersionDir = versionDir;

        return versionJson;
    }

    /// <summary>
    /// 解析参数模板中的一项（字符串或带规则的对象），返回符合当前系统条件的参数值列表。
    /// </summary>
    public static ArgumentItem ParseArgumentItem(JsonElement element)
    {
        var item = new ArgumentItem();

        if (element.ValueKind == JsonValueKind.String)
        {
            item.Values.Add(element.GetString() ?? "");
            return item;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return item;
        }

        if (element.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var ruleElement in rulesElement.EnumerateArray())
            {
                var rule = JsonSerializer.Deserialize<LibraryRule>(ruleElement.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (rule is not null)
                {
                    item.Rules.Add(rule);
                }
            }
        }

        if (element.TryGetProperty("value", out var valueElement))
        {
            if (valueElement.ValueKind == JsonValueKind.String)
            {
                item.Values.Add(valueElement.GetString() ?? "");
            }
            else if (valueElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in valueElement.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        item.Values.Add(v.GetString() ?? "");
                    }
                }
            }
        }

        return item;
    }

    /// <summary>
    /// 判断一组规则是否允许当前系统使用该库/参数。
    /// Mojang 规则语义：初始不允许；按顺序遍历，每匹配一条规则就更新允许状态；
    /// 最终状态即结果。rules 为 null 或空数组时默认允许（兼容未声明规则的库）。
    /// </summary>
    public static bool RulesMatch(IEnumerable<LibraryRule>? rules)
    {
        if (rules is null) return true;

        var allowed = false;
        var any = false;
        foreach (var rule in rules)
        {
            if (rule.IsMatch())
            {
                any = true;
                allowed = rule.Action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true;
            }
        }

        // 有规则但一条都没匹配到 → 不允许
        return any && allowed;
    }
}
