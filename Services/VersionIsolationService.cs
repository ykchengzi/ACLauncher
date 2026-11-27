namespace ACLauncher.Services;

/// <summary>版本隔离的全局默认策略。</summary>
public enum IsolationMode
{
    /// <summary>所有版本共用 .minecraft。</summary>
    Off,

    /// <summary>带模组加载器的版本自动隔离，原版共用（默认）。</summary>
    Smart,

    /// <summary>所有版本都使用 versions/&lt;版本&gt; 作为游戏目录。</summary>
    All,
}

/// <summary>
/// 版本隔离：决定某个版本启动时的游戏目录（mods / saves / config / logs / 截图等都在这个目录下）。
/// 参考主流启动器的做法：
///   - 全局只有一个默认策略（关闭 / 智能 / 全部隔离），默认「智能」——带加载器的版本自动隔离；
///   - 单个版本可以在「版本管理 → 管理」里覆盖为「隔离」或「不隔离」；
///   - libraries / assets / versions 目录始终共享，隔离只影响游戏工作目录，不会重复占用磁盘。
/// </summary>
public static class VersionIsolationService
{
    /// <summary>按版本覆盖的值：隔离。</summary>
    public const string OverrideOn = "on";

    /// <summary>按版本覆盖的值：不隔离。</summary>
    public const string OverrideOff = "off";

    /// <summary>解析全局策略文本（未知值按智能处理）。</summary>
    public static IsolationMode ParseMode(string? raw) => raw switch
    {
        "off" => IsolationMode.Off,
        "all" => IsolationMode.All,
        "smart" => IsolationMode.Smart,
        _ => IsolationMode.Smart,
    };

    /// <summary>全局策略的可选值（界面下拉用）。</summary>
    public static IReadOnlyList<string> ModeLabels { get; } = new[] { "智能（推荐）", "全部隔离", "关闭" };

    /// <summary>界面文本 → 策略值。</summary>
    public static string ModeFromLabel(string label) => label switch
    {
        "全部隔离" => "all",
        "关闭" => "off",
        _ => "smart",
    };

    /// <summary>策略值 → 界面文本。</summary>
    public static string LabelFromMode(string? mode) => ParseMode(mode) switch
    {
        IsolationMode.All => "全部隔离",
        IsolationMode.Off => "关闭",
        _ => "智能（推荐）",
    };

    /// <summary>策略枚举 → 存储值。</summary>
    public static string ToValue(IsolationMode mode) => mode switch
    {
        IsolationMode.All => "all",
        IsolationMode.Off => "off",
        _ => "smart",
    };

    /// <summary>读取全局默认策略（旧版布尔开关会自动迁移）。</summary>
    public static IsolationMode GetGlobalMode()
    {
        var settings = SettingsService.Instance.Load();
        if (!string.IsNullOrWhiteSpace(settings.VersionIsolationMode))
        {
            return ParseMode(settings.VersionIsolationMode);
        }

        // 旧配置迁移：
        //   true  → 全部隔离
        //   false → 关闭
        //   未设置但已有配置文件（老用户升级）→ 保持原来的「共用」行为，避免模组 / 存档突然“消失”
        //   未设置且没有配置文件（全新安装）→ 默认「智能」
        return settings.VersionIsolationLegacy switch
        {
            true => IsolationMode.All,
            false => IsolationMode.Off,
            null => SettingsService.Instance.LastLoadHadFile ? IsolationMode.Off : IsolationMode.Smart,
        };
    }

    /// <summary>某个版本的隔离覆盖值：null 表示跟随全局策略。</summary>
    public static string? GetOverride(string versionName)
    {
        var map = SettingsService.Instance.Load().VersionIsolationOverrides;
        if (map is null) return null;

        // 反序列化出来的字典不保证大小写不敏感，这里手动比较
        foreach (var pair in map)
        {
            if (string.Equals(pair.Key, versionName, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }
        return null;
    }

    /// <summary>设置某个版本的隔离覆盖值（null 表示恢复跟随全局）。</summary>
    public static void SetOverride(string versionName, string? value)
    {
        var settings = SettingsService.Instance.Load();
        settings.VersionIsolationOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var existing = settings.VersionIsolationOverrides.Keys
            .FirstOrDefault(k => string.Equals(k, versionName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) settings.VersionIsolationOverrides.Remove(existing);

        if (!string.IsNullOrEmpty(value))
        {
            settings.VersionIsolationOverrides[versionName] = value;
        }

        SettingsService.Instance.Save(settings);
        LogService.Instance.Log($"版本隔离：{versionName} 设为 {(value is null ? "跟随全局" : value == OverrideOn ? "隔离" : "不隔离")}");
    }

    /// <summary>该版本是否启用隔离（覆盖优先，其次全局策略）。</summary>
    public static bool IsIsolated(string versionName)
    {
        var over = GetOverride(versionName);
        if (over == OverrideOn) return true;
        if (over == OverrideOff) return false;

        return GetGlobalMode() switch
        {
            IsolationMode.All => true,
            IsolationMode.Off => false,
            // 智能：带模组加载器的版本自动隔离
            _ => !string.Equals(ConfigService.Instance.GetVersionLoaderLabel(versionName), "原版", StringComparison.Ordinal),
        };
    }

    /// <summary>该版本启动时使用的游戏目录。</summary>
    public static string GetGameDirectory(string versionName) =>
        IsIsolated(versionName)
            ? Path.Combine(ConfigService.Instance.VersionsDirectory, versionName)
            : ConfigService.Instance.MinecraftDirectory;

    /// <summary>该版本实际使用的 mods 目录。</summary>
    public static string GetModsDirectory(string versionName) =>
        Path.Combine(GetGameDirectory(versionName), "mods");

    /// <summary>该版本实际使用的存档目录。</summary>
    public static string GetSavesDirectory(string versionName) =>
        Path.Combine(GetGameDirectory(versionName), "saves");
}
