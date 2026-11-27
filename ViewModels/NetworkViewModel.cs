using System.Collections.ObjectModel;
using System.Windows.Input;
using ACLauncher.Models;
using ACLauncher.Services;
using Microsoft.UI.Dispatching;

namespace ACLauncher.ViewModels;

/// <summary>
/// 网络设置页视图模型：
///  - 调整下载线程数量；
///  - 是否启动前自动补全缺失文件；
///  - 原版 Minecraft 下载源（Mojang / BMCLAPI）。
/// 任何选项改动后都会自动保存（防抖，无需手动点“保存”）。
/// </summary>
public class NetworkViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>自动保存防抖令牌。</summary>
    private CancellationTokenSource? _autoSaveCts;

    /// <summary>“已自动保存”提示隐藏令牌。</summary>
    private CancellationTokenSource? _hintCts;

    /// <summary>抑制自动保存（页面初始加载 / 程序自动赋值时使用）。</summary>
    private bool _suppressAutoSave;

    private double _downloadThreads = 4;

    /// <summary>下载线程数量（1 - 16）。</summary>
    public double DownloadThreads
    {
        get => _downloadThreads;
        set { if (SetProperty(ref _downloadThreads, value)) ScheduleAutoSave(); }
    }

    private bool _checkResources = true;

    /// <summary>启动前自动补全缺失文件。</summary>
    public bool CheckResources
    {
        get => _checkResources;
        set { if (SetProperty(ref _checkResources, value)) ScheduleAutoSave(); }
    }

    private string _vanillaSourceDisplay = "BMCLAPI 镜像";

    /// <summary>原版 Minecraft 下载源展示文本（Mojang 官方 / BMCLAPI 镜像）。</summary>
    public string VanillaSourceDisplay
    {
        get => _vanillaSourceDisplay;
        private set => SetProperty(ref _vanillaSourceDisplay, value);
    }

    /// <summary>原版下载源当前标识（"mojang" / "bmclapi"）。</summary>
    public string VanillaSource { get; private set; } = Services.VanillaSourceService.SourceBmclapi;

    /// <summary>切换原版下载源（由界面下拉框触发，纳入自动保存）。</summary>
    public void SelectVanillaSource(string source)
    {
        var normalized = source == Services.VanillaSourceService.SourceMojang
            ? Services.VanillaSourceService.SourceMojang
            : Services.VanillaSourceService.SourceBmclapi;
        if (normalized == VanillaSource) return;

        VanillaSource = normalized;
        VanillaSourceDisplay = normalized == Services.VanillaSourceService.SourceMojang
            ? "Mojang 官方"
            : "BMCLAPI 镜像";
        ScheduleAutoSave();
    }

    private string _saveHint = "";

    /// <summary>自动保存反馈文字（保存后短暂显示，随后清空）。</summary>
    public string SaveHint
    {
        get => _saveHint;
        private set => SetProperty(ref _saveHint, value);
    }

    public NetworkViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>从本地加载已保存的网络设置（加载过程不触发自动保存）。</summary>
    public void Load()
    {
        _suppressAutoSave = true;
        try
        {
            var settings = _settingsService.Load();
            DownloadThreads = settings.DownloadThreads;
            CheckResources = settings.CheckResources;

            // 原版下载源：默认 BMCLAPI 镜像；显式保存为 mojang 时用官方源
            VanillaSource = settings.VanillaDownloadSource == Services.VanillaSourceService.SourceMojang
                ? Services.VanillaSourceService.SourceMojang
                : Services.VanillaSourceService.SourceBmclapi;
            VanillaSourceDisplay = VanillaSource == Services.VanillaSourceService.SourceMojang
                ? "Mojang 官方"
                : "BMCLAPI 镜像";
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

    /// <summary>把当前界面值写入 settings.json。</summary>
    private async Task DoSaveAsync()
    {
        try
        {
            var settings = _settingsService.Load();
            settings.DownloadThreads = (int)Math.Clamp(Math.Round(DownloadThreads), 1, 16);
            settings.CheckResources = CheckResources;
            settings.VanillaDownloadSource = VanillaSource;

            await _settingsService.SaveAsync(settings);
            ShowSavedHint();
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"自动保存网络设置失败：{ex.Message}");
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
}
