using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ACLauncher;

/// <summary>
/// 应用程序入口。
/// </summary>
public partial class App : Application
{
    /// <summary>主窗口引用（供全局访问，例如主题切换、文件选择器获取句柄）。</summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// 显式 Main：unpackaged（非 MSIX 打包）WinUI 3 应用。
    /// 自包含部署（WindowsAppSDKSelfContained=true）时无需 Bootstrap —— 运行时 DLL 随程序分发、
    /// 由应用本地解析；此处仍尝试初始化以兼容框架依赖模式，失败不阻断启动。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            // Windows App SDK 2.0 对应 majorMinorVersion = 0x00020000
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(0x00020000);
        }
        catch (Exception ex)
        {
            // 自包含模式下没有已安装的运行时，Bootstrap 失败属正常，继续启动（本地 DLL 会被加载）
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "bootstrap-info.txt"),
                    $"Bootstrap 未初始化（自包含模式正常现象）：\n{ex.Message}");
            }
            catch { }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}");
            }
            catch { }
            throw;
        }
    }

    public App()
    {
        // 未处理异常落盘（WinUI 的 stowed exception 不会进日志，崩溃排查靠这份文件）
        UnhandledException += (_, e) =>
        {
            WriteCrashLog("XamlUnhandled", e.Exception);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain", e.ExceptionObject as Exception);

        // 应用级主题：Application.RequestedTheme 只能在启动时确定，
        // 运行时切换由 ApplyTheme 通过根元素 RequestedTheme + 画刷换色实现。
        try
        {
            var theme = Services.SettingsService.Instance.Load().Theme;
            var useDark = theme switch
            {
                "暗色" => true,
                "亮色" => false,
                _ => !IsSystemLightTheme(),
            };
            RequestedTheme = useDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
        catch
        {
            RequestedTheme = ApplicationTheme.Light;
        }

        InitializeComponent();

        // 注意：构造函数内访问 Application.Resources 会抛 COMException，
        // 配色在 OnLaunched 中写入（见 ApplyPalette）。
    }

    /// <summary>
    /// 写入自定义画刷的颜色（浅色 / 深色两套）。因为画刷是单实例，
    /// 直接修改 Color 会让所有引用它的控件立即刷新，无需重建页面。
    /// </summary>
    public static void ApplyPalette(bool dark)
    {
        try
        {
            if (Current is not null) ApplyPaletteTo(Current.Resources, dark);
        }
        catch (Exception ex)
        {
            Services.LogService.Instance.Log($"切换主题配色失败：{ex.Message}");
        }
    }

    /// <summary>把未处理异常写到程序目录的 crash.log（多次崩溃追加）。</summary>
    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\r\n{ex}\r\n\r\n";
            File.AppendAllText(path, text);
        }
        catch
        {
            // 写日志本身失败时不再抛出，避免掩盖原始崩溃
        }
    }

    /// <summary>自定义配色画刷（单实例，切换主题时只改 Color）。</summary>
    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.SolidColorBrush> PaletteBrushes = new();

    private static void ApplyPaletteTo(ResourceDictionary resources, bool dark)
    {
        void Set(string key, string light, string darkColor)
        {
            var color = ParseColor(dark ? darkColor : light);
            if (PaletteBrushes.TryGetValue(key, out var brush))
            {
                brush.Color = color;
                return;
            }

            // 首次调用：用自己持有的画刷实例替换资源条目，保证后续改色能同步到所有引用
            brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            resources[key] = brush;
            PaletteBrushes[key] = brush;
        }

        // 彩色（深色下提亮，保持品牌橙观感一致）
        Set("McOrangeDeepBrush", "#C25E0E", "#F0A45E");
        Set("McOrangeFaintBrush", "#FDF4E9", "#3A2A1C");
        Set("TealAccentBrush", "#0E7C7B", "#4FC1BF");
        Set("WarmCreamBrush", "#FBF4EC", "#262019");
        Set("InfoTagBorderBrush", "#F3D9BC", "#5A3A1E");
        Set("InfoTagTextBrush", "#C25E0E", "#F0A45E");

        // 表面层级（由深到浅）：窗口底色 → 导航侧栏 → 卡片 / 对话框
        Set("SolidBackgroundFillColorBaseBrush", "#F3F3F3", "#1B1713");
        Set("SolidBackgroundFillColorSecondaryBrush", "#EEEEEE", "#221D18");
        Set("NavigationViewExpandedPaneBackground", "#FBF4EC", "#262019");
        Set("NavigationViewContentBackground", "#F3F3F3", "#1B1713");
        Set("CardBackgroundFillColorDefaultBrush", "#FFFFFF", "#2A231D");
        Set("CardStrokeColorDefaultBrush", "#E6E0D8", "#3A322A");
    }

    /// <summary>#RRGGBB / #AARRGGBB → Color。</summary>
    private static Windows.UI.Color ParseColor(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        var value = Convert.ToUInt32(text, 16);
        return new Windows.UI.Color
        {
            A = (byte)((value >> 24) & 0xFF),
            R = (byte)((value >> 16) & 0xFF),
            G = (byte)((value >> 8) & 0xFF),
            B = (byte)(value & 0xFF),
        };
    }

    /// <summary>读取系统「应用模式」是否为浅色（用于“跟随系统”）。</summary>
    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i != 0;
        }
        catch
        {
            return true;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 0. 按启动主题写入自定义画刷颜色
        //    （构造函数里访问 Application.Resources 会抛 COMException，必须等到这里）
        try
        {
            ApplyPaletteTo(Resources, RequestedTheme == ApplicationTheme.Dark);
        }
        catch (Exception ex)
        {
            Services.LogService.Instance.Log($"初始化主题配色失败：{ex.Message}");
        }

        // 1. 启动时自动加载已保存的账户（实现"自动登录"）
        try
        {
            await Services.AuthService.Instance.LoadAsync();
        }
        catch (Exception ex)
        {
            Services.LogService.Instance.Log($"加载账户失败：{ex.Message}");
        }

        // 2. 创建主窗口（构造时已读取并应用了主题）
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>避免连续触发多次重启。</summary>
    private static bool _restarting;

    /// <summary>
    /// 主题切换：Application.RequestedTheme 启动后无法修改（会抛 COMException），
    /// WinUI 又不会重新解析已加载控件的模板画刷，因此最可靠的做法是保存设置后自动重启启动器。
    /// 新旧主题一致时不做任何事。
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        if (_restarting) return;

        var useDark = theme switch
        {
            "暗色" => true,
            "亮色" => false,
            _ => !IsSystemLightTheme(),
        };
        var currentDark = Current is null || Current.RequestedTheme == ApplicationTheme.Dark;
        if (useDark == currentDark) return;

        _restarting = true;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Services.LogService.Instance.Log("无法确定启动器路径，请手动重启以应用主题。");
                _restarting = false;
                return;
            }

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Services.LogService.Instance.Log($"主题已切换为（{theme}），正在重启启动器...");

            // 给新进程一点启动时间，再退出当前实例
            System.Threading.Thread.Sleep(300);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            _restarting = false;
            Services.LogService.Instance.Log($"重启启动器失败：{ex.Message}，请手动重启以应用主题。");
        }
    }
}
