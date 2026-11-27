namespace ACLauncher.Helpers;

/// <summary>
/// Maven 坐标工具：将库的 Maven 坐标转换为 .minecraft/libraries/ 下的相对路径。
/// </summary>
public static class MavenHelper
{
    /// <summary>
    /// 将 Maven 坐标转换为相对路径（正斜杠），例如：
    /// "com.mojang:patchy:1.20.4" → "com/mojang/patchy/1.20.4/patchy-1.20.4.jar"
    /// "org.lwjgl:lwjgl:3.3.3" + "natives-windows" → "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar"
    /// </summary>
    /// <param name="name">Maven 坐标，格式 group:artifact:version[:classifier][@extension]。</param>
    /// <param name="classifier">额外指定的 classifier（Native 库场景）。</param>
    public static string NameToPath(string name, string? classifier = null)
    {
        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            // 格式异常时原样返回，避免崩溃
            return name;
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];

        // name 中自带 classifier（第 4 段），且未显式指定时使用
        var cls = classifier;
        if (string.IsNullOrEmpty(cls) && parts.Length >= 4 && !parts[3].StartsWith('@'))
        {
            cls = parts[3];
        }

        var fileName = $"{artifact}-{version}{(string.IsNullOrEmpty(cls) ? "" : "-" + cls)}.jar";
        return $"{group}/{artifact}/{version}/{fileName}";
    }
}
