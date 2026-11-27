using System.Text.Json;

namespace ACLauncher.Services;

/// <summary>版本的渠道类型（决定版本卡片上的图标与标签）。</summary>
public enum VersionChannel
{
    /// <summary>正式版（Mojang type = release）。</summary>
    Release,

    /// <summary>测试版（快照 snapshot）。</summary>
    Snapshot,

    /// <summary>远古版（old_beta / old_alpha）。</summary>
    Ancient,

    /// <summary>版本文件缺失或损坏，无法识别。</summary>
    Broken,
}

/// <summary>
/// 版本渠道识别：把版本归为正式版 / 测试版 / 远古版 / 版本异常四类，
/// 并给出对应的方块图标文件名（草方块 / 命令方块 / 圆石 / 红石块）。
/// </summary>
public static class VersionChannelService
{
    /// <summary>按 Mojang 版本清单的 type 字段分类。</summary>
    public static VersionChannel FromMojangType(string? type) => type switch
    {
        "snapshot" => VersionChannel.Snapshot,
        "old_beta" or "old_alpha" => VersionChannel.Ancient,
        _ => VersionChannel.Release,
    };

    /// <summary>
    /// 识别本地已安装版本的渠道。
    /// 版本目录或版本 JSON 缺失 / 无法解析时判为「版本异常」（红石块）。
    /// </summary>
    public static VersionChannel DetectInstalled(string versionId, string versionsDirectory)
    {
        try
        {
            var dir = Path.Combine(versionsDirectory, versionId);
            if (!Directory.Exists(dir)) return VersionChannel.Broken;

            var jsonPath = Path.Combine(dir, versionId + ".json");
            if (!File.Exists(jsonPath)) return VersionChannel.Broken;

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return VersionChannel.Broken;

            // 官方版本 JSON 带 type 字段，直接据此分类
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                return FromMojangType(type.GetString());
            }

            // 加载器生成的版本 JSON 通常没有 type，但一定有 id 或 inheritsFrom
            var hasId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String;
            var hasInherits = root.TryGetProperty("inheritsFrom", out var inh) && inh.ValueKind == JsonValueKind.String;
            return hasId || hasInherits ? VersionChannel.Release : VersionChannel.Broken;
        }
        catch
        {
            return VersionChannel.Broken;
        }
    }

    /// <summary>渠道的中文标签。</summary>
    public static string Label(VersionChannel channel) => channel switch
    {
        VersionChannel.Snapshot => "测试版",
        VersionChannel.Ancient => "远古版",
        VersionChannel.Broken => "版本异常",
        _ => "正式版",
    };

    /// <summary>渠道对应的方块图标文件名（位于程序目录 Blocks\ 下）。</summary>
    public static string IconFile(VersionChannel channel) => channel switch
    {
        VersionChannel.Snapshot => "command_block.png",
        VersionChannel.Ancient => "cobblestone.png",
        VersionChannel.Broken => "redstone_block.png",
        _ => "grass_block.png",
    };
}
