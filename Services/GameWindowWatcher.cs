using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ACLauncher.Services;

/// <summary>等待游戏窗口的结果。</summary>
public enum GameWindowState
{
    /// <summary>已检测到游戏主窗口。</summary>
    Appeared,

    /// <summary>窗口出现前进程已退出。</summary>
    Exited,
}

/// <summary>
/// 游戏窗口检测。
/// 不用 Process.MainWindowHandle：它对 LWJGL / GLFW 创建的窗口经常返回 0
/// （窗口由游戏线程创建，主窗口句柄不更新），会导致窗口明明弹出了却检测不到。
/// 改为直接枚举桌面上所有顶层窗口：
///   - 窗口类名含 GLFW / LWJGL（Minecraft 客户端固定使用），或
///   - 窗口属于游戏进程且可见、有标题、尺寸合理（排除控制台 / 输入法等系统窗口）。
/// 不设超时：配置差或大型整合包加载可能需要很久，只要进程还活着就一直等，
/// 期间定时回报状态，用户可随时点“取消启动”。
/// </summary>
public static class GameWindowWatcher
{
    private const int PollIntervalMs = 500;
    private const int ReportIntervalSeconds = 15;

    /// <summary>等待窗口出现。进程退出返回 Exited；取消则抛出 OperationCanceledException。</summary>
    public static async Task<GameWindowState> WaitAsync(
        Process process, Action<string>? onWaiting = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited) return GameWindowState.Exited;

            if (FindWindow((uint)process.Id) is not null) return GameWindowState.Appeared;

            if (onWaiting is not null && sw.Elapsed - lastReport >= TimeSpan.FromSeconds(ReportIntervalSeconds))
            {
                lastReport = sw.Elapsed;
                onWaiting($"游戏仍在加载，等待窗口出现…（已等待 {(int)sw.Elapsed.TotalSeconds} 秒）");
            }

            await Task.Delay(PollIntervalMs, ct);
        }
    }

    /// <summary>检查当前是否已有该进程的游戏窗口（启动器重开后可用来恢复“游戏运行中”状态）。</summary>
    public static bool IsWindowVisible(uint processId) => FindWindow(processId) is not null;

    // ===================== Win32 =====================

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>不属于游戏窗口的系统窗口类名（控制台、输入法、托盘等）。</summary>
    private static readonly string[] IgnoredClasses =
    {
        "ConsoleWindowClass", "PseudoConsoleWindow", "IME", "MSCTFIME UI", "Default IME",
        "GDI+ Hook Window Class", "Shell_TrayWnd", "Progman", "WorkerW",
    };

    /// <summary>
    /// 枚举桌面顶层窗口查找游戏窗口。
    /// 分两轮：先找属于游戏进程的窗口（最准确），找不到再按 GLFW 类名兜底
    /// （java_launch_wrapper 等包装器可能让游戏窗口落在派生的子进程里）。
    /// 只接受可见窗口——GLFW 会创建隐藏的 "GLFW3 Helper" 消息窗口，必须排除。
    /// </summary>
    private static IntPtr? FindWindow(uint gameProcessId)
    {
        // 第一轮：进程匹配
        var own = ScanWindows((pid, _) => pid == gameProcessId, requireGlfw: false);
        if (own is not null) return own;

        // 第二轮：GLFW 类名兜底（同一台机器同时开多个实例时优先第一轮的结果）
        return ScanWindows((_, className) =>
                className.Contains("GLFW", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("LWJGL", StringComparison.OrdinalIgnoreCase),
            requireGlfw: true);
    }

    /// <summary>扫描顶层窗口，返回第一个满足条件的可见窗口。</summary>
    private static IntPtr? ScanWindows(Func<uint, string, bool> match, bool requireGlfw)
    {
        var found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            var classBuffer = new StringBuilder(256);
            if (GetClassName(hWnd, classBuffer, classBuffer.Capacity) <= 0) return true;
            var className = classBuffer.ToString();

            if (IgnoredClasses.Any(c => className.Equals(c, StringComparison.OrdinalIgnoreCase))) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (!match(pid, className)) return true;

            if (!GetWindowRect(hWnd, out var rect)) return true;
            if (rect.Width < 160 || rect.Height < 100) return true;

            // 非 GLFW 窗口要求有标题，避免把无标题的工具窗口当成游戏
            if (!requireGlfw)
            {
                var title = new StringBuilder(256);
                if (GetWindowText(hWnd, title, title.Capacity) <= 0) return true;
            }

            found = hWnd;
            return false; // 停止枚举
        }, IntPtr.Zero);

        return found == IntPtr.Zero ? null : found;
    }
}
