using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACLauncher.Models;

/// <summary>
/// Modrinth 整合包清单（zip 根目录的 modrinth.index.json）。
/// 格式说明：https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack
/// </summary>
public class ModrinthIndex
{
    /// <summary>清单格式版本（当前为 1）。</summary>
    public int FormatVersion { get; set; }

    /// <summary>游戏类型（固定为 minecraft）。</summary>
    public string Game { get; set; } = "";

    /// <summary>该整合包版本的唯一 ID。</summary>
    public string VersionId { get; set; } = "";

    /// <summary>整合包名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>简短描述（可选）。</summary>
    public string? Summary { get; set; }

    /// <summary>需要联网下载的文件列表。</summary>
    public List<ModrinthFile> Files { get; set; } = new();

    /// <summary>游戏与加载器依赖。</summary>
    public ModrinthDependencies Dependencies { get; set; } = new();
}

/// <summary>清单中的一个待下载文件。</summary>
public class ModrinthFile
{
    /// <summary>目标相对路径（相对 .minecraft，如 mods/MyMod.jar）。</summary>
    public string Path { get; set; } = "";

    /// <summary>文件哈希（必须含 SHA1 与 SHA512）。</summary>
    public ModrinthHashes Hashes { get; set; } = new();

    /// <summary>可用的下载地址列表（优先第一个，失败自动切换）。</summary>
    public List<string> Downloads { get; set; } = new();

    /// <summary>文件大小（字节，用于进度展示）。</summary>
    public long FileSize { get; set; }
}

/// <summary>文件哈希。</summary>
public class ModrinthHashes
{
    public string? Sha1 { get; set; }
    public string? Sha512 { get; set; }
}

/// <summary>依赖声明（哪个字段存在就按哪个安装）。</summary>
public class ModrinthDependencies
{
    /// <summary>Minecraft 版本，如 1.20.1。</summary>
    [JsonPropertyName("minecraft")]
    public string? Minecraft { get; set; }

    /// <summary>Forge 加载器版本。</summary>
    [JsonPropertyName("forge")]
    public string? Forge { get; set; }

    /// <summary>NeoForge 加载器版本。</summary>
    [JsonPropertyName("neoforge")]
    public string? NeoForge { get; set; }

    /// <summary>Fabric Loader 版本。</summary>
    [JsonPropertyName("fabric-loader")]
    public string? FabricLoader { get; set; }

    /// <summary>Quilt Loader 版本。</summary>
    [JsonPropertyName("quilt-loader")]
    public string? QuiltLoader { get; set; }
}
