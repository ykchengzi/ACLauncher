using System.Collections.ObjectModel;
using System.Windows.Input;
using ACLauncher.Services;
using Microsoft.UI.Dispatching;

namespace ACLauncher.ViewModels;

/// <summary>
/// 日志页视图模型：实时展示日志行，支持清空。
/// </summary>
public class LogViewModel : ViewModelBase
{
    private readonly DispatcherQueue _dispatcher;

    /// <summary>日志行集合（UI 绑定）。</summary>
    public ObservableCollection<string> Lines { get; } = new();

    /// <summary>清空日志。</summary>
    public void Clear() => LogService.Instance.Clear();

    /// <summary>清空日志命令。</summary>
    public ICommand ClearCommand { get; }

    public LogViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ClearCommand = new RelayCommand(() => LogService.Instance.Clear());

        // 先填充已有日志
        foreach (var line in LogService.Instance.Lines)
        {
            Lines.Add(line);
        }

        // 订阅新日志（可能在后台线程触发，转发到 UI 线程）
        LogService.Instance.LineAdded += line => _dispatcher.TryEnqueue(() => Lines.Add(line));
        LogService.Instance.LineCleared += () => _dispatcher.TryEnqueue(() => Lines.Clear());
    }
}
