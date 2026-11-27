using System.Text.Json;
using System.Text.Json.Serialization;
using ACLauncher.Helpers;

namespace ACLauncher.Models;

/// <summary>
/// 依赖库模型（版本 JSON 中的 libraries 项）。
/// </summary>
public class MinecraftLibrary
{
    /// <summary>Maven 坐标，例如 "com.mojang:patchy:1.20.4"。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>操作系统规则列表（可选）。为空表示对所有系统适用。</summary>
    [JsonPropertyName("rules")]
    public List<LibraryRule>? Rules { get; set; }

    /// <summary>Native 分类器映射，例如 { "windows": "natives-windows" }。</summary>
    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    /// <summary>下载信息（新版本 JSON 提供 artifact 路径；旧版本没有该字段，需从 name 推导）。</summary>
    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    /// <summary>仓库基址（顶层 url 字段，例如 "https://libraries.minecraft.net/" 或 "https://maven.fabricmc.net/"）。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 计算该库在 .minecraft/libraries/ 下的相对路径（正斜杠分隔）。
    /// </summary>
    public string? GetPath() => GetPath(null);

    /// <summary>
    /// 计算带 classifier 的库相对路径（用于 Native 库）。
    /// </summary>
    /// <param name="classifier">classifier 后缀，例如 "natives-windows"。</param>
    public string? GetPath(string? classifier)
    {
        // 优先使用 downloads 中提供的明确路径
        if (classifier is null && Downloads?.Artifact?.Path is { Length: > 0 } p)
        {
            return p;
        }
        if (classifier is not null && Downloads?.Classifiers is { } classifiers
            && classifiers.TryGetValue(classifier, out var artifact)
            && artifact?.Path is { Length: > 0 } cp)
        {
            return cp;
        }

        // 否则从 Maven 坐标推导路径
        if (string.IsNullOrWhiteSpace(Name))
        {
            return null;
        }
        return MavenHelper.NameToPath(Name!, classifier);
    }
}

/// <summary>
/// 依赖库的下载信息。
/// </summary>
public class LibraryDownloads
{
    /// <summary>主构件（普通 JAR）。</summary>
    [JsonPropertyName("artifact")]
    public LibraryArtifact? Artifact { get; set; }

    /// <summary>分类构件（例如 natives-windows 对应的 JAR）。</summary>
    [JsonPropertyName("classifiers")]
    public Dictionary<string, LibraryArtifact>? Classifiers { get; set; }
}

/// <summary>
/// 单个文件构件。
/// </summary>
public class LibraryArtifact
{
    /// <summary>相对于 .minecraft/libraries/ 的路径。</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>下载地址（完整 URL）。</summary>
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
/// 规则条目（库或参数的条件）。
/// </summary>
public class LibraryRule
{
    /// <summary>动作："allow"（允许）或 "disallow"（禁止）。</summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    /// <summary>适用的操作系统条件（可选）。</summary>
    [JsonPropertyName("os")]
    public LibraryOs? Os { get; set; }

    /// <summary>
    /// 特性开关条件（arguments.game 中的 demo / 自定义分辨率 / 快速游戏等）。
    /// 启动器默认不启用任何特性，因此含 features 的规则一律不匹配。
    /// </summary>
    [JsonPropertyName("features")]
    public Dictionary<string, JsonElement>? Features { get; set; }

    /// <summary>是否匹配当前操作系统（按最后一条匹配的规则决定）。</summary>
    public bool IsMatch()
    {
        // 依赖特性开关的规则（--demo、--width/--height、--quickPlay* 等）：默认不启用
        if (Features is { Count: > 0 })
        {
            return false;
        }

        if (Os is null)
        {
            // 没有 os 条件的规则匹配所有平台
            return true;
        }

        // 系统名：windows / osx / linux
        if (!string.IsNullOrEmpty(Os.Name) && !string.Equals(Os.Name, OsInfo.CurrentOsName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 架构：x86 / x64 / arm64 / arm
        if (!string.IsNullOrEmpty(Os.Arch) && !string.Equals(Os.Arch, OsInfo.CurrentArch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 系统版本（目前仅 Windows 使用，例如 "10.0.22621" 等）
        if (!string.IsNullOrEmpty(Os.Version) && !OsInfo.MatchesWindowsVersion(Os.Version))
        {
            return false;
        }

        // 系统版本范围（26.2 起 default-user-jvm 使用，如 versionRange: {min:"10.0.17134"}）
        if (Os.VersionRange is not null &&
            !OsInfo.MatchesWindowsVersionRange(Os.VersionRange.Min, Os.VersionRange.Max))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// 规则中的操作系统条件。
/// </summary>
public class LibraryOs
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>系统版本范围（26.2 起，按 Windows 内部版本号比较）。</summary>
    [JsonPropertyName("versionRange")]
    public OsVersionRange? VersionRange { get; set; }
}

/// <summary>
/// 操作系统版本范围（min / max 均为包含边界，如 "10.0.17134"）。
/// </summary>
public class OsVersionRange
{
    [JsonPropertyName("min")]
    public string? Min { get; set; }

    [JsonPropertyName("max")]
    public string? Max { get; set; }
}

/// <summary>
/// 参数模板中的单个条件参数项。
/// 由 JsonElement 解析而来：值为字符串或字符串数组，可带 rules。
/// </summary>
public class ArgumentItem
{
    /// <summary>适用的规则列表（为空表示无条件）。</summary>
    public List<LibraryRule> Rules { get; set; } = new();

    /// <summary>参数值（一个或多个）。</summary>
    public List<string> Values { get; set; } = new();

    /// <summary>是否有 rules 条件。</summary>
    public bool HasRules => Rules.Count > 0;
}
