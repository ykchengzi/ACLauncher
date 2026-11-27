using System.Text;

namespace ACLauncher.Helpers;

/// <summary>
/// 命令行字符串工具。
/// </summary>
public static class CommandLineHelper
{
    /// <summary>
    /// 将一行命令字符串按空格拆分为多个参数（支持双引号包裹的含空格参数）。
    /// 用于解析旧版本（1.12.2 及以前）的 minecraftArguments。
    /// </summary>
    public static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
