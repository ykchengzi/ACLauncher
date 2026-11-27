using System.Text.Json.Serialization;

namespace ACLauncher.Models;

/// <summary>
/// config.json 配置模型。
/// 配置文件位于启动器可执行文件同目录，仅锁定游戏版本。
/// </summary>
public class Config
{
    /// <summary>锁定的游戏版本号，例如 "1.20.4"。</summary>
    [JsonPropertyName("game_version")]
    public string? GameVersion { get; set; }
}
