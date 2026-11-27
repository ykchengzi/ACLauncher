using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ACLauncher.Helpers;

/// <summary>
/// 操作系统信息：用于版本 JSON 中 rules / natives 的匹配。
/// </summary>
public static class OsInfo
{
    /// <summary>当前系统名（版本 JSON 约定）：windows / osx / linux。</summary>
    public static string CurrentOsName { get; } = DetectOsName();

    /// <summary>当前处理器架构：x86 / x64 / arm64 / arm。</summary>
    public static string CurrentArch { get; } = DetectArch();

    private static string DetectOsName()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "osx";
        return "linux";
    }

    private static string DetectArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => "unknown",
    };

    /// <summary>
    /// 判断 Windows 版本号是否匹配规则中的正则表达式（例如 "10.0.22621"）。
    /// 仅 Windows 下有意义。
    /// </summary>
    public static bool MatchesWindowsVersion(string pattern)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var v = Environment.OSVersion.Version;
        var versionString = $"{v.Major}.{v.Minor}.{v.Build}";
        try
        {
            return Regex.IsMatch(versionString, pattern);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断当前 Windows 版本是否落在给定版本范围内（min / max 均为包含边界）。
    /// 输入形如 "10.0.17134"，按数值段比较；缺失段补 0；无法解析的边界忽略。
    /// </summary>
    public static bool MatchesWindowsVersionRange(string? min, string? max)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var current = Environment.OSVersion.Version;

        if (!string.IsNullOrWhiteSpace(min) && TryParseVersion(min!, out var lo) && current < lo)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(max) && TryParseVersion(max!, out var hi) && current > hi)
        {
            return false;
        }
        return true;
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        var parts = text.Split('.');
        var nums = new int[4];
        for (var i = 0; i < 4; i++)
        {
            nums[i] = i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
        }
        try
        {
            version = new Version(nums[0], nums[1], nums[2], nums[3]);
            return true;
        }
        catch
        {
            version = new Version(0, 0, 0, 0);
            return false;
        }
    }
}
