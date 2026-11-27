using System.Collections.ObjectModel;
using System.Windows.Input;
using ACLauncher.Models;
using ACLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ACLauncher.ViewModels;

/// <summary>
/// 主页视图模型：版本信息、游戏目录、当前账户、启动状态与启动命令。
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly ConfigService _configService = ConfigService.Instance;
    private readonly AuthService _authService = AuthService.Instance;
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private readonly GameLaunchService _launchService = GameLaunchService.Instance;
    private readonly DispatcherQueue _dispatcher;

    private string _gameVersion = "未知";
    private string _minecraftDirectory = "";
    private string _status = "就绪";
    private bool _isLaunching;
    private bool _hasAnyVersion;
    private double _launchProgress;
    private bool _isProgressIndeterminate = true;
    private CancellationTokenSource? _launchCts;

    /// <summary>请求导航到其他页面的回调（由 MainWindow 订阅：主页按钮跳转版本相关页面）。</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>当前配置锁定的游戏版本（只读展示，来自 config.json）。</summary>
    public string GameVersion
    {
        get => _gameVersion;
        private set => SetProperty(ref _gameVersion, value);
    }

    /// <summary>.minecraft 游戏目录绝对路径（只读展示）。</summary>
    public string MinecraftDirectory
    {
        get => _minecraftDirectory;
        private set => SetProperty(ref _minecraftDirectory, value);
    }

    /// <summary>状态文字（就绪 / 正在解析... / 游戏运行中 / 错误信息）。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>是否正在启动（用于禁用启动按钮）。窗口弹出前保持为 true。</summary>
    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (SetProperty(ref _isLaunching, value))
            {
                // 同步刷新启动按钮的可执行状态，使其真正变灰/恢复
                (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ProgressVisibility));
                OnPropertyChanged(nameof(CancelButtonVisibility));
                if (value)
                {
                    // 新一轮启动：进度归零并切到不定态，直到资源下载给出确定进度
                    LaunchProgress = 0;
                    IsProgressIndeterminate = true;
                }
            }
        }
    }

    /// <summary>启动进度（0-100），资源补全 / 运行时下载阶段有效。</summary>
    public double LaunchProgress
    {
        get => _launchProgress;
        private set => SetProperty(ref _launchProgress, value);
    }

    /// <summary>进度条是否为不定态（解析 / 构建参数等无法量化的阶段）。</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    /// <summary>启动进度条可见性：仅启动过程中显示。</summary>
    public Visibility ProgressVisibility =>
        IsLaunching ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>“取消启动”按钮可见性：仅启动过程中显示。</summary>
    public Visibility CancelButtonVisibility =>
        IsLaunching ? Visibility.Visible : Visibility.Collapsed;

    private bool _isGameRunning;

    /// <summary>游戏窗口已弹出、进程正在运行（控制右下角“强制结束”按钮显隐）。</summary>
    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (SetProperty(ref _isGameRunning, value))
            {
                OnPropertyChanged(nameof(KillButtonVisibility));
            }
        }
    }

    /// <summary>右下角“强制结束”圆形按钮可见性：仅游戏运行时出现。</summary>
    public Visibility KillButtonVisibility =>
        IsGameRunning ? Visibility.Visible : Visibility.Collapsed;

    // ===================== 服务器状态卡 =====================

    private bool _serverOnline;
    private string _serverStatusText = "正在检测...";
    private string _serverPlayersText = "-- / --";
    private string _serverMotdText = "";
    private string _serverVersionName = "";

    /// <summary>服务器是否在线（状态点颜色的依据）。</summary>
    public bool ServerOnline
    {
        get => _serverOnline;
        private set
        {
            if (SetProperty(ref _serverOnline, value))
            {
                OnPropertyChanged(nameof(ServerOnlineDotVisibility));
                OnPropertyChanged(nameof(ServerOfflineDotVisibility));
            }
        }
    }

    /// <summary>状态行文字（在线 · 23ms / 离线或无法连接 / 正在检测...）。</summary>
    public string ServerStatusText { get => _serverStatusText; private set => SetProperty(ref _serverStatusText, value); }

    /// <summary>在线人数文字（如 “12 / 50”）。</summary>
    public string ServerPlayersText { get => _serverPlayersText; private set => SetProperty(ref _serverPlayersText, value); }

    /// <summary>服务器 MOTD（纯文本，可能为空）。</summary>
    public string ServerMotdText { get => _serverMotdText; private set => SetProperty(ref _serverMotdText, value); }

    /// <summary>服务器游戏版本名（如 “26.2”）。</summary>
    public string ServerVersionName { get => _serverVersionName; private set => SetProperty(ref _serverVersionName, value); }

    /// <summary>在线状态点可见性（绿色）。</summary>
    public Visibility ServerOnlineDotVisibility =>
        ServerOnline ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>离线状态点可见性（红色）。</summary>
    public Visibility ServerOfflineDotVisibility =>
        ServerOnline ? Visibility.Collapsed : Visibility.Visible;

    // ===================== 当前版本卡标签 =====================

    private string _loaderTagText = "";
    private string _modTagText = "";

    /// <summary>加载器标签（如 “Fabric 0.19.5”），解析不出为空隐藏。</summary>
    public string LoaderTagText
    {
        get => _loaderTagText;
        private set { if (SetProperty(ref _loaderTagText, value)) OnPropertyChanged(nameof(LoaderTagVisibility)); }
    }

    public Visibility LoaderTagVisibility =>
        string.IsNullOrEmpty(LoaderTagText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Mod 数量标签（“29 个 Mod”），目录不存在则隐藏。</summary>
    public string ModTagText
    {
        get => _modTagText;
        private set { if (SetProperty(ref _modTagText, value)) OnPropertyChanged(nameof(ModTagVisibility)); }
    }

    public Visibility ModTagVisibility =>
        string.IsNullOrEmpty(ModTagText) ? Visibility.Collapsed : Visibility.Visible;

    // ===================== Java 环境卡 =====================

    private string _javaSummaryText = "正在检测本机 Java...";

    /// <summary>本机 Java 检测摘要（如 “已找到 Java 21、Java 25”）。</summary>
    public string JavaSummaryText { get => _javaSummaryText; private set => SetProperty(ref _javaSummaryText, value); }

    // ===================== 日志速览卡 =====================

    /// <summary>最近日志行（最多 5 条，随日志事件实时滚动）。</summary>
    public ObservableCollection<string> LogPreviewLines { get; } = new();

    /// <summary>当前账户玩家名。</summary>
    public string CurrentPlayer => _authService.CurrentAccount?.PlayerName ?? "未登录";

    /// <summary>当前账户类型。</summary>
    public string CurrentAccountType => _authService.CurrentAccount?.TypeDisplay ?? "请先在“账户”页登录";

    private string? _currentHeadImagePath;

    /// <summary>当前账户头像的本地路径（启动坞显示用，异步加载）。</summary>
    public string? CurrentHeadImagePath
    {
        get => _currentHeadImagePath;
        private set => SetProperty(ref _currentHeadImagePath, value);
    }

    /// <summary>为当前账户加载头像（失败保持为空，界面显示占位图标）。</summary>
    private async Task RefreshCurrentHeadAsync()
    {
        var account = _authService.CurrentAccount;
        if (account is null)
        {
            _dispatcher.TryEnqueue(() => CurrentHeadImagePath = null);
            return;
        }
        try
        {
            var path = await SkinService.Instance.GetHeadImagePathAsync(account);
            _dispatcher.TryEnqueue(() => CurrentHeadImagePath = path);
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"加载当前账户头像失败：{ex.Message}");
        }
    }

    /// <summary>欢迎行主文字（随账户变化）。</summary>
    public string WelcomeText => $"欢迎回来，{CurrentPlayer}";

    /// <summary>是否已安装任意版本（决定"游戏版本"行按钮是"去下载"还是"选择版本"）。</summary>
    public bool HasAnyVersion
    {
        get => _hasAnyVersion;
        private set
        {
            if (SetProperty(ref _hasAnyVersion, value))
            {
                OnPropertyChanged(nameof(VersionButtonText));
            }
        }
    }

    /// <summary>版本按钮文字：无版本 →"去下载"；已有版本 →"选择版本"。</summary>
    public string VersionButtonText => HasAnyVersion ? "选择版本" : "去下载";

    /// <summary>启动游戏命令。</summary>
    public ICommand LaunchCommand { get; }

    /// <summary>取消启动命令（启动过程中可点，中止解析 / 下载 / 等待窗口）。</summary>
    public ICommand CancelLaunchCommand { get; }

    /// <summary>强制结束游戏命令（右下角圆形按钮）。</summary>
    public ICommand KillCommand { get; }

    /// <summary>版本按钮命令（跳转"选择版本"或"版本下载"页面）。</summary>
    public ICommand VersionButtonCommand { get; }

    /// <summary>刷新服务器状态命令（服务器卡右上角按钮）。</summary>
    public ICommand RefreshServerStatusCommand { get; }

    /// <summary>查看 Java 详情（跳转设置页）。</summary>
    public ICommand JavaDetailCommand { get; }

    /// <summary>查看全部日志（跳转日志页）。</summary>
    public ICommand OpenLogCommand { get; }

    /// <summary>切换账户（启动坞账户胶囊点击，跳转账户页）。</summary>
    public ICommand OpenAccountCommand { get; }

    public MainViewModel()
    {
        // ViewModel 在 UI 线程创建，可直接获取当前线程的 DispatcherQueue
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => !IsLaunching);
        CancelLaunchCommand = new RelayCommand(() => _launchService.CancelLaunch());
        KillCommand = new RelayCommand(() => _launchService.KillGame());
        VersionButtonCommand = new RelayCommand(() =>
        {
            NavigateRequested?.Invoke(HasAnyVersion ? "Select" : "Download");
        });
        RefreshServerStatusCommand = new RelayCommand(() => _ = RefreshServerStatusAsync());
        JavaDetailCommand = new RelayCommand(() => NavigateRequested?.Invoke("Settings"));
        OpenLogCommand = new RelayCommand(() => NavigateRequested?.Invoke("Log"));
        OpenAccountCommand = new RelayCommand(() => NavigateRequested?.Invoke("Account"));

        // 订阅启动状态变化（可能在后台线程触发，转发到 UI 线程）
        _launchService.StatusChanged += status => _dispatcher.TryEnqueue(() => Status = status);

        // 订阅启动进度变化（null = 不定态；0~1 = 资源下载确定进度）
        _launchService.LaunchProgressChanged += progress => _dispatcher.TryEnqueue(() =>
        {
            if (progress is null)
            {
                IsProgressIndeterminate = true;
            }
            else
            {
                IsProgressIndeterminate = false;
                LaunchProgress = Math.Round(progress.Value * 100, 1);
            }
        });

        // 订阅游戏运行状态（窗口弹出显示强制结束按钮，进程退出隐藏）
        _launchService.GameRunningChanged += running =>
            _dispatcher.TryEnqueue(() => IsGameRunning = running);

        // 订阅账户变化（登录 / 切换 / 删除后刷新主页账户信息与欢迎行）
        _authService.AccountsChanged += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(CurrentPlayer));
            OnPropertyChanged(nameof(CurrentAccountType));
            OnPropertyChanged(nameof(WelcomeText));
            _ = RefreshCurrentHeadAsync();
        });

        // 订阅日志行：主页日志速览卡保留最近 5 条（事件在后台线程触发，转发 UI 线程）
        LogService.Instance.LineAdded += line => _dispatcher.TryEnqueue(() =>
        {
            LogPreviewLines.Add(line);
            while (LogPreviewLines.Count > 12)
            {
                LogPreviewLines.RemoveAt(0);
            }
        });
    }

    /// <summary>
    /// 初始化：读取 config.json 并展示版本号与游戏目录。
    /// 配置不存在或格式错误时在状态栏给出中文提示并阻止启动。
    /// </summary>
    public async Task InitializeAsync()
    {
        MinecraftDirectory = _configService.MinecraftDirectory;

        try
        {
            var config = await _configService.LoadAsync();
            GameVersion = config.GameVersion ?? "未知";
            Status = "就绪";
        }
        catch (Exception ex)
        {
            GameVersion = "未知";
            Status = $"配置错误：{ex.Message}";
            LogService.Instance.Log($"配置加载失败：{ex.Message}");
        }

        // 刷新"是否已安装版本"（决定主页版本按钮是"去下载"还是"选择版本"）
        HasAnyVersion = _configService.GetInstalledVersions().Count > 0;

        // 后台填充信息卡：服务器状态 / 版本标签 / Java 摘要 / 账户头像（不阻塞主页显示）
        _ = RefreshServerStatusAsync();
        _ = RefreshVersionTagsAsync();
        _ = RefreshJavaSummaryAsync();
        _ = RefreshCurrentHeadAsync();

        // 日志速览：先带出已有日志的最后 5 条
        foreach (var line in LogService.Instance.Lines.TakeLast(12))
        {
            LogPreviewLines.Add(line);
        }
        while (LogPreviewLines.Count > 12)
        {
            LogPreviewLines.RemoveAt(0);
        }
    }

    /// <summary>查询服务器在线状态并刷新服务器卡（网络操作，不抛异常）。</summary>
    public async Task RefreshServerStatusAsync()
    {
        ServerStatusText = "正在检测...";
        ServerPlayersText = "-- / --";
        // 查询端点与展示域名分离：界面始终显示 mc.ambercat.top
        var status = await ServerStatusService.Instance.QueryAsync(
            ServerStatusService.DefaultQueryHost, ServerStatusService.DefaultQueryPort);
        _dispatcher.TryEnqueue(() =>
        {
            ServerOnline = status.Online;
            ServerStatusText = status.Online ? $"在线 · {status.LatencyMs}ms" : "离线或无法连接";
            ServerPlayersText = status.Online && status.PlayersOnline >= 0
                ? $"{status.PlayersOnline} / {status.PlayersMax}"
                : "-- / --";
            ServerMotdText = status.Motd;
            ServerVersionName = status.VersionName;
        });
    }

    /// <summary>读取当前版本信息，填充版本卡的标签（加载器 / Mod 数量）。</summary>
    private Task RefreshVersionTagsAsync()
    {
        var version = GameVersion;
        if (string.IsNullOrEmpty(version) || version == "未知") return Task.CompletedTask;

        var versionDir = Path.Combine(_configService.MinecraftDirectory, "versions", version);

        // 加载器：从版本目录名解析（如 26.2-fabric-0.19.5 → Fabric 0.19.5），无加载器显示“原版”
        LoaderTagText = ParseLoaderTag(version);

        // Mod 数量：实扫版本目录下的 mods（整合包的 mod 在版本目录内）
        var modsDir = Path.Combine(versionDir, "mods");
        if (Directory.Exists(modsDir))
        {
            var count = Directory.EnumerateFiles(modsDir, "*.jar").Count();
            if (count > 0) ModTagText = $"{count} 个 Mod";
        }

        return Task.CompletedTask;
    }

    /// <summary>从版本目录名解析模组加载器与版本；没有加载器时返回“原版”。</summary>
    private static string ParseLoaderTag(string version)
    {
        static bool TrySlice(string text, string prefix, out string rest)
        {
            var index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                rest = "";
                return false;
            }
            rest = text[(index + prefix.Length)..];
            return rest.Length > 0;
        }

        // neoforge 必须排在 forge 之前判断（否则 "neoforge-" 会被 "forge-" 误匹配）
        if (TrySlice(version, "neoforge-", out var neo)) return "NeoForge " + neo;
        if (TrySlice(version, "fabric-", out var fabric)) return "Fabric " + fabric;
        if (TrySlice(version, "quilt-", out var quilt)) return "Quilt " + quilt;
        if (TrySlice(version, "forge-", out var forge)) return "Forge " + forge;
        return "原版";
    }

    /// <summary>检测本机 Java 并刷新 Java 环境卡摘要。</summary>
    private async Task RefreshJavaSummaryAsync()
    {
        try
        {
            var javas = await JavaService.Instance.DetectAsync();
            var majors = javas
                .Select(j => new string(j.Version.TakeWhile(char.IsDigit).ToArray()))
                .Where(s => int.TryParse(s, out _))
                .Distinct()
                .OrderByDescending(int.Parse)
                .ToList();

            _dispatcher.TryEnqueue(() =>
            {
                JavaSummaryText = majors.Count > 0
                    ? $"已找到 Java {string.Join("、", majors)}"
                    : "未检测到本机 Java，启动时将自动下载所需运行时";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => JavaSummaryText = $"Java 检测失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动游戏前做完整检查，然后交给 GameLaunchService。
    /// </summary>
    private async Task LaunchAsync()
    {
        // 记录点击前的小字，窗口弹出后恢复为它
        var previousStatus = Status;
        IsLaunching = true;
        _launchCts = new CancellationTokenSource();
        try
        {
            // 1. 配置检查（重新读取，保证最新且有效）
            Config config;
            try
            {
                config = await _configService.LoadAsync();
                GameVersion = config.GameVersion ?? "未知";
            }
            catch (Exception ex)
            {
                Status = $"配置错误：{ex.Message}";
                return;
            }

            if (!_configService.IsMinecraftDirectoryValid())
            {
                Status = $"未找到 .minecraft 目录：{_configService.MinecraftDirectory}";
                return;
            }

            // 2. 账户检查
            var account = _authService.CurrentAccount;
            if (account is null)
            {
                Status = "尚未登录账户，请前往“账户”页添加或切换账户";
                return;
            }

            // 3. 账户令牌检查：Microsoft 临近过期自动刷新，外置登录（Yggdrasil）先校验再续期
            if (account.Type is AccountType.Microsoft or AccountType.Yggdrasil)
            {
                Status = "正在检查账户令牌...";
                try
                {
                    await _authService.EnsureValidAsync(account);
                }
                catch (Exception ex)
                {
                    Status = $"账户令牌刷新失败：{ex.Message}";
                    return;
                }
            }

            // 4. Java 检查（手动配置优先于自动检测）
            var settings = _settingsService.Load();
            var javaPath = settings.EffectiveJavaPath;
            if (string.IsNullOrEmpty(javaPath))
            {
                Status = "未配置 Java 路径，请前往“设置”页配置";
                return;
            }

            // 5. 组装启动选项并启动（后续状态由 GameLaunchService 事件驱动）
            var options = new LaunchOptions
            {
                Version = config.GameVersion!,
                Account = account,
                JavaPath = javaPath,
                MinMemoryMB = settings.EffectiveMinMemoryMB,
                MaxMemoryMB = settings.EffectiveMaxMemoryMB,
                // 自定义启动参数由启动器内置管理（默认连接服务器 mc.ambercat.top），不再读取用户设置
                ExtraJvmArgs = "",
                ExtraGameArgs = "",
                GamePriority = settings.GamePriority,
                CheckResources = settings.CheckResources,
            };

            // 该方法会一直等待到游戏窗口弹出（或进程提前退出 / 用户取消）才返回
            var started = await _launchService.LaunchAsync(options, _launchCts.Token);

            // 窗口已弹出：恢复按钮颜色并把小字恢复为点击前的内容；
            // 若进程在窗口弹出前退出，保留 GameLaunchService 推送的“游戏已退出”信息。
            if (started)
            {
                Status = previousStatus;
            }
        }
        catch (Exception ex)
        {
            Status = $"启动失败：{ex.Message}";
            LogService.Instance.Log($"启动失败：{ex}");
        }
        finally
        {
            _launchCts?.Dispose();
            _launchCts = null;
            IsLaunching = false;
        }
    }
}
