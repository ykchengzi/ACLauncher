namespace ACLauncher.Services;

/// <summary>
/// 日志服务：集中管理所有日志行。
/// 游戏进程输出、启动流程关键步骤都通过本服务记录，
/// 日志页 ViewModel 订阅事件实时刷新 UI。
/// </summary>
public class LogService
{
    public static LogService Instance { get; } = new();

    private readonly object _lock = new();
    private readonly List<string> _lines = new();

    /// <summary>新增一行日志（已带时间戳），事件在写入线程触发，由 UI 侧转发到 UI 线程。</summary>
    public event Action<string>? LineAdded;

    /// <summary>日志被清空。</summary>
    public event Action? LineCleared;

    /// <summary>当前全部日志行（只读快照）。</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return _lines.ToArray();
            }
        }
    }

    /// <summary>写入一行日志。</summary>
    public void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_lock)
        {
            _lines.Add(line);
        }
        LineAdded?.Invoke(line);
    }

    /// <summary>清空日志。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }
        LineCleared?.Invoke();
    }
}
