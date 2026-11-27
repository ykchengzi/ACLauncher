using System.Text.Json;

namespace ACLauncher.Services;

/// <summary>
/// 版本重命名：把 versions\&lt;旧名&gt; 目录及其 JSON / JAR 改成用户自定义的昵称。
/// 仅重命名目录与同名文件，不修改父版本（inheritsFrom）指向。
/// </summary>
public static class VersionRenameService
{
    /// <summary>校验昵称是否可用；返回错误信息（null 表示可用）。</summary>
    public static string? Validate(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "昵称不能为空。";
        if (trimmed.Length > 40) return "昵称最长 40 个字符。";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "昵称不能包含 \\ / : * ? \" < > | 等字符。";
        if (trimmed is "." or "..") return "昵称不合法。";

        var versionsDir = ConfigService.Instance.VersionsDirectory;
        if (Directory.Exists(Path.Combine(versionsDir, trimmed)))
        {
            return $"已存在同名版本「{trimmed}」，请换一个昵称。";
        }
        return null;
    }

    /// <summary>
    /// 重命名版本目录。成功返回新版本名；失败抛出异常。
    /// </summary>
    public static async Task<string> RenameAsync(string oldId, string newName)
    {
        var name = (newName ?? "").Trim();
        var versionsDir = ConfigService.Instance.VersionsDirectory;
        var oldDir = Path.Combine(versionsDir, oldId);
        var newDir = Path.Combine(versionsDir, name);

        if (!Directory.Exists(oldDir))
        {
            throw new DirectoryNotFoundException($"版本目录不存在：{oldId}");
        }
        if (string.Equals(oldId, name, StringComparison.OrdinalIgnoreCase))
        {
            return oldId;
        }
        if (Directory.Exists(newDir))
        {
            throw new InvalidOperationException($"已存在同名版本「{name}」。");
        }

        // 1. 移动目录
        Directory.Move(oldDir, newDir);

        // 2. 同名 JSON / JAR 改名
        var oldJson = Path.Combine(newDir, oldId + ".json");
        var newJson = Path.Combine(newDir, name + ".json");
        if (File.Exists(oldJson))
        {
            File.Move(oldJson, newJson, overwrite: true);
            await UpdateJsonIdAsync(newJson, name);
        }

        var oldJar = Path.Combine(newDir, oldId + ".jar");
        var newJar = Path.Combine(newDir, name + ".jar");
        if (File.Exists(oldJar)) File.Move(oldJar, newJar, overwrite: true);

        // 3. 同步当前版本配置
        try
        {
            var cfg = await ConfigService.Instance.LoadAsync();
            if (string.Equals(cfg.GameVersion, oldId, StringComparison.OrdinalIgnoreCase))
            {
                cfg.GameVersion = name;
                await ConfigService.Instance.SaveAsync(cfg);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"重命名后更新当前版本失败：{ex.Message}");
        }

        LogService.Instance.Log($"版本已重命名：{oldId} → {name}");
        return name;
    }

    /// <summary>把版本 JSON 的 id 字段改成新名字（启动器按目录名读取，改 id 只是保持一致）。</summary>
    private static async Task UpdateJsonIdAsync(string jsonPath, string newId)
    {
        try
        {
            var text = await File.ReadAllTextAsync(jsonPath);
            using var doc = JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                var idWritten = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("id"))
                    {
                        writer.WriteString("id", newId);
                        idWritten = true;
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                if (!idWritten) writer.WriteString("id", newId);
                writer.WriteEndObject();
            }
            await File.WriteAllBytesAsync(jsonPath, stream.ToArray());
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"更新版本 JSON 的 id 失败：{ex.Message}");
        }
    }
}
