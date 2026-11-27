using ACLauncher.Services;
using ACLauncher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ACLauncher.Views;

/// <summary>
/// 日志页：实时滚动展示游戏进程输出与启动步骤，支持清空。
/// 使用虚拟化 ListView（只渲染可见行），大日志量下进出页面不卡顿。
/// </summary>
public sealed partial class LogPage : Page
{
    public LogViewModel? ViewModel { get; private set; }

    private ScrollViewer? _innerScrollViewer;
    private bool _stickToBottom = true;

    public LogPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _innerScrollViewer = FindScrollViewer(LogList);
            if (_innerScrollViewer is not null)
            {
                _innerScrollViewer.ViewChanged += OnInnerViewChanged;
            }
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is LogViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;

            // 新日志到达：用户在底部附近时自动滚动到底（避免打断用户向上翻看）
            vm.Lines.CollectionChanged += (_, _) =>
            {
                if (_stickToBottom)
                {
                    ScrollToBottom();
                }
            };

            // 进入页面：日志填完后滚到底部
            DispatcherQueue.TryEnqueue(() => ScrollToBottom());
        }
    }

    /// <summary>用户手动滚动时判断是否停在底部附近（决定新日志是否自动跟随）。</summary>
    private void OnInnerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_innerScrollViewer is null) return;
        _stickToBottom =
            _innerScrollViewer.VerticalOffset + _innerScrollViewer.ViewportHeight
                >= _innerScrollViewer.ExtentHeight - 40;
    }

    private void ScrollToBottom()
    {
        if (_innerScrollViewer is null) return;
        _innerScrollViewer.UpdateLayout();
        _innerScrollViewer.ChangeView(null, _innerScrollViewer.ExtentHeight, null, disableAnimation: true);
    }

    /// <summary>从 ListView 的可视化树中找内部 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.Clear();
    }

    /// <summary>一键复制全部日志到剪贴板。</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = ViewModel is null ? "" : string.Join(Environment.NewLine, ViewModel.Lines);
            if (string.IsNullOrEmpty(text)) return;

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            LogService.Instance.Log($"已复制日志（{ViewModel!.Lines.Count} 行）到剪贴板。");
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"复制日志失败：{ex.Message}");
        }
    }
}
