namespace ACLauncher.Services;

/// <summary>
/// 启动次数统计：每次游戏成功启动后累加，并在特定次数触发“考虑购买正版”的提醒。
/// 次数保存在 settings.json，与其它设置一起持久化。
/// </summary>
public static class LaunchCounterService
{
    /// <summary>触发提醒的次数节点。</summary>
    private static readonly int[] Milestones = { 5, 10, 15, 50, 100, 500, 1000 };

    /// <summary>累计启动次数达到该值后，提醒文案换成更强的一版。</summary>
    public const int StrongToneFrom = 100;

    /// <summary>记录一次成功启动，返回最新累计次数与命中的提醒节点（未命中为 null）。</summary>
    public static (int Count, int? Milestone) RegisterLaunch()
    {
        var settings = SettingsService.Instance.Load();
        settings.LaunchCount = Math.Max(0, settings.LaunchCount) + 1;
        SettingsService.Instance.Save(settings);

        var hit = Array.IndexOf(Milestones, settings.LaunchCount);
        return (settings.LaunchCount, hit >= 0 ? settings.LaunchCount : null);
    }
}
