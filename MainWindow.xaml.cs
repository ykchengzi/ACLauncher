using ACLauncher.ViewModels;
using ACLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ACLauncher;

/// <summary>
/// 主窗口：负责左侧导航与各页面切换。
/// 四个页面对应四个共享的 ViewModel（页面间数据互通）。
/// </summary>
public sealed partial class MainWindow : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private Microsoft.UI.Windowing.AppWindow? _appWindow;

    /// <summary>
    /// 应用标题栏主题（深色 / 浅色）。运行时切换主题后调用，Windows 10 需通过 DWM 属性切换深色标题栏。
    /// </summary>
    public void ApplyTitleBarTheme(bool dark)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var useDark = dark ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（20H1+），19 为旧版本号
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
            }

            // 通知 DWM 重绘非客户区，避免切换后标题栏仍停留在旧配色
            const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            var titleBar = _appWindow?.TitleBar;
            if (titleBar is null) return;
            if (dark)
            {
                var bg = Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x17, 0x13);
                var fg = Microsoft.UI.Colors.White;
                titleBar.BackgroundColor = bg;
                titleBar.InactiveBackgroundColor = bg;
                titleBar.ForegroundColor = fg;
                titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xC5, 0xBD, 0xB4);
                titleBar.ButtonBackgroundColor = bg;
                titleBar.ButtonInactiveBackgroundColor = bg;
                titleBar.ButtonForegroundColor = fg;
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xC5, 0xBD, 0xB4);
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x23, 0x1D);
            }
            else
            {
                // 恢复系统默认（null = 交给系统按浅色处理）
                titleBar.BackgroundColor = null;
                titleBar.InactiveBackgroundColor = null;
                titleBar.ForegroundColor = null;
                titleBar.InactiveForegroundColor = null;
                titleBar.ButtonBackgroundColor = null;
                titleBar.ButtonInactiveBackgroundColor = null;
                titleBar.ButtonForegroundColor = null;
                titleBar.ButtonInactiveForegroundColor = null;
                titleBar.ButtonHoverBackgroundColor = null;
                titleBar.ButtonHoverForegroundColor = null;
            }
        }
        catch { /* 标题栏着色失败不影响使用 */ }
    }

    // 各页面共享的 ViewModel 实例（登录状态、日志等在页面间保持一致）
    private readonly MainViewModel _mainViewModel = new();
    private readonly AccountViewModel _accountViewModel = new();
    private readonly SelectVersionViewModel _selectVersionViewModel = new();
    private readonly VersionDownloadViewModel _versionDownloadViewModel = new();
    private readonly NetworkViewModel _networkViewModel = new();
    private readonly SettingsViewModel _settingsViewModel = new();
    private readonly LogViewModel _logViewModel = new();

    public MainWindow()
    {
        InitializeComponent();

        // 设置窗口初始尺寸（按 DPI 缩放后的 1200 x 820 逻辑尺寸，避免高缩放下过窄挤掉布局）
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        if (appWindow is not null)
        {
            // 初始尺寸先用 DPI 估算，窗口加载后再用 XamlRoot 的真实缩放精确调整
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var width = (int)(1240 * scale);
            var height = (int)(860 * scale);
            appWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(960, width), Math.Max(640, height)));

            // 任务栏 / 标题栏图标（未打包应用使用应用目录内的图标文件）
            try
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ACLauncher.ico");
                if (System.IO.File.Exists(iconPath)) appWindow.SetIcon(iconPath);
            }
            catch { /* 设置图标失败不影响使用 */ }

            // 标题栏跟随应用主题（深色下白色标题栏会非常刺眼）
            ApplyTitleBarTheme(Application.Current.RequestedTheme == ApplicationTheme.Dark);
        }

        // 默认选中主页
        NavView.SelectedItem = NavView.MenuItems[0];

        // 主页"游戏版本"按钮跳转：无版本→版本下载页；有版本→选择版本页
        _mainViewModel.NavigateRequested += tag =>
        {
            var item = NavView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
            if (item is not null) NavView.SelectedItem = item;
        };

        // 每次打开启动器，后台自动检测 Java 路径与版本（不阻塞窗口显示）
        _ = _settingsViewModel.DetectJavaOnStartupAsync();

        // 启动后后台检查更新（发现新版本时弹窗提示）
        _ = CheckUpdatesOnStartupAsync();
    }

    /// <summary>启动时静默检查更新；有新版本时弹窗（不阻塞启动）。</summary>
    private async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(4000); // 等主窗口完全显示后再请求，避免抢占首屏
            var info = await Services.UpdateService.Instance.CheckAsync();
            if (info is null) return;

            var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null) return;

            await Views.UpdatePrompt.ShowAsync(xamlRoot, info);
        }
        catch (Exception ex)
        {
            Services.LogService.Instance.Log($"启动检查更新失败：{ex.Message}");
        }
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // 初次导航到主页
        ContentFrame.Navigate(typeof(MainPage), _mainViewModel);

        // 窗口显示后再次应用标题栏主题（Windows 10 上需在窗口创建完成后设置）
        ApplyTitleBarTheme(Application.Current.RequestedTheme == ApplicationTheme.Dark);

        // 按真实缩放比例把窗口调整到合适的逻辑尺寸（避免高缩放下布局被挤压）
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            var scale = NavView.XamlRoot?.RasterizationScale ?? 1.0;

            var width = (int)(1180 * scale);
            var height = (int)(800 * scale);

            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            width = Math.Min(width, display.WorkArea.Width);
            height = Math.Min(height, display.WorkArea.Height);

            appWindow?.Resize(new Windows.Graphics.SizeInt32(Math.Max(960, width), Math.Max(620, height)));
        }
        catch { /* 调整失败保持初始尺寸 */ }
    }

    /// <summary>按导航 Tag 切换到对应页面（供页面内部跳转使用，如版本管理返回选择版本）。</summary>
    public void NavigateTo(string tag)
    {
        var item = NavView.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        // 强制刷新：即使当前已经是该导航项（如从子页面返回），也要重新导航到目标页面
        if (ReferenceEquals(NavView.SelectedItem, item))
        {
            NavigateFrame(tag);
        }
        else
        {
            NavView.SelectedItem = item;
        }
    }

    /// <summary>按 Tag 导航内容 Frame（SelectionChanged 与强制刷新共用）。</summary>
    private void NavigateFrame(string tag)
    {
        switch (tag)
        {
            case "Main":
                ContentFrame.Navigate(typeof(MainPage), _mainViewModel);
                break;
            case "Account":
                ContentFrame.Navigate(typeof(AccountPage), _accountViewModel);
                break;
            case "Select":
                ContentFrame.Navigate(typeof(SelectVersionPage), _selectVersionViewModel);
                break;
            case "Download":
                ContentFrame.Navigate(typeof(VersionDownloadPage), _versionDownloadViewModel);
                break;
            case "Network":
                ContentFrame.Navigate(typeof(NetworkPage), _networkViewModel);
                break;
            case "Settings":
                ContentFrame.Navigate(typeof(SettingsPage), _settingsViewModel);
                break;
            case "Log":
                ContentFrame.Navigate(typeof(LogPage), _logViewModel);
                break;
            case "About":
                ContentFrame.Navigate(typeof(AboutPage));
                break;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // 根据选中项 Tag 切换页面，并把对应 ViewModel 作为参数传入
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            NavigateFrame(tag);
        }
    }
}
