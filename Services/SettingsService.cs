using System.Text.Json;
using ACLauncher.Helpers;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>
/// 内部设置服务：读写启动器目录下的 settings.json（Java 路径、内存、自定义参数、主题）。
/// </summary>
public class SettingsService
{
    public static SettingsService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>settings.json 绝对路径。</summary>
    public string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>
    /// 同步加载设置（小文件，直接 File.ReadAllText，避免在 UI 线程上 .GetAwaiter().GetResult() 造成死锁）。
    /// 文件不存在或损坏时返回默认设置。
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>保存设置（同步；用于启动计数这类必须在退出前落盘的小改动）。</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"保存设置失败：{ex.Message}");
        }
    }

    /// <summary>保存设置。</summary>
    public async Task SaveAsync(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(SettingsPath, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"保存设置失败：{ex.Message}");
        }
    }
}
