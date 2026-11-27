using System.Text.Json.Serialization;

namespace ACLauncher.Models;

/// <summary>
/// 启动器内部设置（Java 路径、内存、自定义参数、主题）。
/// 持久化到启动器目录下的 settings.json。
/// </summary>
public class AppSettings
{
    /// <summary>手动配置的 Java 可执行文件路径（优先于自动检测）。</summary>
    public string? JavaPath { get; set; }

    /// <summary>JVM 最小内存（MB）。</summary>
    public double MinMemoryMB { get; set; } = 1024;

    /// <summary>JVM 最大内存（MB）。</summary>
    public double MaxMemoryMB { get; set; } = 4096;

    /// <summary>是否自动分配内存（开启时忽略上面的手动值，按物理内存自动计算）。</summary>
    public bool AutoMemory { get; set; }

    /// <summary>
    /// 自动分配的最大内存（MB）：物理内存的一半，限制在 2GB - 16GB，按 256MB 取整。
    /// 物理内存用 GC.GetGCMemoryInfo().TotalAvailableMemoryBytes 读取（容器感知）。
    /// </summary>
    [JsonIgnore]
    public double AutoMaxMemoryMB
    {
        get
        {
            try
            {
                var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0;
                if (totalMb <= 0) return 4096;
                var half = Math.Round(totalMb / 2.0 / 256.0) * 256.0;
                return Math.Clamp(half, 2048, 16384);
            }
            catch
            {
                return 4096;
            }
        }
    }

    /// <summary>实际生效的最大内存（自动模式取自动值）。</summary>
    [JsonIgnore]
    public double EffectiveMaxMemoryMB => AutoMemory ? AutoMaxMemoryMB : MaxMemoryMB;

    /// <summary>实际生效的最小内存（自动模式固定 1024）。</summary>
    [JsonIgnore]
    public double EffectiveMinMemoryMB => AutoMemory ? Math.Min(1024, AutoMaxMemoryMB) : MinMemoryMB;

    /// <summary>额外 JVM 参数（空格分隔）。</summary>
    public string ExtraJvmArgs { get; set; } = "";

    /// <summary>额外游戏参数（空格分隔）。</summary>
    public string ExtraGameArgs { get; set; } = "";

    /// <summary>主题："亮色" / "暗色" / "跟随系统"。</summary>
    public string Theme { get; set; } = "跟随系统";

    // ===== 网络与下载设置 =====

    /// <summary>下载线程数量（1 - 16）。</summary>
    public int DownloadThreads { get; set; } = 4;

    /// <summary>
    /// GitHub 加速域名（用户在网络设置中选择）。
    /// 空字符串表示 GitHub 直连；否则为选中的 gh-proxy 镜像地址（如 "https://ghproxy.net/"）。
    /// </summary>
    public string DownloadSource { get; set; } = "";

    /// <summary>
    /// 原版 Minecraft 下载源（用户在网络设置中选择）：
    /// "bmclapi" = BMCLAPI 镜像（默认，国内访问快）；"mojang" = Mojang 官方。
    /// 清单 / 版本 JSON 优先走所选源，另一源自动兜底。
    /// </summary>
    public string VanillaDownloadSource { get; set; } = "bmclapi";

    /// <summary>
    /// 版本下载地址（GitHub Release 的 .zip 完整地址，例如
    /// "https://github.com/user/repo/releases/download/v1.0.0/file.zip"）。
    /// 支持多个地址，用分号或换行分隔；下载时自动使用"加速域名 + 原地址"的方式。
    /// 未配置时版本下载页显示占位版本。
    /// </summary>
    public string VersionDownloadUrls { get; set; } = "";

    /// <summary>7-Zip 可执行文件路径（手动指定，可选；未指定时自动检测）。</summary>
    public string? SevenZipPath { get; set; }

    /// <summary>启动前是否检查并补全缺失的库 / 资源文件。</summary>
    public bool CheckResources { get; set; } = true;

    /// <summary>
    /// 版本隔离：开启后每个版本使用独立的游戏目录（versions/&lt;版本&gt;），
    /// 服务器列表、世界、截图、资源包等互不互通。默认关闭（共用 .minecraft）。
    /// </summary>
    public bool VersionIsolation { get; set; }

    /// <summary>启动后自动加入橙猫生存服务器（默认开启）。</summary>
    public bool AutoJoinServer { get; set; } = true;

    // ===== 游戏进程设置 =====

    /// <summary>游戏进程优先级："普通" / "高" / "低" / "最高" / "最低"。</summary>
    public string GamePriority { get; set; } = "普通";

    /// <summary>当前生效的 Java 路径（手动配置优先）。</summary>
    [JsonIgnore]
    public string? EffectiveJavaPath =>
        !string.IsNullOrWhiteSpace(JavaPath) && File.Exists(JavaPath) ? JavaPath : null;
}
