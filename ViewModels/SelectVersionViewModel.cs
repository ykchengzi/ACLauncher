using System.Collections.ObjectModel;
using System.Windows.Input;
using ACLauncher.Services;
using Microsoft.UI.Dispatching;

namespace ACLauncher.ViewModels;

/// <summary>本地已安装的一个游戏版本（列表项）。</summary>
public class InstalledVersionItem : ViewModelBase
{
    /// <summary>版本名（versions/ 目录名，即版本 ID）。</summary>
    public string Id { get; init; } = "";

    /// <summary>模组加载器名称（原版 / Fabric / Quilt / Forge / NeoForge）。</summary>
    public string LoaderLabel { get; init; } = "原版";

    /// <summary>版本渠道标签（正式版 / 测试版 / 远古版 / 版本异常）。</summary>
    public string ChannelLabel { get; init; } = "正式版";

    /// <summary>渠道图标文件名（Blocks\ 下的方块贴图）。</summary>
    public string IconFile { get; init; } = "grass_block.png";

    /// <summary>是否为异常版本（版本文件缺失或损坏）。</summary>
    public bool IsBroken { get; init; }

    /// <summary>是否已安装模组加载器（原版之外都算）。</summary>
    public bool HasLoader => !string.Equals(LoaderLabel, "原版", StringComparison.Ordinal);

    /// <summary>搜索匹配（版本名 / Minecraft 版本 / 加载器）。</summary>
    public bool Matches(string keyword) =>
        Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || MinecraftVersion.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || LoaderLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>版本目录占用大小文本。</summary>
    public string SizeText { get; init; } = "";

    /// <summary>对应的 Minecraft 版本号（如 26.2）。</summary>
    public string MinecraftVersion { get; init; } = "";

    /// <summary>模组数量文本（无 mods 目录时为空）。</summary>
    public string ModsText { get; init; } = "";

    /// <summary>模组信息可见性（空则隐藏分隔点与文本）。</summary>
    public Microsoft.UI.Xaml.Visibility ModsVisibility =>
        string.IsNullOrEmpty(ModsText) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    private bool _isSelected;

    /// <summary>是否被点选（卡片高亮用）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isCurrent;

    /// <summary>是否为当前选中的版本。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(Display));
                OnPropertyChanged(nameof(CurrentVisibility));
            }
        }
    }

    /// <summary>列表展示文本（当前版本加标记）。</summary>
    public string Display => IsCurrent ? $"{Id}  （当前版本）" : Id;

    /// <summary>"当前"标记的可见性。</summary>
    public Microsoft.UI.Xaml.Visibility CurrentVisibility =>
        IsCurrent ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}

/// <summary>
/// 选择版本页视图模型：列出用户已下载（本地已安装）的所有版本，
/// 选择后设为当前版本（写入 config.json）。
/// </summary>
public class SelectVersionViewModel : ViewModelBase
{
    private readonly ConfigService _configService = ConfigService.Instance;
    private readonly DispatcherQueue _dispatcher;

    private IReadOnlyList<InstalledVersionItem> _visible = Array.Empty<InstalledVersionItem>();

    /// <summary>
    /// 当前可见的版本列表（已按搜索词过滤）。
    /// 用整体替换而不是逐项增删：ItemsRepeater 在布局过程中收到集合变更会抛 E_FAIL。
    /// </summary>
    public IReadOnlyList<InstalledVersionItem> Versions
    {
        get => _visible;
        private set
        {
            _visible = value;
            OnPropertyChanged(nameof(Versions));
        }
    }

    /// <summary>全部已安装版本（过滤前的原始列表）。</summary>
    private readonly List<InstalledVersionItem> _allVersions = new();

    private string _searchText = "";

    /// <summary>搜索词：按版本名 / Minecraft 版本 / 加载器过滤。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ApplyFilter();
        }
    }

    private InstalledVersionItem? _selectedVersion;

    /// <summary>当前选中的版本。</summary>
    public InstalledVersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                OnPropertyChanged(nameof(CanSelect));
                OnPropertyChanged(nameof(CanDelete));
                // 关键：选中项变化后刷新“设为当前版本”按钮的可执行状态，否则按钮一直灰
                (SelectCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>是否可设为当前版本。</summary>
    public bool CanSelect => SelectedVersion is not null;

    /// <summary>是否可删除（选中了某个版本，且游戏未在运行）。</summary>
    public bool CanDelete => SelectedVersion is not null
        && !GameLaunchService.Instance.IsRunning
        && !GameLaunchService.Instance.IsGameRunning;

    private string _status = "就绪";

    /// <summary>状态文字。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>设为当前版本命令。</summary>
    public ICommand SelectCommand { get; }

    /// <summary>删除所选版本命令（删除 versions 目录中对应版本文件夹）。</summary>
    public ICommand DeleteCommand { get; }

    public SelectVersionViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        SelectCommand = new RelayCommand(async () => await SelectAsync(), () => CanSelect);
        DeleteCommand = new RelayCommand(async () => await DeleteAsync(), () => CanDelete);
    }

    /// <summary>扫描本地 versions 目录，加载已安装版本并标记当前版本。</summary>
    public async Task LoadAsync()
    {
        _allVersions.Clear();
        try
        {
            var config = await _configService.LoadAsync();
            var current = config.GameVersion ?? "";

            var installed = _configService.GetInstalledVersions();
            foreach (var id in installed)
            {
                var modsCount = CountMods(id);
                var channel = VersionChannelService.DetectInstalled(id, _configService.VersionsDirectory);
                _allVersions.Add(new InstalledVersionItem
                {
                    Id = id,
                    IsCurrent = string.Equals(id, current, StringComparison.OrdinalIgnoreCase),
                    LoaderLabel = _configService.GetVersionLoaderLabel(id),
                    SizeText = $"{_configService.GetVersionSizeMB(id):F0} MB",
                    MinecraftVersion = _configService.GetVersionMinecraftVersion(id),
                    ModsText = modsCount > 0 ? $"{modsCount} 个模组" : "",
                    ChannelLabel = VersionChannelService.Label(channel),
                    IconFile = VersionChannelService.IconFile(channel),
                    IsBroken = channel == VersionChannel.Broken,
                });
            }

            ApplyFilter();

            // 默认选中当前版本（卡片高亮 + 底部按钮可用状态）
            var currentItem = Versions.FirstOrDefault(v => v.IsCurrent) ?? Versions.FirstOrDefault();
            if (currentItem is not null)
            {
                SelectCard(currentItem);
            }
        }
        catch (Exception ex)
        {
            Status = $"加载版本列表失败：{ex.Message}";
            LogService.Instance.Log($"加载版本列表失败：{ex}");
        }
    }

    /// <summary>按搜索词重建可见列表，并保持 / 清除选中状态。</summary>
    private void ApplyFilter()
    {
        var keyword = (_searchText ?? "").Trim();
        var keep = SelectedVersion;

        Versions = keyword.Length == 0
            ? _allVersions.ToList()
            : _allVersions.Where(v => v.Matches(keyword)).ToList();

        if (keep is not null && Versions.Contains(keep))
        {
            SelectedVersion = keep;
        }
        else
        {
            SelectedVersion = null;
            foreach (var v in _allVersions) v.IsSelected = false;
        }

        Status = _allVersions.Count == 0
            ? "尚未安装任何版本，请前往“下载”页下载。"
            : keyword.Length == 0
                ? $"共 {_allVersions.Count} 个已安装版本，选择后点击“设为当前版本”。"
                : $"筛选出 {Versions.Count} / {_allVersions.Count} 个版本。";
    }

    /// <summary>统计 mods 里的 jar 数量（随版本隔离设置取对应目录）。</summary>
    private int CountMods(string versionId)
    {
        try
        {
            var modsDir = _configService.GetModsDirectory(versionId);
            return Directory.Exists(modsDir) ? Directory.EnumerateFiles(modsDir, "*.jar").Count() : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>点击版本卡片：点选为待操作版本（底部按钮据此启用）。</summary>
    public void SelectCard(InstalledVersionItem item)
    {
        if (item is null) return;
        SelectedVersion = item;
        foreach (var v in Versions) v.IsSelected = ReferenceEquals(v, item);
    }

    /// <summary>把选中的版本设为当前版本（写入 config.json）。</summary>
    private async Task SelectAsync()
    {
        var selected = SelectedVersion;
        if (selected is null) return;

        try
        {
            var config = await _configService.LoadAsync();
            config.GameVersion = selected.Id;
            await _configService.SaveAsync(config);

            // 刷新当前标记
            foreach (var v in Versions) v.IsCurrent = string.Equals(v.Id, selected.Id, StringComparison.OrdinalIgnoreCase);
            Status = $"已将当前版本设为：{selected.Id}";
            LogService.Instance.Log($"已切换当前版本：{selected.Id}");
        }
        catch (Exception ex)
        {
            Status = $"设置当前版本失败：{ex.Message}";
            LogService.Instance.Log($"设置当前版本失败：{ex}");
        }
    }

    /// <summary>
    /// 删除所选版本：删除 versions 下的版本目录，释放磁盘空间。
    /// 删除当前版本时，自动把当前版本切换到剩余版本中按名称排序最新的一个（没有则清空）。
    /// 共享资源（libraries / assets / runtime）不删除，其他版本仍在使用。
    /// </summary>
    public async Task DeleteAsync()
    {
        var selected = SelectedVersion;
        if (selected is null) return;

        try
        {
            var versionDir = Path.Combine(_configService.VersionsDirectory, selected.Id);
            if (!Directory.Exists(versionDir))
            {
                Status = $"版本目录不存在：{selected.Id}";
                await LoadAsync();
                return;
            }

            Status = $"正在删除 {selected.Id} ...";
            await Task.Run(() => Directory.Delete(versionDir, true));
            LogService.Instance.Log($"已删除版本：{selected.Id}");

            // 若删除的是当前版本，切到剩余版本中按名称排序最新的
            var config = await _configService.LoadAsync();
            if (string.Equals(config.GameVersion, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                var remaining = _configService.GetInstalledVersions();
                config.GameVersion = remaining.Count > 0
                    ? remaining.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).First()
                    : "";
                await _configService.SaveAsync(config);
                LogService.Instance.Log(config.GameVersion.Length > 0
                    ? $"当前版本已自动切换为：{config.GameVersion}"
                    : "已无可用版本，当前版本已清空。");
            }

            await LoadAsync();
            Status = $"已删除版本 {selected.Id}";
        }
        catch (IOException ex) when (ex.Message.Contains("being used") || ex.HResult == unchecked((int)0x80070020))
        {
            Status = $"删除失败：{selected.Id} 的文件正在被占用（游戏可能未完全退出），请稍后重试。";
            LogService.Instance.Log($"删除版本失败（文件占用）：{ex.Message}");
        }
        catch (Exception ex)
        {
            Status = $"删除失败：{ex.Message}";
            LogService.Instance.Log($"删除版本失败：{ex}");
        }
    }
}
