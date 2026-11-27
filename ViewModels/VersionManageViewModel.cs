using ACLauncher.Services;
using Microsoft.UI.Xaml;

namespace ACLauncher.ViewModels;

/// <summary>
/// 版本管理页视图模型：单个已安装版本的详细信息与常用操作。
/// </summary>
public class VersionManageViewModel : ViewModelBase
{
    private readonly ConfigService _configService = ConfigService.Instance;

    public string VersionName { get; private set; } = "";

    private string _loaderLabel = "原版";
    public string LoaderLabel { get => _loaderLabel; private set => SetProperty(ref _loaderLabel, value); }

    private string _pathText = "";
    public string PathText { get => _pathText; private set => SetProperty(ref _pathText, value); }

    private string _sizeText = "";
    public string SizeText { get => _sizeText; private set => SetProperty(ref _sizeText, value); }

    private string _filesText = "";
    public string FilesText { get => _filesText; private set => SetProperty(ref _filesText, value); }

    private string _modsText = "0 个";
    public string ModsText { get => _modsText; private set => SetProperty(ref _modsText, value); }

    private string _savesText = "0 个";
    public string SavesText { get => _savesText; private set => SetProperty(ref _savesText, value); }

    private string _javaText = "未知";
    public string JavaText { get => _javaText; private set => SetProperty(ref _javaText, value); }

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        private set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(CurrentBadgeVisibility));
                OnPropertyChanged(nameof(SetCurrentButtonText));
                OnPropertyChanged(nameof(SetCurrentEnabled));
            }
        }
    }

    /// <summary>“设为当前版本”按钮可用性（已是当前版本则禁用）。</summary>
    public bool SetCurrentEnabled => !IsCurrent;

    public Visibility CurrentBadgeVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public string SetCurrentButtonText => IsCurrent ? "已是当前版本" : "设为当前版本";

    private string _status = "";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string VersionDir => Path.Combine(_configService.VersionsDirectory, VersionName);

    public string ModsDir => Path.Combine(VersionDir, "mods");

    public string SavesDir => Path.Combine(_configService.MinecraftDirectory, "saves");

    /// <summary>加载版本的详细信息。</summary>
    public async Task LoadAsync(string versionName)
    {
        VersionName = versionName;
        OnPropertyChanged(nameof(VersionName));
        LoaderLabel = _configService.GetVersionLoaderLabel(versionName);
        PathText = VersionDir;

        try
        {
            var config = await _configService.LoadAsync();
            IsCurrent = string.Equals(config.GameVersion, versionName, StringComparison.OrdinalIgnoreCase);
        }
        catch { IsCurrent = false; }

        // 统计（后台线程避免卡 UI）
        await Task.Run(() =>
        {
            try
            {
                var dir = new DirectoryInfo(VersionDir);
                var files = dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).ToList() : new List<FileInfo>();
                SizeText = $"{files.Sum(f => f.Length) / 1024.0 / 1024.0:F0} MB";
                FilesText = $"{files.Count} 个文件";
            }
            catch
            {
                SizeText = "--";
                FilesText = "--";
            }

            try
            {
                ModsText = Directory.Exists(ModsDir)
                    ? $"{Directory.EnumerateFiles(ModsDir, "*.jar").Count()} 个"
                    : "无 mods 目录";
            }
            catch { ModsText = "--"; }

            try
            {
                SavesText = Directory.Exists(SavesDir)
                    ? $"{Directory.GetDirectories(SavesDir).Length} 个存档"
                    : "无存档";
            }
            catch { SavesText = "--"; }
        });

        // Java 要求
        try
        {
            var jsonPath = Path.Combine(VersionDir, versionName + ".json");
            if (File.Exists(jsonPath))
            {
                var json = await VersionParserService.Instance.ParseAsync(versionName);
                var major = json.JavaVersion?.MajorVersion ?? 0;
                JavaText = major > 0 ? $"Java {major}+" : "未声明";
            }
            else
            {
                JavaText = "无版本文件";
            }
        }
        catch
        {
            JavaText = "读取失败";
        }
    }

    /// <summary>把该版本设为当前版本。</summary>
    public async Task<bool> SetCurrentAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            config.GameVersion = VersionName;
            await _configService.SaveAsync(config);
            IsCurrent = true;
            Status = $"已设为当前版本：{VersionName}";
            LogService.Instance.Log($"版本管理：已切换当前版本为 {VersionName}");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"设置失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>删除该版本（共享库与资源不受影响）；删除当前版本时自动切换到剩余最新版本。</summary>
    public async Task<bool> DeleteAsync()
    {
        try
        {
            if (!Directory.Exists(VersionDir))
            {
                Status = "版本目录不存在";
                return false;
            }

            Status = "正在删除...";
            await Task.Run(() => Directory.Delete(VersionDir, true));
            LogService.Instance.Log($"已删除版本：{VersionName}");

            var config = await _configService.LoadAsync();
            if (string.Equals(config.GameVersion, VersionName, StringComparison.OrdinalIgnoreCase))
            {
                var remaining = _configService.GetInstalledVersions();
                config.GameVersion = remaining.Count > 0
                    ? remaining.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).First()
                    : "";
                await _configService.SaveAsync(config);
            }
            return true;
        }
        catch (Exception ex)
        {
            Status = $"删除失败：{ex.Message}";
            LogService.Instance.Log($"删除版本失败：{ex}");
            return false;
        }
    }
}
