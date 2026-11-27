using System.Collections.ObjectModel;
using System.Text.Json;
using ACLauncher.Services;
using Microsoft.UI.Xaml;

namespace ACLauncher.ViewModels;

/// <summary>版本 mods 目录下的一个模组文件。</summary>
public class InstalledModItem
{
    /// <summary>文件名（含 .jar）。</summary>
    public string FileName { get; init; } = "";

    /// <summary>完整路径。</summary>
    public string FullPath { get; init; } = "";

    /// <summary>文件大小展示文本。</summary>
    public string SizeText { get; init; } = "";
}

/// <summary>
/// 版本管理页视图模型：单个已安装版本的详细信息、加载器安装与模组管理。
/// </summary>
public class VersionManageViewModel : ViewModelBase
{
    private readonly ConfigService _configService = ConfigService.Instance;

    public string VersionName { get; private set; } = "";

    /// <summary>对应的 Minecraft 版本号（安装加载器时使用）。</summary>
    public string MinecraftVersion { get; private set; } = "";

    private string _loaderLabel = "原版";
    public string LoaderLabel { get => _loaderLabel; private set => SetProperty(ref _loaderLabel, value); }

    private string _channelLabel = "正式版";

    /// <summary>版本渠道标签（正式版 / 测试版 / 远古版 / 版本异常）。</summary>
    public string ChannelLabel { get => _channelLabel; private set => SetProperty(ref _channelLabel, value); }

    private string _iconFile = "grass_block.png";

    /// <summary>渠道图标文件名（Blocks\ 下的方块贴图）。</summary>
    public string IconFile { get => _iconFile; private set => SetProperty(ref _iconFile, value); }

    private bool _isBroken;

    /// <summary>是否为异常版本（版本文件缺失或损坏）。</summary>
    public bool IsBroken { get => _isBroken; private set => SetProperty(ref _isBroken, value); }

    /// <summary>是否已安装模组加载器。</summary>
    public bool HasLoader => !string.Equals(LoaderLabel, "原版", StringComparison.Ordinal);

    /// <summary>已安装加载器的版本号（如 0.16.5；原版为空）。</summary>
    private string _loaderVersionText = "无";
    public string LoaderVersionText { get => _loaderVersionText; private set => SetProperty(ref _loaderVersionText, value); }

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

    public string ModsDir => _configService.GetModsDirectory(VersionName);

    public string SavesDir => VersionIsolationService.GetSavesDirectory(VersionName);

    // ===== 版本隔离（“管理”标签页） =====

    /// <summary>隔离选项的可选值。</summary>
    public IReadOnlyList<string> IsolationOptions { get; } = new[] { "跟随全局", "隔离", "不隔离" };

    private bool _suppressIsolationSave = true;

    private string _isolationOption = "跟随全局";

    /// <summary>该版本的隔离选项（跟随全局 / 隔离 / 不隔离）。</summary>
    public string IsolationOption
    {
        get => _isolationOption;
        set
        {
            if (!SetProperty(ref _isolationOption, value)) return;
            if (!_suppressIsolationSave) ApplyIsolationOption(value);
        }
    }

    /// <summary>当前生效的隔离状态说明。</summary>
    public string IsolationSummary => VersionIsolationService.IsIsolated(VersionName)
        ? "已隔离：游戏目录为本版本目录，世界 / 模组 / 配置与其他版本互不干扰。"
        : "未隔离：游戏目录为 .minecraft，世界 / 模组 / 配置与其他未隔离版本共用。";

    /// <summary>当前生效的游戏目录。</summary>
    public string IsolationPathText => VersionIsolationService.GetGameDirectory(VersionName);

    /// <summary>是否已隔离（界面标签用）。</summary>
    public bool IsIsolated => VersionIsolationService.IsIsolated(VersionName);

    public Visibility IsolatedBadgeVisibility => IsIsolated ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>用户切换隔离选项：写入覆盖并刷新目录相关统计。</summary>
    private void ApplyIsolationOption(string label)
    {
        var value = label switch
        {
            "隔离" => VersionIsolationService.OverrideOn,
            "不隔离" => VersionIsolationService.OverrideOff,
            _ => null,
        };

        VersionIsolationService.SetOverride(VersionName, value);
        Status = label == "跟随全局"
            ? "已恢复为跟随全局隔离策略。"
            : $"已将该版本设为{(label == "隔离" ? "隔离" : "不隔离")}，下次启动生效。";

        OnPropertyChanged(nameof(IsIsolated));
        OnPropertyChanged(nameof(IsolationSummary));
        OnPropertyChanged(nameof(IsolationPathText));
        OnPropertyChanged(nameof(IsolatedBadgeVisibility));
        OnPropertyChanged(nameof(ModsDir));
        OnPropertyChanged(nameof(SavesDir));

        _ = RefreshModsAsync();
        _ = RefreshSaveCountAsync();
    }

    // ===== 加载器管理（“管理”标签页） =====

    /// <summary>可安装的加载器种类。</summary>
    public IReadOnlyList<string> LoaderKinds { get; } = new[] { "Fabric", "Quilt", "Forge", "NeoForge" };

    private string _selectedLoaderKind = "Fabric";
    public string SelectedLoaderKind
    {
        get => _selectedLoaderKind;
        set
        {
            if (SetProperty(ref _selectedLoaderKind, value)) _ = LoadLoaderVersionsAsync();
        }
    }

    /// <summary>所选加载器在当前 MC 版本下可用的版本（新到旧）。</summary>
    public ObservableCollection<string> LoaderVersions { get; } = new();

    private string? _selectedLoaderVersion;
    public string? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set
        {
            if (SetProperty(ref _selectedLoaderVersion, value)) OnPropertyChanged(nameof(InstallLoaderEnabled));
        }
    }

    public bool InstallLoaderEnabled => !IsInstallingLoader && !string.IsNullOrWhiteSpace(SelectedLoaderVersion);

    private bool _isLoadingLoaders;
    public bool IsLoadingLoaders
    {
        get => _isLoadingLoaders;
        private set
        {
            if (SetProperty(ref _isLoadingLoaders, value)) OnPropertyChanged(nameof(InstallLoaderEnabled));
        }
    }

    private bool _isInstallingLoader;
    public bool IsInstallingLoader
    {
        get => _isInstallingLoader;
        private set
        {
            if (SetProperty(ref _isInstallingLoader, value)) OnPropertyChanged(nameof(InstallLoaderEnabled));
        }
    }

    private string _loaderActionStatus = "";
    public string LoaderActionStatus { get => _loaderActionStatus; private set => SetProperty(ref _loaderActionStatus, value); }

    // ===== 模组管理（“模组”标签页） =====

    /// <summary>版本 mods 目录下的模组文件。</summary>
    public ObservableCollection<InstalledModItem> Mods { get; } = new();

    private InstalledModItem? _selectedMod;
    public InstalledModItem? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value)) OnPropertyChanged(nameof(CanDeleteMod));
        }
    }

    public bool CanDeleteMod => SelectedMod is not null;

    /// <summary>模组列表为空时的提示可见性。</summary>
    public Visibility ModsEmptyVisibility => Mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>加载版本的详细信息（异常不外抛，避免中断后续初始化）。</summary>
    public async Task LoadAsync(string versionName)
    {
        try
        {
            await LoadCoreAsync(versionName);
        }
        catch (Exception ex)
        {
            Status = $"加载版本信息失败：{ex.Message}";
            LogService.Instance.Log($"版本管理：加载失败：{ex}");
        }
    }

    private async Task LoadCoreAsync(string versionName)
    {
        VersionName = versionName;
        OnPropertyChanged(nameof(VersionName));
        OnPropertyChanged(nameof(VersionDir));
        OnPropertyChanged(nameof(ModsDir));
        OnPropertyChanged(nameof(SavesDir));

        MinecraftVersion = _configService.GetVersionMinecraftVersion(versionName);
        LoaderLabel = _configService.GetVersionLoaderLabel(versionName);

        var channel = VersionChannelService.DetectInstalled(versionName, _configService.VersionsDirectory);
        ChannelLabel = VersionChannelService.Label(channel);
        IconFile = VersionChannelService.IconFile(channel);
        IsBroken = channel == VersionChannel.Broken;

        OnPropertyChanged(nameof(HasLoader));
        PathText = VersionDir;
        LoaderVersionText = HasLoader ? DetectLoaderVersion(versionName) : "无";

        try
        {
            var config = await _configService.LoadAsync();
            IsCurrent = string.Equals(config.GameVersion, versionName, StringComparison.OrdinalIgnoreCase);
        }
        catch { IsCurrent = false; }

        // 统计（在后台线程计算，回到 UI 线程再赋值，避免跨线程更新绑定属性）
        try
        {
            var modsDir = ModsDir;
            var savesDir = SavesDir;
            var stats = await Task.Run(() =>
            {
                var size = "--";
                var files = "--";
                try
                {
                    var dir = new DirectoryInfo(VersionDir);
                    var list = dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).ToList() : new List<FileInfo>();
                    size = $"{list.Sum(f => f.Length) / 1024.0 / 1024.0:F0} MB";
                    files = $"{list.Count} 个文件";
                }
                catch { }

                var mods = "--";
                try
                {
                    mods = Directory.Exists(modsDir)
                        ? $"{Directory.EnumerateFiles(modsDir, "*.jar").Count()} 个"
                        : "无 mods 目录";
                }
                catch { }

                var saves = "--";
                try
                {
                    saves = Directory.Exists(savesDir)
                        ? $"{Directory.GetDirectories(savesDir).Length} 个存档"
                        : "无存档";
                }
                catch { }

                return (Size: size, Files: files, Mods: mods, Saves: saves);
            });

            SizeText = stats.Size;
            FilesText = stats.Files;
            ModsText = stats.Mods;
            SavesText = stats.Saves;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"版本管理：统计版本信息失败：{ex.Message}");
        }

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

        // 隔离选项：显示当前覆盖值（加载期间不触发保存）
        _suppressIsolationSave = true;
        _isolationOption = VersionIsolationService.GetOverride(versionName) switch
        {
            VersionIsolationService.OverrideOn => "隔离",
            VersionIsolationService.OverrideOff => "不隔离",
            _ => "跟随全局",
        };
        OnPropertyChanged(nameof(IsolationOption));
        OnPropertyChanged(nameof(IsIsolated));
        OnPropertyChanged(nameof(IsolationSummary));
        OnPropertyChanged(nameof(IsolationPathText));
        OnPropertyChanged(nameof(IsolatedBadgeVisibility));
        _suppressIsolationSave = false;

        // 加载器下拉框：默认停在已安装的加载器上
        if (HasLoader && LoaderKinds.Contains(LoaderLabel))
        {
            _selectedLoaderKind = LoaderLabel;
            OnPropertyChanged(nameof(SelectedLoaderKind));
        }
        _ = LoadLoaderVersionsAsync();

        await RefreshModsAsync();

        if (IsBroken)
        {
            Status = "该版本的文件不完整或已损坏，可能无法正常启动。";
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

    // ===== 加载器 =====

    /// <summary>拉取所选加载器在对应 MC 版本下可用的版本列表。</summary>
    public async Task LoadLoaderVersionsAsync()
    {
        var kind = _selectedLoaderKind.ToLowerInvariant();
        IsLoadingLoaders = true;
        LoaderActionStatus = $"正在获取 {_selectedLoaderKind} 版本列表…";
        LogService.Instance.Log($"版本管理：查询 {MinecraftVersion} 的 {_selectedLoaderKind} 版本列表…");
        try
        {
            var list = await LoaderService.Instance.GetLoaderVersionsAsync(MinecraftVersion, kind);
            LoaderVersions.Clear();
            foreach (var v in list) LoaderVersions.Add(v);

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            LoaderActionStatus = LoaderVersions.Count == 0
                ? $"未找到适用于 Minecraft {MinecraftVersion} 的 {_selectedLoaderKind} 版本。"
                : $"共 {LoaderVersions.Count} 个 {_selectedLoaderKind} 版本可选。";
            LogService.Instance.Log($"版本管理：{_selectedLoaderKind} 可选版本 {LoaderVersions.Count} 个（最新 {SelectedLoaderVersion ?? "无"}）。");
        }
        catch (Exception ex)
        {
            LoaderVersions.Clear();
            SelectedLoaderVersion = null;
            LoaderActionStatus = $"获取加载器版本失败：{ex.Message}";
            LogService.Instance.Log($"版本管理：获取加载器版本失败：{ex}");
        }
        finally
        {
            IsLoadingLoaders = false;
        }
    }

    /// <summary>安装（或更换）所选加载器：会生成一个新的版本目录，不覆盖当前版本。</summary>
    public async Task InstallSelectedLoaderAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedLoaderVersion))
        {
            LoaderActionStatus = "请先选择一个加载器版本。";
            return;
        }

        var kind = _selectedLoaderKind.ToLowerInvariant();
        IsInstallingLoader = true;
        LoaderActionStatus = $"正在安装 {SelectedLoaderKind} {SelectedLoaderVersion} …（需要下载依赖库，请稍候）";
        try
        {
            var versionId = await LoaderService.Instance.InstallAsync(MinecraftVersion, kind, SelectedLoaderVersion);
            LoaderActionStatus = $"安装完成：新版本「{versionId}」已添加到“选择版本”页，可切换使用。";
            LogService.Instance.Log($"版本管理：为 {MinecraftVersion} 安装 {SelectedLoaderKind} {SelectedLoaderVersion} → {versionId}");
        }
        catch (Exception ex)
        {
            LoaderActionStatus = $"安装失败：{ex.Message}";
            LogService.Instance.Log($"安装加载器失败：{ex}");
        }
        finally
        {
            IsInstallingLoader = false;
        }
    }

    // ===== 模组 =====

    /// <summary>刷新存档数量（隔离状态变化后调用）。</summary>
    private async Task RefreshSaveCountAsync()
    {
        var dir = SavesDir;
        SavesText = await Task.Run(() =>
        {
            try
            {
                return Directory.Exists(dir) ? $"{Directory.GetDirectories(dir).Length} 个存档" : "无存档";
            }
            catch
            {
                return "--";
            }
        });
    }

    /// <summary>重新扫描 mods 目录。</summary>
    public async Task RefreshModsAsync()
    {
        var items = await Task.Run(() =>
        {
            var list = new List<InstalledModItem>();
            try
            {
                if (Directory.Exists(ModsDir))
                {
                    foreach (var f in new DirectoryInfo(ModsDir).EnumerateFiles("*.jar")
                                 .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(new InstalledModItem
                        {
                            FileName = f.Name,
                            FullPath = f.FullName,
                            SizeText = f.Length >= 1024 * 1024
                                ? $"{f.Length / 1024.0 / 1024.0:F1} MB"
                                : $"{f.Length / 1024.0:F0} KB",
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Log($"扫描模组失败：{ex.Message}");
            }
            return list;
        });

        Mods.Clear();
        foreach (var item in items) Mods.Add(item);
        SelectedMod = null;
        ModsText = $"{Mods.Count} 个";
        OnPropertyChanged(nameof(ModsEmptyVisibility));
    }

    /// <summary>删除选中的模组文件。</summary>
    public bool DeleteSelectedMod()
    {
        var mod = SelectedMod;
        if (mod is null) return false;

        try
        {
            File.Delete(mod.FullPath);
            LogService.Instance.Log($"版本管理：已删除模组 {mod.FileName}");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"删除模组失败：{ex.Message}";
            LogService.Instance.Log($"删除模组失败：{ex}");
            return false;
        }
    }

    /// <summary>从版本 JSON 的 libraries 中读出加载器版本号（读不到返回“未知”）。</summary>
    private string DetectLoaderVersion(string versionName)
    {
        try
        {
            var jsonPath = Path.Combine(VersionDir, versionName + ".json");
            if (!File.Exists(jsonPath)) return "未知";

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!doc.RootElement.TryGetProperty("libraries", out var libs) || libs.ValueKind != JsonValueKind.Array)
                return "未知";

            foreach (var lib in libs.EnumerateArray())
            {
                if (!lib.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { } name) continue;

                // name 形如 net.fabricmc:fabric-loader:0.16.5
                var parts = name.Split(':');
                if (parts.Length < 3) continue;

                var artifact = parts[1];
                var version = parts[2];
                var match = LoaderLabel switch
                {
                    "Fabric" => artifact == "fabric-loader",
                    "Quilt" => artifact == "quilt-loader",
                    "Forge" => artifact is "forge" or "forge-all",
                    "NeoForge" => artifact is "neoforge" or "neoforge-all",
                    _ => false,
                };
                if (match) return version;
            }
        }
        catch
        {
            // 忽略：读不到版本号不影响其它功能
        }
        return "未知";
    }
}
