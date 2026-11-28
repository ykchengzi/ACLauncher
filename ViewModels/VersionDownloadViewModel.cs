using System.Collections.ObjectModel;
using System.Windows.Input;
using ACLauncher.Services;
using Microsoft.UI.Dispatching;

namespace ACLauncher.ViewModels;

/// <summary>
/// 版本下载页视图模型：展示可下载版本列表（一行一个版本），
/// 下载所选版本并设为当前版本。
/// </summary>
public class VersionDownloadViewModel : ViewModelBase
{
    private readonly VersionDownloadService _downloadService = VersionDownloadService.Instance;
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private readonly DispatcherQueue _dispatcher;

    // ===================== 页面分区（左侧导航） =====================

    private string _selectedSection = "Game";

    /// <summary>当前分区：Game / Pack / Mod / Resourcepack / Shader / World。</summary>
    public string SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (!SetProperty(ref _selectedSection, value)) return;
            foreach (var name in new[] { nameof(GameVisible), nameof(PackVisible), nameof(ContentVisible),
                         nameof(PackSearchVisible), nameof(PackSourceBarVisible),
                         nameof(ContentHeaderText), nameof(IsModSection), nameof(IsPackSection),
                         nameof(VersionFilterVisible), nameof(BrowseOnlyVisible),
                         nameof(DetailKindText), nameof(DetailMetaText), nameof(DetailActionText) })
            {
                OnPropertyChanged(name);
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility GameVisible =>
        SelectedSection is "Game" ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>整合包：官方源列表可见性。</summary>
    public Microsoft.UI.Xaml.Visibility PackVisible =>
        SelectedSection is "Pack" && PackSource == "official"
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>整合包：Modrinth 搜索面板可见性。</summary>
    public Microsoft.UI.Xaml.Visibility PackSearchVisible =>
        SelectedSection is "Pack" && PackSource == "modrinth"
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>整合包下载源选择栏可见性。</summary>
    public Microsoft.UI.Xaml.Visibility PackSourceBarVisible =>
        SelectedSection is "Pack" ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ContentVisible =>
        SelectedSection is "Mod" or "Resourcepack" or "Shader" || PackSearchVisible == Microsoft.UI.Xaml.Visibility.Visible
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>内容区标题（模组 / 资源包 / 光影）。</summary>
    public string ContentHeaderText => SelectedSection switch
    {
        "Resourcepack" => "资源包",
        "Shader" => "光影",
        "Pack" => "整合包",
        _ => "模组",
    };

    /// <summary>当前是否模组分区（模组需要按加载器过滤）。</summary>
    public bool IsModSection => SelectedSection is "Mod";

    /// <summary>当前是否整合包分区（仅按类别筛选）。</summary>
    public bool IsPackSection => SelectedSection is "Pack";

    /// <summary>非整合包分区时显示“浏览…下载”按钮。</summary>
    public Microsoft.UI.Xaml.Visibility BrowseOnlyVisible =>
        IsPackSection ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>版本筛选栏可见性（整合包分区只保留类别筛选）。</summary>
    public Microsoft.UI.Xaml.Visibility VersionFilterVisible =>
        IsPackSection ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>切换分区（由左侧导航触发）。</summary>
    public void SetSection(string section)
    {
        if (string.IsNullOrEmpty(section)) return;
        SelectedSection = section;
        // 离开详情状态，避免残留
        CloseDetails();
        // 按新分区重建筛选项
        RebuildFilterOptions();

    }

    /// <summary>切换到分区并自动加载（内容类分区自动发起一次搜索）。</summary>
    public async Task EnterSectionAsync(string section)
    {
        SetSection(section);
        var isBrowseSection = section is "Mod" or "Resourcepack" or "Shader"
                              || (section is "Pack" && PackSource == "modrinth");
        if (isBrowseSection)
        {
            await SearchModrinthAsync();
        }
    }

    // ===================== 整合包下载源 =====================

    private string _packSource = "official";

    /// <summary>整合包下载源："official" = 橙猫服官方；"modrinth" = Modrinth。</summary>
    public string PackSource
    {
        get => _packSource;
        private set
        {
            if (!SetProperty(ref _packSource, value)) return;
            OnPropertyChanged(nameof(PackVisible));
            OnPropertyChanged(nameof(PackSearchVisible));
            OnPropertyChanged(nameof(ContentVisible));
            OnPropertyChanged(nameof(SelectionsAreModrinth));
            CloseDetails();
        }
    }

    /// <summary>整合包是否选择了 Modrinth 源（供界面下拉框初始化用）。</summary>
    public bool SelectionsAreModrinth => PackSource == "modrinth";

    /// <summary>切换整合包源（由界面下拉框触发）。</summary>
    public void SetPackSource(string source)
    {
        PackSource = source == "modrinth" ? "modrinth" : "official";
        if (PackSource == "modrinth")
        {
            _ = SearchModrinthAsync();
        }
    }

    // ===================== 筛选 =====================

    /// <summary>筛选选项（显示名 + Modrinth 值；值为空表示"全部"）。</summary>
    public sealed record FilterOption(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    /// <summary>类别选项（随分区不同）。</summary>
    public ObservableCollection<FilterOption> FilterCategories { get; } = new();

    /// <summary>游戏版本选项。</summary>
    public ObservableCollection<FilterOption> FilterVersions { get; } = new();

    /// <summary>模组加载器选项。</summary>
    public ObservableCollection<FilterOption> FilterLoaders { get; } = new();

    private FilterOption? _filterCategory;
    public FilterOption? FilterCategory
    {
        get => _filterCategory;
        set => SetProperty(ref _filterCategory, value);
    }

    private FilterOption? _filterVersion;
    public FilterOption? FilterVersion
    {
        get => _filterVersion;
        set => SetProperty(ref _filterVersion, value);
    }

    private FilterOption? _filterLoader;
    public FilterOption? FilterLoader
    {
        get => _filterLoader;
        set => SetProperty(ref _filterLoader, value);
    }

    /// <summary>按当前分区重建筛选选项（类别 / 版本 / 加载器）。</summary>
    private void RebuildFilterOptions()
    {
        // 类别（按 Modrinth 分区）
        FilterCategories.Clear();
        FilterCategories.Add(new FilterOption("全部类别", ""));
        var categories = CurrentProjectType switch
        {
            "resourcepack" => new[]
            {
                ("16x", "16x"), ("32x", "32x"), ("64x", "64x"), ("128x", "128x"), ("256x", "256x"),
                ("真实", "realistic"), ("简洁", "simplistic"), ("主题", "themed"), ("字体", "fonts"), ("界面", "gui"),
            },
            "shader" => new[]
            {
                ("幻想", "fantasy"), ("真实", "realistic"), ("半写实", "semi-realistic"), ("卡通", "cartoon"),
                ("低配", "low"), ("中配", "medium"), ("高配", "high"), ("原版风", "vanilla-like"),
            },
            "modpack" => new[]
            {
                ("冒险", "adventure"), ("挑战", "challenge"), ("战斗", "combat"), ("科技", "technology"),
                ("魔法", "magic"), ("任务", "quests"), ("轻量", "lightweight"), ("多人", "multiplayer"), ("优化", "optimization"),
            },
            _ => new[]
            {
                ("优化", "optimization"), ("辅助", "utility"), ("科技", "technology"), ("魔法", "magic"),
                ("冒险", "adventure"), ("世界生成", "worldgen"), ("装饰", "decoration"), ("库", "library"),
            },
        };
        foreach (var (label, value) in categories) FilterCategories.Add(new FilterOption(label, value));
        FilterCategory = FilterCategories[0];

        // 版本：全部 + 当前版本 + 正式版列表
        // 只有当前版本装了模组加载器时才自动选中“当前版本”；原版不自动填，避免筛不到内容
        FilterVersions.Clear();
        FilterVersions.Add(new FilterOption("全部版本", ""));
        if (!string.IsNullOrEmpty(CurrentGameVersion))
        {
            FilterVersions.Add(new FilterOption($"当前版本（{CurrentGameVersion}）", CurrentGameVersion));
        }
        foreach (var v in _allVanilla.Where(v => v.Category == "正式版").Take(30))
        {
            if (v.Id == CurrentGameVersion) continue;
            FilterVersions.Add(new FilterOption(v.Id, v.Id));
        }
        FilterVersion = CurrentVersionHasLoader && !string.IsNullOrEmpty(CurrentGameVersion)
            ? FilterVersions[1]
            : FilterVersions[0];

        // 加载器（仅模组使用）：当前版本带加载器时默认选中该加载器
        FilterLoaders.Clear();
        FilterLoaders.Add(new FilterOption("全部加载器", ""));
        FilterLoaders.Add(new FilterOption("Fabric", "fabric"));
        FilterLoaders.Add(new FilterOption("Forge", "forge"));
        FilterLoaders.Add(new FilterOption("NeoForge", "neoforge"));
        FilterLoaders.Add(new FilterOption("Quilt", "quilt"));
        FilterLoader = CurrentVersionHasLoader
            ? FilterLoaders.FirstOrDefault(o =>
                  string.Equals(o.Value, CurrentLoader, StringComparison.OrdinalIgnoreCase)) ?? FilterLoaders[0]
            : FilterLoaders[0];
    }

    // ===================== 模组 / 资源包 / 光影 / 整合包（Modrinth） =====================

    /// <summary>搜索结果集合（当前页）。</summary>
    public ObservableCollection<ModrinthProjectItem> SearchResults { get; } = new();

    /// <summary>当前项目类型（mod / resourcepack / shader / modpack）。</summary>
    private string CurrentProjectType => SelectedSection switch
    {
        "Resourcepack" => "resourcepack",
        "Shader" => "shader",
        "Pack" => "modpack",
        _ => "mod",
    };

    // ---- 分页 ----

    private const int PageSize = 20;
    private int _pageIndex; // 0 基
    private int _totalHits;

    /// <summary>页码展示（如 "3 / 27"）。</summary>
    public string PageText => _totalHits <= 0
        ? ""
        : $"{_pageIndex + 1} / {Math.Max(1, (int)Math.Ceiling(_totalHits / (double)PageSize))}";

    /// <summary>是否可上一页。</summary>
    public bool CanPrevPage => _pageIndex > 0;

    /// <summary>是否可下一页。</summary>
    public bool CanNextPage => (_pageIndex + 1) * PageSize < _totalHits;

    /// <summary>下一页。</summary>
    public async Task NextPageAsync()
    {
        if (!CanNextPage || IsSearching) return;
        _pageIndex++;
        await FetchPageAsync();
    }

    /// <summary>上一页。</summary>
    public async Task PrevPageAsync()
    {
        if (!CanPrevPage || IsSearching) return;
        _pageIndex--;
        await FetchPageAsync();
    }

    private string _searchQuery = "";

    /// <summary>搜索关键词。</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    private ModrinthProjectItem? _selectedProject;

    /// <summary>选中的搜索结果。</summary>
    public ModrinthProjectItem? SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value);
    }

    private bool _isSearching;

    /// <summary>是否正在搜索 / 安装。</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(CanPrevPage));
                OnPropertyChanged(nameof(CanNextPage));
            }
        }
    }

    /// <summary>当前版本的文件夹名（来自配置，如 26.2-fabric-0.19.5）。</summary>
    public string CurrentVersionFolder { get; private set; } = "";

    /// <summary>当前版本对应的 Minecraft 版本号（如 26.2，不是文件夹名；供筛选使用）。</summary>
    public string CurrentGameVersion { get; private set; } = "";

    /// <summary>当前版本是否安装了模组加载器。</summary>
    public bool CurrentVersionHasLoader => CurrentLoader is not null;

    /// <summary>从当前版本名推断加载器（fabric / quilt / forge / neoforge / null）。</summary>
    public string? CurrentLoader
    {
        get
        {
            var v = CurrentVersionFolder.ToLowerInvariant();
            if (v.Contains("neoforge")) return "neoforge";
            if (v.Contains("fabric")) return "fabric";
            if (v.Contains("quilt")) return "quilt";
            if (v.Contains("forge")) return "forge";
            return null;
        }
    }

    /// <summary>重新读取当前版本（安装加载器后刷新加载器判断）。</summary>
    public async Task RefreshCurrentVersionAsync()
    {
        try
        {
            CurrentVersionFolder = (await ConfigService.Instance.LoadAsync()).GameVersion ?? "";
        }
        catch
        {
            CurrentVersionFolder = "";
        }
        CurrentGameVersion = string.IsNullOrEmpty(CurrentVersionFolder)
            ? ""
            : ConfigService.Instance.GetVersionMinecraftVersion(CurrentVersionFolder);

        OnPropertyChanged(nameof(CurrentVersionFolder));
        OnPropertyChanged(nameof(CurrentGameVersion));
        OnPropertyChanged(nameof(CurrentLoader));
        OnPropertyChanged(nameof(CurrentVersionHasLoader));
    }

    /// <summary>搜索当前分区的项目（重置到第一页）。</summary>
    public async Task SearchModrinthAsync()
    {
        if (IsSearching) return;
        _pageIndex = 0;
        await FetchPageAsync();
    }

    /// <summary>拉取当前页数据。</summary>
    private async Task FetchPageAsync()
    {
        IsSearching = true;
        Status = "正在搜索...";
        try
        {
            var type = CurrentProjectType;
            // 筛选条件：版本 / 加载器（仅模组）/ 类别，空值表示全部
            var gameVersion = FilterVersion?.Value ?? "";
            var loader = type == "mod" ? FilterLoader?.Value : null;
            var category = FilterCategory?.Value ?? "";

            var (items, totalHits) = await ModrinthService.Instance.SearchPagedAsync(
                SearchQuery.Trim(), type, gameVersion, loader, _pageIndex * PageSize, PageSize, category);

            SearchResults.Clear();
            foreach (var r in items) SearchResults.Add(r);
            _totalHits = totalHits;
            OnPropertyChanged(nameof(PageText));
            OnPropertyChanged(nameof(CanPrevPage));
            OnPropertyChanged(nameof(CanNextPage));

            var versionLabel = string.IsNullOrEmpty(gameVersion) ? "全部版本" : gameVersion;
            Status = totalHits == 0
                ? $"没有找到符合条件（{versionLabel}）的项目"
                : $"共 {totalHits} 个项目";
        }
        catch (Exception ex)
        {
            Status = $"搜索失败：{ex.Message}";
            LogService.Instance.Log($"Modrinth 搜索失败：{ex}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    // ===================== 项目详情 =====================

    /// <summary>详情页：按游戏版本分组（下拉框选择版本，列表显示该版本下的文件）。</summary>
    public ObservableCollection<VersionGroup> ProjectVersionGroups { get; } = new();

    private VersionGroup? _selectedVersionGroup;

    /// <summary>详情页当前选中的游戏版本分组。</summary>
    public VersionGroup? SelectedVersionGroup
    {
        get => _selectedVersionGroup;
        set
        {
            if (!SetProperty(ref _selectedVersionGroup, value)) return;
            GroupFiles.Clear();
            if (value is not null)
            {
                foreach (var f in value.Files) GroupFiles.Add(f);
            }
            SelectedProjectVersion = GroupFiles.FirstOrDefault();
        }
    }

    /// <summary>当前分组下的文件列表（UI 绑定）。</summary>
    public ObservableCollection<ModrinthVersionItem> GroupFiles { get; } = new();

    private ModrinthVersionItem? _selectedProjectVersion;

    /// <summary>详情页中选中的版本文件。</summary>
    public ModrinthVersionItem? SelectedProjectVersion
    {
        get => _selectedProjectVersion;
        set
        {
            if (SetProperty(ref _selectedProjectVersion, value))
            {
                OnPropertyChanged(nameof(DetailSelectionText));
            }
        }
    }

    private bool _isDetailOpen;

    /// <summary>是否处于项目详情视图。</summary>
    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        private set
        {
            if (!SetProperty(ref _isDetailOpen, value)) return;
            foreach (var name in new[] { nameof(ListVisible), nameof(DetailVisible),
                         nameof(DetailTitleText), nameof(DetailDescriptionText),
                         nameof(DetailIconUri), nameof(HasDetailIcon), nameof(DetailMetaText),
                         nameof(DetailVersionCountText), nameof(DetailSelectionText),
                         nameof(DetailKindText), nameof(DetailActionText), nameof(CanExpandDescription) })
            {
                OnPropertyChanged(name);
            }
        }
    }

    /// <summary>检索列表可见性（非详情状态）。</summary>
    public Microsoft.UI.Xaml.Visibility ListVisible =>
        IsDetailOpen ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>详情视图可见性。</summary>
    public Microsoft.UI.Xaml.Visibility DetailVisible =>
        IsDetailOpen ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>详情标题。</summary>
    public string DetailTitleText => SelectedProject?.DisplayTitle ?? "";

    /// <summary>详情简介。</summary>
    public string DetailDescriptionText => SelectedProject?.DisplayDescription ?? "";

    /// <summary>详情页项目图标。</summary>
    public Uri? DetailIconUri => SelectedProject?.IconUri;

    /// <summary>是否有项目图标。</summary>
    public bool HasDetailIcon => DetailIconUri is not null;

    /// <summary>项目类型中文名（模组 / 资源包 / 光影 / 整合包）。</summary>
    public string DetailKindText => SelectedSection switch
    {
        "Resourcepack" => "资源包",
        "Shader" => "光影",
        "Pack" => "整合包",
        _ => "模组",
    };

    /// <summary>详情副标题：类型 · 下载量。</summary>
    public string DetailMetaText
    {
        get
        {
            var project = SelectedProject;
            if (project is null) return DetailKindText;

            return project.Downloads > 0
                ? $"{DetailKindText}  ·  {project.DownloadsText}"
                : DetailKindText;
        }
    }

    /// <summary>版本分组数量文本。</summary>
    public string DetailVersionCountText =>
        ProjectVersionGroups.Count == 0 ? "" : $"共 {ProjectVersionGroups.Count} 个游戏版本";

    /// <summary>当前选中文件的摘要（版本号 · 通道 · 大小 · 日期）。</summary>
    public string DetailSelectionText
    {
        get
        {
            var version = SelectedProjectVersion;
            if (version is null) return "请选择要下载的版本";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(version.VersionNumber)) parts.Add($"v{version.VersionNumber}");
            parts.Add(version.ChannelText);
            if (!string.IsNullOrEmpty(version.SizeText)) parts.Add(version.SizeText);
            if (!string.IsNullOrEmpty(version.DateText)) parts.Add(version.DateText);
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>底部主按钮文字（随分区变化）。</summary>
    public string DetailActionText => SelectedSection switch
    {
        "Pack" => "安装整合包",
        "Resourcepack" => "下载并放入 resourcepacks",
        "Shader" => "下载并放入 shaderpacks",
        _ => "下载并放入 mods",
    };

    private bool _isDetailLoading;

    /// <summary>是否正在拉取版本列表。</summary>
    public bool IsDetailLoading
    {
        get => _isDetailLoading;
        private set
        {
            if (SetProperty(ref _isDetailLoading, value))
            {
                OnPropertyChanged(nameof(DetailLoadingVisibility));
                OnPropertyChanged(nameof(DetailListVisibility));
            }
        }
    }

    public Microsoft.UI.Xaml.Visibility DetailLoadingVisibility =>
        IsDetailLoading ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility DetailListVisibility =>
        IsDetailLoading ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    private bool _descriptionExpanded;

    /// <summary>简介是否展开（收起时最多显示 3 行）。</summary>
    public bool DescriptionExpanded
    {
        get => _descriptionExpanded;
        set
        {
            if (SetProperty(ref _descriptionExpanded, value))
            {
                OnPropertyChanged(nameof(DescriptionMaxLines));
                OnPropertyChanged(nameof(DescriptionToggleText));
            }
        }
    }

    public int DescriptionMaxLines => DescriptionExpanded ? 0 : 3;

    public string DescriptionToggleText => DescriptionExpanded ? "收起" : "展开全文";

    /// <summary>简介是否足够长、需要“展开全文”按钮。</summary>
    public bool CanExpandDescription => DetailDescriptionText.Length > 100;

    /// <summary>
    /// 进入项目详情：加载全部版本，按游戏版本分组（新的在上），
    /// 同一游戏版本的多个文件合并到同一分组，下拉框选择版本后显示文件列表。
    /// </summary>
    public async Task OpenDetailsAsync(ModrinthProjectItem project)
    {
        if (project is null || IsSearching) return;
        SelectedProject = project;
        IsDetailOpen = true;
        ProjectVersionGroups.Clear();
        GroupFiles.Clear();
        SelectedVersionGroup = null;
        DescriptionExpanded = false;
        IsDetailLoading = true;
        OnPropertyChanged(nameof(DetailVersionCountText));
        Status = "正在获取版本列表...";
        try
        {
            var versions = await ModrinthService.Instance.GetVersionsAsync(project.ProjectId);

            // 按游戏版本聚合
            var map = new Dictionary<string, VersionGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in versions)
            {
                foreach (var gv in v.GameVersions)
                {
                    if (!map.TryGetValue(gv, out var group))
                    {
                        group = new VersionGroup
                        {
                            GameVersion = gv,
                            Tag = IsSnapshotVersion(gv) ? "快照" : "正式版",
                        };
                        map[gv] = group;
                    }
                    group.Files.Add(v);
                }
            }

            // 新版本在上，快照与正式版按版本号排序
            foreach (var group in map.Values
                         .OrderByDescending(g => g, Comparer<VersionGroup>.Create(CompareGameVersions)))
            {
                ProjectVersionGroups.Add(group);
            }

            SelectedVersionGroup = ProjectVersionGroups.FirstOrDefault();
            OnPropertyChanged(nameof(DetailVersionCountText));
            Status = ProjectVersionGroups.Count == 0
                ? "该项目没有可用版本"
                : $"共 {ProjectVersionGroups.Count} 个游戏版本，选择版本与文件后用下方按钮下载";
        }
        catch (Exception ex)
        {
            Status = $"获取版本列表失败：{ex.Message}";
            LogService.Instance.Log($"获取版本列表失败：{ex}");
        }
        finally
        {
            IsDetailLoading = false;
        }
    }

    /// <summary>判断是否快照版本（含 snapshot / pre / rc / 周快照如 26w14a）。</summary>
    private static bool IsSnapshotVersion(string version)
    {
        var v = version.ToLowerInvariant();
        return v.Contains("snapshot") || v.Contains("-pre") || v.Contains("-rc") ||
               System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+w\d+[a-z]");
    }

    /// <summary>游戏版本比较：先按数字段比较，再正式版排在快照之后（同基础版本）。</summary>
    private static int CompareGameVersions(VersionGroup a, VersionGroup b)
    {
        static int[] Parts(string v)
        {
            var digits = System.Text.RegularExpressions.Regex.Matches(v, @"\d+");
            return digits.Select(m => int.TryParse(m.Value, out var n) ? n : 0).ToArray();
        }

        var x = Parts(a.GameVersion);
        var y = Parts(b.GameVersion);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        // 数字相同：正式版（非快照）更大
        var aSnap = a.Tag == "快照" ? 0 : 1;
        var bSnap = b.Tag == "快照" ? 0 : 1;
        return aSnap.CompareTo(bSnap);
    }

    /// <summary>退出详情返回列表。</summary>
    public void CloseDetails()
    {
        if (!IsDetailOpen) return;
        IsDetailOpen = false;
        ProjectVersionGroups.Clear();
        GroupFiles.Clear();
        SelectedVersionGroup = null;
        SelectedProjectVersion = null;
    }

    /// <summary>
    /// 把选中的版本文件下载到用户选择的文件夹。
    /// 默认目标目录：当前版本带加载器时用 versions/&lt;当前版本&gt;/mods（模组），否则 .minecraft/&lt;类型目录&gt;。
    /// </summary>
    public async Task InstallSelectedVersionToFolderAsync(string targetFolder)
    {
        if (SelectedProjectVersion is null || string.IsNullOrEmpty(targetFolder)) return;
        Status = "正在下载...";
        try
        {
            var progress = new Progress<string>(s => Status = s);
            await ModrinthService.Instance.DownloadVersionFileAsync(SelectedProjectVersion, targetFolder, progress);
            Status = $"已保存到：{targetFolder}";
            LogService.Instance.Log($"已下载 {SelectedProjectVersion.FileName} 到 {targetFolder}");
        }
        catch (Exception ex)
        {
            Status = $"下载失败：{ex.Message}";
            LogService.Instance.Log($"下载版本文件失败：{ex}");
        }
    }

    /// <summary>计算“浏览”对话框的默认起始目录。</summary>
    public string GetDefaultTargetFolder()
    {
        var mcDir = ConfigService.Instance.MinecraftDirectory;
        if (CurrentProjectType == "mod" && CurrentVersionHasLoader && !string.IsNullOrEmpty(CurrentVersionFolder))
        {
            var versionMods = Path.Combine(mcDir, "versions", CurrentVersionFolder, "mods");
            if (Directory.Exists(versionMods)) return versionMods;
        }
        var folder = CurrentProjectType switch
        {
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            "modpack" => "",
            _ => "mods",
        };
        var defaultDir = folder.Length == 0 ? mcDir : Path.Combine(mcDir, folder);
        Directory.CreateDirectory(defaultDir);
        return defaultDir;
    }

    /// <summary>安装选中的项目到对应目录（mods / resourcepacks / shaderpacks）。</summary>
    public async Task InstallSelectedProjectAsync()
    {
        if (SelectedProject is null || IsSearching) return;

        var type = SelectedSection switch
        {
            "Resourcepack" => "resourcepack",
            "Shader" => "shader",
            _ => "mod",
        };
        var targetDirName = type switch
        {
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            _ => "mods",
        };
        var targetDir = Path.Combine(ConfigService.Instance.MinecraftDirectory, targetDirName);

        IsSearching = true;
        Status = "正在安装...";
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var count = await ModrinthService.Instance.InstallLatestAsync(
                SelectedProject, type, targetDir, CurrentGameVersion, CurrentLoader, progress);
            Status = $"已安装到 {targetDirName}（{count} 个文件）";
            LogService.Instance.Log($"已安装 {SelectedProject.Title} 到 {targetDirName}（{count} 个文件）");
        }
        catch (Exception ex)
        {
            Status = $"安装失败：{ex.Message}";
            LogService.Instance.Log($"安装 {SelectedProject.Title} 失败：{ex}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    // ===================== 翻译 =====================

    private bool _isTranslating;

    /// <summary>是否正在翻译搜索结果。</summary>
    public bool IsTranslating
    {
        get => _isTranslating;
        private set => SetProperty(ref _isTranslating, value);
    }

    private bool _isTranslated;

    /// <summary>当前搜索结果是否已翻译（按钮切换为“显示原文”）。</summary>
    public bool IsTranslated
    {
        get => _isTranslated;
        private set
        {
            if (SetProperty(ref _isTranslated, value))
            {
                OnPropertyChanged(nameof(TranslateButtonText));
            }
        }
    }

    /// <summary>翻译按钮文字：未翻译显示“翻译”，已翻译显示“显示原文”。</summary>
    public string TranslateButtonText => IsTranslated ? "显示原文" : "翻译";

    /// <summary>
    /// 把当前搜索结果中的英文简介批量翻译成中文（一次请求多条，速度快）。
    /// 标题保持原文（模组名翻译容易失真，且翻译器会插入占位标记）。
    /// </summary>
    public async Task TranslateSearchResultsAsync()
    {
        if (IsTranslating || SearchResults.Count == 0) return;

        // 已翻译 → 恢复原文
        if (IsTranslated)
        {
            foreach (var item in SearchResults)
            {
                item.ApplyTranslation(null, null);
            }
            IsTranslated = false;
            Status = "已显示原文";
            return;
        }

        IsTranslating = true;
        Status = "正在翻译...";
        try
        {
            var items = SearchResults.ToList();
            var descriptions = items.Select(i => i.Description).ToList();
            var translations = await TranslationService.Instance.TranslateBatchToChineseAsync(descriptions);

            for (var i = 0; i < items.Count && i < translations.Count; i++)
            {
                if (translations[i] is { Length: > 0 } translated)
                {
                    items[i].ApplyTranslation(null, translated);
                }
            }
            IsTranslated = true;
            Status = "翻译完成，点击“显示原文”可切换回原文";
        }
        catch (Exception ex)
        {
            Status = $"翻译失败：{ex.Message}";
            LogService.Instance.Log($"翻译失败：{ex}");
        }
        finally
        {
            IsTranslating = false;
        }
    }

    /// <summary>可下载的整合包列表。</summary>
    public ObservableCollection<DownloadableVersion> Packs { get; } = new();

    /// <summary>官方版本全量列表（内部分类数据源）。</summary>
    private readonly List<DownloadableVersion> _allVanilla = new();

    /// <summary>按当前分类过滤后的官方版本列表（UI 绑定）。</summary>
    public ObservableCollection<DownloadableVersion> VanillaVersions { get; } = new();

    private string _vanillaCategory = "正式版";

    /// <summary>官方版本当前分类（正式版 / 快照 / 旧版）。</summary>
    public string VanillaCategory
    {
        get => _vanillaCategory;
        private set => SetProperty(ref _vanillaCategory, value);
    }

    /// <summary>切换官方版本分类并刷新列表。</summary>
    public void SetVanillaCategory(string category)
    {
        if (string.IsNullOrEmpty(category) || category == VanillaCategory) return;
        VanillaCategory = category;
        ApplyVanillaFilter();
    }

    /// <summary>按分类过滤官方版本。</summary>
    private void ApplyVanillaFilter()
    {
        VanillaVersions.Clear();
        foreach (var v in _allVanilla.Where(v => v.Category == VanillaCategory))
        {
            VanillaVersions.Add(v);
        }
        SelectedVanillaVersion = null;
        // 通知属性变更：x:Bind OneWay 会重新求值 ItemsSource，确保列表立即显示
        OnPropertyChanged(nameof(VanillaVersions));
    }

    private DownloadableVersion? _selectedPack;

    /// <summary>当前选中的整合包。</summary>
    public DownloadableVersion? SelectedPack
    {
        get => _selectedPack;
        set
        {
            if (SetProperty(ref _selectedPack, value)) OnPropertyChanged(nameof(CanDownloadPack));
        }
    }

    /// <summary>是否可下载所选整合包。</summary>
    public bool CanDownloadPack => SelectedPack?.CanDownload == true;

    private DownloadableVersion? _selectedVanillaVersion;

    /// <summary>当前选中的官方原版版本。</summary>
    public DownloadableVersion? SelectedVanillaVersion
    {
        get => _selectedVanillaVersion;
        set
        {
            if (SetProperty(ref _selectedVanillaVersion, value)) OnPropertyChanged(nameof(CanDownloadVanilla));
        }
    }

    /// <summary>是否可下载所选官方原版版本。</summary>
    public bool CanDownloadVanilla => SelectedVanillaVersion?.CanDownload == true;

    private bool _isDownloading;

    /// <summary>是否正在下载。</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    private string _status = "就绪";

    /// <summary>下载状态文字。</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusVisibility));
            }
        }
    }

    /// <summary>状态文字可见性：空状态不占位。</summary>
    public Microsoft.UI.Xaml.Visibility StatusVisibility =>
        string.IsNullOrEmpty(Status) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>下载地址配置状态提示（原版下载源）。</summary>
    public string RepoInfo
    {
        get
        {
            var settings = _settingsService.Load();
            var vanillaSource = settings.VanillaDownloadSource == VanillaSourceService.SourceMojang
                ? "Mojang 官方"
                : "BMCLAPI 镜像";
            return $"官方版本下载源：{vanillaSource}（可在“网络设置”中切换）。";
        }
    }

    /// <summary>刷新版本列表命令。</summary>
    public ICommand RefreshCommand { get; }

    public VersionDownloadViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    /// <summary>加载可下载版本列表（整合包 / 官方版本全量，官方版本按分类过滤展示）。</summary>
    public async Task LoadAsync()
    {
        Packs.Clear();
        _allVanilla.Clear();
        VanillaVersions.Clear();
        try
        {
            OnPropertyChanged(nameof(RepoInfo));
            Status = "正在获取版本列表...";

            // 缓存当前版本文件夹名与对应的 Minecraft 版本号（筛选用版本号，不是文件夹名）
            try
            {
                CurrentVersionFolder = (await ConfigService.Instance.LoadAsync()).GameVersion ?? "";
            }
            catch { CurrentVersionFolder = ""; }
            CurrentGameVersion = string.IsNullOrEmpty(CurrentVersionFolder)
                ? ""
                : ConfigService.Instance.GetVersionMinecraftVersion(CurrentVersionFolder);

            var list = await _downloadService.GetVersionListAsync();
            foreach (var v in list)
            {
                if (v.DownloadUrl.StartsWith("vanilla://", StringComparison.OrdinalIgnoreCase))
                {
                    _allVanilla.Add(v);
                }
                else
                {
                    Packs.Add(v);
                }
            }
            ApplyVanillaFilter();
            RebuildFilterOptions();
            Status = "";
        }
        catch (Exception ex)
        {
            Status = $"获取版本列表失败：{ex.Message}";
            LogService.Instance.Log($"获取版本列表失败：{ex}");
        }
    }

    /// <summary>
    /// 只把所选整合包的源文件下载到指定路径（不安装）。
    /// </summary>
    public async Task SavePackSourceFileAsync(DownloadableVersion version, string destPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken? cancellationToken = null,
        Action<string>? onStatus = null)
    {
        Status = $"正在下载 {version.MainText} 的源文件...";
        try
        {
            await _downloadService.SaveSourceFileAsync(version, destPath, progress, cancellationToken, onStatus);
            Status = $"源文件已保存到：{destPath}";
            LogService.Instance.Log($"整合包源文件已保存：{destPath}");
        }
        catch (OperationCanceledException)
        {
            Status = "下载已取消";
            throw;
        }
        catch (Exception ex)
        {
            Status = $"下载源文件失败：{ex.Message}";
            LogService.Instance.Log($"下载整合包源文件失败：{ex}");
            throw;
        }
    }

    /// <summary>
    /// 安装所选 Modrinth 整合包的指定版本：下载 .mrpack → 解压 → 按 Modrinth 清单安装依赖与加载器 → 设为当前版本。
    /// </summary>
    public async Task<bool> InstallSelectedPackAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken? cancellationToken = null,
        Action<string>? onStatus = null)
    {
        var project = SelectedProject;
        var version = SelectedProjectVersion;
        if (project is null || version is null)
        {
            Status = "请先选择一个整合包版本。";
            return false;
        }

        var mcDir = ConfigService.Instance.MinecraftDirectory;
        var cacheDir = Path.Combine(mcDir, ".aclauncher_cache");
        var unpackDir = Path.Combine(cacheDir, "pack_" + Guid.NewGuid().ToString("N")[..8]);
        var packFile = Path.Combine(cacheDir, version.FileName);

        var versionsDir = Path.Combine(mcDir, "versions");
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(versionsDir))
            {
                foreach (var d in Directory.GetDirectories(versionsDir))
                {
                    if (Path.GetFileName(d) is { Length: > 0 } n) before.Add(n);
                }
            }
        }
        catch { /* 快照失败跳过清理 */ }

        IsSearching = true;
        Status = "正在下载整合包...";
        try
        {
            Directory.CreateDirectory(cacheDir);
            var task = new DownloadTask
            {
                Urls = new List<string> { version.FileUrl },
                LocalPath = packFile,
                ExpectedSha1 = version.Sha1,
                DisplayName = version.FileName,
            };
            var ok = await DownloadService.Instance.DownloadOneAsync(task, progress, cancellationToken);
            if (!ok) throw new IOException("整合包下载失败。");

            onStatus?.Invoke("正在解压整合包...");
            Directory.CreateDirectory(unpackDir);
            await SevenZipService.Instance.ExtractAsync(packFile, unpackDir);

            onStatus?.Invoke("正在安装整合包...");
            var installedVersion = await PackInstallService.Instance.TryInstallPackAsync(unpackDir, project.DisplayTitle);
            if (string.IsNullOrEmpty(installedVersion))
            {
                throw new InvalidOperationException("该整合包格式暂不支持自动安装，请改用“下载源文件”。");
            }

            var cfg = await ConfigService.Instance.LoadAsync();
            cfg.GameVersion = installedVersion;
            await ConfigService.Instance.SaveAsync(cfg);

            // 整合包里没有图标时，用 Modrinth 项目图标作为该版本的图标
            if (VersionIconService.GetPackIconPath(installedVersion, versionsDir) is null && project.IconUri is { } iconUri)
            {
                await VersionIconService.TryDownloadIconAsync(iconUri, ConfigService.Instance.MinecraftDirectory, installedVersion);
            }

            Status = $"整合包已安装并设为当前版本：{installedVersion}";
            LogService.Instance.Log($"Modrinth 整合包安装完成：{project.DisplayTitle} → {installedVersion}");
            await LoadAsync();
            await RefreshCurrentVersionAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "安装已取消";
            CleanupNewVersions(versionsDir, before);
            return false;
        }
        catch (Exception ex)
        {
            Status = $"安装失败：{ex.Message}";
            LogService.Instance.Log($"安装 Modrinth 整合包失败：{ex}");
            CleanupNewVersions(versionsDir, before);
            return false;
        }
        finally
        {
            IsSearching = false;
            try { if (File.Exists(packFile)) File.Delete(packFile); } catch { /* 忽略 */ }
            try { if (Directory.Exists(unpackDir)) Directory.Delete(unpackDir, true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>删除本次安装新产生的版本目录（失败 / 取消时清理半成品）。</summary>
    private static void CleanupNewVersions(string versionsDir, HashSet<string> before)
    {
        try
        {
            if (!Directory.Exists(versionsDir)) return;
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || before.Contains(name)) continue;
                try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
    }

    /// <summary>设置状态文字（供页面在交互前提示）。</summary>
    public void SetStatus(string text) => Status = text;

    /// <summary>
    /// 下载所选版本并设为当前版本（供页面弹出进度对话框调用）。
    /// </summary>
    /// <param name="version">要下载的版本。</param>
    /// <param name="progress">下载进度回调。</param>
    /// <param name="cancellationToken">取消令牌（用户点击取消按钮时触发）。</param>
    public async Task<bool> DownloadAsync(DownloadableVersion version,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken? cancellationToken = null,
        Action<string>? onStatus = null)
    {
        if (version is null || !version.CanDownload)
        {
            Status = "请先选择一个可下载的版本。";
            return false;
        }

        IsDownloading = true;
        try
        {
            Status = $"正在下载版本 {version.Id}...";
            await _downloadService.DownloadVersionAsync(version, progress, cancellationToken, onStatus);
            Status = $"版本 {version.Id} 下载完成，已设为当前版本";
            await LoadAsync(); // 刷新已安装标记
            await RefreshCurrentVersionAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "下载已取消";
            LogService.Instance.Log("下载已取消。");
            return false;
        }
        catch (Exception ex)
        {
            Status = $"下载失败：{ex.Message}";
            LogService.Instance.Log($"下载版本失败：{ex}");
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }
}

/// <summary>详情页：一个游戏版本分组（同名版本的多个文件合并在 Files 中）。</summary>
public class VersionGroup
{
    public string GameVersion { get; init; } = "";
    public string Tag { get; init; } = "正式版";
    public List<ModrinthVersionItem> Files { get; } = new();

    /// <summary>下拉框展示文本，如 “Minecraft 26.3（快照）”。</summary>
    public string Display => $"Minecraft {GameVersion}（{Tag}）";

    public override string ToString() => Display;
}
