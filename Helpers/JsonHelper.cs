using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACLauncher.Helpers;

/// <summary>
/// JSON 序列化 / 反序列化工具。
/// </summary>
public static class JsonHelper
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // 属性名大小写不敏感，兼容不同版本 JSON 的字段书写差异
        PropertyNameCaseInsensitive = true,
        // 输出美化，便于人工检查配置文件
        WriteIndented = true,
        // 空值不写出
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>反序列化 JSON 字符串。</summary>
    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>序列化对象为 JSON 字符串。</summary>
    public static string Serialize<T>(T obj)
        => JsonSerializer.Serialize(obj, Options);

    /// <summary>从文件读取并反序列化。</summary>
    public static async Task<T?> DeserializeFromFileAsync<T>(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return Deserialize<T>(json);
    }

    /// <summary>序列化并写入文件。</summary>
    public static async Task SerializeToFileAsync<T>(string path, T obj)
        => await File.WriteAllTextAsync(path, Serialize(obj));
}
