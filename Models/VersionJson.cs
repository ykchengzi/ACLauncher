using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACLauncher.Models;

/// <summary>
/// 版本 JSON（versions/&lt;版本&gt;/&lt;版本&gt;.json）数据模型。
/// 兼容新旧两种结构：
///  - 新版本（约 1.13+）：使用 arguments.jvm / arguments.game 模板；
///  - 旧版本（约 1.12.2 及以前）：没有 arguments 字段，使用 minecraftArguments 字符串。
/// </summary>
public class VersionJson
{
    /// <summary>版本 ID，例如 "1.20.4"。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// 继承自的原版版本号（Fabric / Quilt / Forge 等加载器版本 JSON 含此字段，例如 "1.20.1"）。
    /// 加载器版本只声明自己的库、主类与参数，其余（原版库、参数、资源、核心 JAR）需从父版本合并。
    /// </summary>
    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    /// <summary>游戏主类名，例如 "net.minecraft.client.main.Main"。</summary>
    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    /// <summary>依赖库列表。</summary>
    [JsonPropertyName("libraries")]
    public List<MinecraftLibrary>? Libraries { get; set; }

    /// <summary>新版本参数模板。</summary>
    [JsonPropertyName("arguments")]
    public Arguments? Arguments { get; set; }

    /// <summary>旧版本（1.12.2 及以前）的单条参数字符串。</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    /// <summary>资源索引名（assets 字段，1.7.2+ 使用），例如 "1.20"。</summary>
    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    /// <summary>资源索引对象（1.7.2+ 使用），其中 id 与 assets 字段通常一致。</summary>
    [JsonPropertyName("assetIndex")]
    public AssetIndex? AssetIndex { get; set; }

    /// <summary>版本下载信息（客户端 jar 等，用于缺失时自动补全）。</summary>
    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    /// <summary>核心 JAR 文件名（无扩展名），缺省时等于版本 ID。</summary>
    [JsonPropertyName("jar")]
    public string? Jar { get; set; }

    /// <summary>该版本要求的 Java 运行时（如 26.2 要求 Java 25 / java-runtime-epsilon）。旧版本可能缺省。</summary>
    [JsonPropertyName("javaVersion")]
    public JavaVersionInfo? JavaVersion { get; set; }

    // ===== 运行时由 Parser 填入，不在 JSON 中 =====

    /// <summary>实际使用的 JSON 文件完整路径。</summary>
    [JsonIgnore]
    public string? ResolvedJsonPath { get; set; }

    /// <summary>版本目录完整路径。</summary>
    [JsonIgnore]
    public string? ResolvedVersionDir { get; set; }

    /// <summary>资源索引名：优先 assetIndex.id，其次 assets 字段。</summary>
    public string GetAssetIndexName()
        => !string.IsNullOrWhiteSpace(AssetIndex?.Id)
            ? AssetIndex!.Id!
            : (!string.IsNullOrWhiteSpace(Assets) ? Assets! : Id ?? "legacy");

    /// <summary>
    /// 获取核心 JAR 文件完整路径。
    /// 查找顺序：
    ///  1. 当前版本目录按 jar / id 字段名找；
    ///  2. 当前版本目录里最大的 .jar；
    ///  3. 继承版本（inheritsFrom，如 Fabric/Quilt）的父版本目录 versions/{jar}/{jar}.jar；
    ///  4. 父版本目录里最大的 .jar。
    /// </summary>
    public string GetMainJarPath()
    {
        var dir = ResolvedVersionDir ?? "";
        var preferredName = string.IsNullOrWhiteSpace(Jar) ? Id : Jar;

        // 1. 当前版本目录按 jar / id 字段找
        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var preferred = Path.Combine(dir, preferredName + ".jar");
            if (File.Exists(preferred)) return preferred;
        }

        // 2. 当前版本目录里最大的 .jar
        if (Directory.Exists(dir))
        {
            var jars = Directory.GetFiles(dir, "*.jar", SearchOption.TopDirectoryOnly);
            if (jars.Length > 0)
            {
                return jars.OrderByDescending(f => new FileInfo(f).Length).First();
            }
        }

        // 3/4. 继承版本：父版本目录（versions/{jar}/）
        if (!string.IsNullOrWhiteSpace(preferredName) && !string.IsNullOrEmpty(dir))
        {
            var versionsRoot = Directory.GetParent(dir)?.FullName;
            if (!string.IsNullOrEmpty(versionsRoot))
            {
                var parentDir = Path.Combine(versionsRoot, preferredName!);
                var parentPreferred = Path.Combine(parentDir, preferredName! + ".jar");
                if (File.Exists(parentPreferred)) return parentPreferred;

                if (Directory.Exists(parentDir))
                {
                    var parentJars = Directory.GetFiles(parentDir, "*.jar", SearchOption.TopDirectoryOnly);
                    if (parentJars.Length > 0)
                    {
                        return parentJars.OrderByDescending(f => new FileInfo(f).Length).First();
                    }
                }
            }
        }

        throw new FileNotFoundException($"版本目录下未找到核心 JAR 文件：{dir}（继承自 {preferredName}）");
    }
}

/// <summary>
/// 参数模板（arguments.jvm / arguments.game）。
/// 每一项可能是字符串，也可能是带规则的条件对象，因此用 JsonElement 原始解析。
/// </summary>
public class Arguments
{
    [JsonPropertyName("game")]
    public List<JsonElement>? Game { get; set; }

    [JsonPropertyName("jvm")]
    public List<JsonElement>? Jvm { get; set; }

    /// <summary>
    /// 新版本（26.2 起）默认用户 JVM 参数：内存、GC、CompactObjectHeaders 等。
    /// 仅当用户未自定义内存 / JVM 参数时作为默认值应用。
    /// </summary>
    [JsonPropertyName("default-user-jvm")]
    public List<JsonElement>? DefaultUserJvm { get; set; }
}

/// <summary>
/// 版本要求的 Java 运行时信息。
/// </summary>
public class JavaVersionInfo
{
    /// <summary>Mojang 运行时组件名，如 java-runtime-epsilon。</summary>
    [JsonPropertyName("component")]
    public string? Component { get; set; }

    /// <summary>要求的 Java 主版本号，如 25。</summary>
    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

/// <summary>
/// 资源索引对象。
/// </summary>
public class AssetIndex
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>资源索引 JSON 下载地址。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>SHA1 校验值。</summary>
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    /// <summary>文件大小（字节）。</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// 版本下载信息（客户端 jar / 服务端 / 资源索引等）。
/// </summary>
public class VersionDownloads
{
    /// <summary>客户端 jar 下载信息。</summary>
    [JsonPropertyName("client")]
    public ClientDownload? Client { get; set; }
}

/// <summary>
/// 客户端 jar 下载信息。
/// </summary>
public class ClientDownload
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
