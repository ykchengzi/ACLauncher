using System.Diagnostics;

namespace ACLauncher.Models;

/// <summary>
/// 游戏进程优先级设置（映射到 System.Diagnostics.ProcessPriorityClass）。
/// </summary>
public static class GameProcessPriority
{
    /// <summary>可选优先级（显示名）。</summary>
    public static readonly string[] Options =
    {
        "最低", "低", "普通", "高", "最高",
    };

    /// <summary>
    /// 将显示名转换为 Windows 进程优先级类。
    /// </summary>
    public static ProcessPriorityClass ToPriorityClass(string? name) => name switch
    {
        "最低" => ProcessPriorityClass.Idle,
        "低" => ProcessPriorityClass.BelowNormal,
        "高" => ProcessPriorityClass.AboveNormal,
        "最高" => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };
}
