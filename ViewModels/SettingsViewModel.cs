using System.Collections.ObjectModel;
using System.Windows.Input;
using ACLauncher.Models;
using ACLauncher.Services;
using Microsoft.UI.Dispatching;

namespace ACLauncher.ViewModels;

/// <summary>
/// 设置页视图模型：Java 路径（自动检测 + 手动配置）、JVM 内存、主题、进程优先级。
/// 任何选项改动后都会自动保存（防抖，无需手动点“保存”）。启动参数由启动器内置（默认连接
/// 服务器 mc.ambercat.top），不允许用户自定义。
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private readonly JavaService _javaService = JavaService.Instance;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>自动检测到的 Java 列表。</summary>
    public ObservableCollection<JavaInfo> DetectedJavaList { get; } = new();

    /// <summary>自动保存防抖令牌（连续改动只保存最后一次）。</summary>
    private CancellationTokenSource? _autoSaveCts;

    /// <summary>“已自动保存”提示隐藏令牌。</summary>
    private CancellationTokenSource? _hintCts;

    /// <summary>抑制自动保存（页面初始加载 / 程序自动赋值时使用，避免程序化赋值触发保存）。</summary>
    private bool _suppressAutoSave;

    private JavaInfo? _selectedJava;

    /// <summary>下拉框中选中的 Java。</summary>
    public JavaInfo? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (SetProperty(ref _selectedJava, value))
            {
                OnPropertyChanged(nameof(JavaPathDisplay));
                ScheduleAutoSave();
            }
        }
    }

    private double _minMemoryMB = 1024;

    /// <summary>JVM 最小内存（MB）。</summary>
    public double MinMemoryMB
    {
        get => _minMemoryMB;
        set { if (SetProperty(ref _minMemoryMB, value)) ScheduleAutoSave(); }
    }

    private double _maxMemoryMB = 4096;

    /// <summary>JVM 最大内存（MB）。</summary>
    public double MaxMemoryMB
    {
        get => _maxMemoryMB;
        set { if (SetProperty(ref _maxMemoryMB, value)) ScheduleAutoSave(); }
    }

    private bool _autoMemory;

    /// <summary>自动分配内存（按物理内存一半自动计算，忽略手动数值）。</summary>
    public bool AutoMemory
    {
        get => _autoMemory;
        set
        {
            if (SetProperty(ref _autoMemory, value))
            {
                OnPropertyChanged(nameof(ManualMemoryEnabled));
                OnPropertyChanged(nameof(ManualMemoryOpacity));
                OnPropertyChanged(nameof(AutoMemoryHint));
                ScheduleAutoSave();
            }
        }
    }

    /// <summary>手动内存输入是否可用（自动模式下禁用）。</summary>
    public bool ManualMemoryEnabled => !AutoMemory;

    /// <summary>手动内存输入区透明度（自动模式下变淡）。</summary>
    public double ManualMemoryOpacity => AutoMemory ? 0.45 : 1.0;

    /// <summary>自动内存说明文字（显示实际将要分配的值）。</summary>
    public string AutoMemoryHint
    {
        get
        {
            var settings = SettingsService.Instance.Load();
            settings.AutoMemory = true;
            return $"自动模式：将分配 {settings.AutoMaxMemoryMB:0} MB（物理内存的一半，上限 16 GB）";
        }
    }

    /// <summary>可选主题。</summary>
    public IReadOnlyList<string> Themes { get; } = new[] { "跟随系统", "亮色", "暗色" };

    private string _theme = "跟随系统";

    /// <summary>当前主题（保存后生效）。</summary>
    public string Theme
    {
        get => _theme;
        set { if (SetProperty(ref _theme, value)) ScheduleAutoSave(); }
    }

    /// <summary>可选进程优先级。</summary>
    public IReadOnlyList<string> Priorities { get; } = Models.GameProcessPriority.Options;

    private string _gamePriority = "普通";

    /// <summary>游戏进程优先级。</summary>
    public string GamePriority
    {
        get => _gamePriority;
        set { if (SetProperty(ref _gamePriority, value)) ScheduleAutoSave(); }
    }

    private bool _versionIsolation;

    /// <summary>版本隔离：每个版本使用独立的游戏目录（世界 / 服务器列表等互不互通）。</summary>
    public bool VersionIsolation
    {
        get => _versionIsolation;
        set { if (SetProperty(ref _versionIsolation, value)) ScheduleAutoSave(); }
    }

    private bool _autoJoinServer = true;

    /// <summary>启动后自动加入橙猫生存服务器。</summary>
    public bool AutoJoinServer
    {
        get => _autoJoinServer;
        set { if (SetProperty(ref _autoJoinServer, value)) ScheduleAutoSave(); }
    }

    private bool _isDetecting;

    /// <summary>是否正在检测 Java。</summary>
    public bool IsDetecting
    {
        get => _isDetecting;
        private set => SetProperty(ref _isDetecting, value);
    }

    private string _saveHint = "";

    /// <summary>自动保存反馈文字（保存后短暂显示“已自动保存”，随后清空）。</summary>
    public string SaveHint
    {
        get => _saveHint;
        private set => SetProperty(ref _saveHint, value);
    }

    /// <summary>当前 Java 路径展示文本。</summary>
    public string JavaPathDisplay => SelectedJava?.Path ?? "未配置";

    /// <summary>自动检测 Java 命令（手动按钮）。</summary>
    public ICommand DetectJavaCommand { get; }

    public SettingsViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync, () => !IsDetecting);
    }

    /// <summary>从本地加载已保存的设置（加载过程不算用户修改，抑制自动保存）。</summary>
    public void Load()
    {
        _suppressAutoSave = true;
        try
        {
            var settings = _settingsService.Load();
            MinMemoryMB = settings.MinMemoryMB;
            MaxMemoryMB = settings.MaxMemoryMB;
            AutoMemory = settings.AutoMemory;
            Theme = settings.Theme;
            GamePriority = settings.GamePriority;
            VersionIsolation = settings.VersionIsolation;
            AutoJoinServer = settings.AutoJoinServer;
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    /// <summary>安排一次防抖自动保存（400ms 内的连续改动合并为一次写盘）。</summary>
    private void ScheduleAutoSave()
    {
        if (_suppressAutoSave) return;

        _autoSaveCts?.Cancel();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;
        _ = AutoSaveLoopAsync(token);
    }

    private async Task AutoSaveLoopAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested) return;
        await DoSaveAsync();
    }

    /// <summary>把当前界面值写入 settings.json 并应用主题。</summary>
    private async Task DoSaveAsync()
    {
        try
        {
            var settings = _settingsService.Load();
            settings.JavaPath = SelectedJava?.Path ?? settings.JavaPath;
            settings.MinMemoryMB = MinMemoryMB;
            settings.MaxMemoryMB = MaxMemoryMB;
            settings.AutoMemory = AutoMemory;
            settings.Theme = Theme;
            settings.GamePriority = GamePriority;
            settings.VersionIsolation = VersionIsolation;
            settings.AutoJoinServer = AutoJoinServer;

            await _settingsService.SaveAsync(settings);
            App.ApplyTheme(Theme);
            ShowSavedHint();
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"自动保存设置失败：{ex.Message}");
        }
    }

    /// <summary>短暂显示“已自动保存”，2 秒后自动消失。</summary>
    private void ShowSavedHint()
    {
        SaveHint = "✓ 已自动保存";
        _hintCts?.Cancel();
        _hintCts = new CancellationTokenSource();
        var token = _hintCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _dispatcher.TryEnqueue(() => SaveHint = "");
        });
    }

    /// <summary>自动检测系统 Java（用户在设置页点击按钮时调用）。</summary>
    public async Task DetectJavaAsync()
    {
        IsDetecting = true;
        try
        {
            var list = await _javaService.DetectAsync();
            _dispatcher.TryEnqueue(() =>
            {
                PopulateAndSelect(list);
                if (DetectedJavaList.Count == 0)
                {
                    LogService.Instance.Log("未检测到任何 Java 安装，请手动指定 javaw.exe / java.exe 路径。");
                }
                else
                {
                    LogService.Instance.Log($"检测到 {DetectedJavaList.Count} 个 Java 安装。");
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => LogService.Instance.Log($"Java 检测失败：{ex.Message}"));
        }
        finally
        {
            IsDetecting = false;
        }
    }

    /// <summary>
    /// 每次启动器启动时后台检测 Java：
    /// 填充下拉列表；若已保存的 Java 路径为空或已失效，自动选择检测到的最高版本并保存。
    /// </summary>
    public async Task DetectJavaOnStartupAsync()
    {
        try
        {
            var list = await _javaService.DetectAsync();
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    DetectedJavaList.Clear();
                    foreach (var java in list)
                    {
                        DetectedJavaList.Add(java);
                    }

                    var settings = _settingsService.Load();
                    var savedValid = !string.IsNullOrWhiteSpace(settings.JavaPath)
                                     && File.Exists(settings.JavaPath);
                    var savedMatch = DetectedJavaList.FirstOrDefault(j =>
                        string.Equals(j.Path, settings.JavaPath, StringComparison.OrdinalIgnoreCase));

                    _suppressAutoSave = true;
                    if (savedMatch is not null)
                    {
                        // 已保存的 Java 仍在检测列表中：保持选中
                        SelectedJava = savedMatch;
                        _suppressAutoSave = false;
                    }
                    else if (savedValid)
                    {
                        // 已保存路径有效（如手动指定），即使不在检测列表也保留
                        SelectedJava = DetectedJavaList.FirstOrDefault();
                        _suppressAutoSave = false;
                    }
                    else
                    {
                        // 未配置或已失效：自动选择最高版本（DetectAsync 已按版本排序）并保存
                        var best = DetectedJavaList.FirstOrDefault();
                        SelectedJava = best;
                        _suppressAutoSave = false;
                        if (best is not null)
                        {
                            settings.JavaPath = best.Path;
                            _ = _settingsService.SaveAsync(settings);
                            LogService.Instance.Log($"启动时自动检测到 Java，已选择：{best.Version} ({best.Path})");
                        }
                    }

                    if (DetectedJavaList.Count > 0)
                    {
                        LogService.Instance.Log($"启动检测到 {DetectedJavaList.Count} 个 Java 安装。");
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Log($"启动 Java 检测处理失败：{ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"启动 Java 检测失败：{ex.Message}");
        }
    }

    /// <summary>用检测结果填充列表，并选中已保存的 Java（程序化赋值，不触发自动保存）。</summary>
    private void PopulateAndSelect(List<JavaInfo> list)
    {
        DetectedJavaList.Clear();
        foreach (var java in list)
        {
            DetectedJavaList.Add(java);
        }

        var saved = _settingsService.Load().JavaPath;
        _suppressAutoSave = true;
        SelectedJava = DetectedJavaList.FirstOrDefault(j =>
                          string.Equals(j.Path, saved, StringComparison.OrdinalIgnoreCase))
                      ?? DetectedJavaList.FirstOrDefault();
        _suppressAutoSave = false;
    }

    /// <summary>设置手动选择的 Java 路径（由文件选择器回调）。</summary>
    public void SetManualJavaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var info = new JavaInfo { Path = path, Version = "手动配置" };
        var existing = DetectedJavaList.FirstOrDefault(j =>
            string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedJava = existing;
        }
        else
        {
            DetectedJavaList.Add(info);
            SelectedJava = info;
        }
    }
}
