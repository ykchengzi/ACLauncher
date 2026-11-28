using System.Diagnostics;
using ACLauncher.Services;
using ACLauncher.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ACLauncher.Views;

/// <summary>
/// 下载页：左侧分区导航（游戏 / 整合包 / 模组 / 资源包 / 光影 / 世界），
/// 右侧对应内容；游戏与整合包支持带进度的下载对话框。
/// </summary>
public sealed partial class VersionDownloadPage : Page
{
    public VersionDownloadViewModel? ViewModel { get; private set; }

    private CancellationTokenSource? _downloadCts;

    /// <summary>对话框里“最近下载内容”的历史（最多 5 行）。</summary>
    private readonly System.Collections.Generic.Queue<string> _recentStages = new();

    /// <summary>更新阶段文字，并把最新内容追加到对话框的实时列表。</summary>
    private void UpdateStageText(string text)
    {
        var stage = text ?? "";
        if (_recentStages.Count == 0 || _recentStages.LastOrDefault() != stage)
        {
            _recentStages.Enqueue(stage);
            while (_recentStages.Count > 5) _recentStages.Dequeue();
            if (DialogDetailText is not null)
            {
                DialogDetailText.Text = string.Join(Environment.NewLine, _recentStages);
            }
        }
        if (DialogProgressText is not null) DialogProgressText.Text = stage;
    }

    private void ResetRecentStages()
    {
        _recentStages.Clear();
        if (DialogDetailText is not null) DialogDetailText.Text = "";
    }
    private long _lastBytes;
    private long _lastTimestamp;

    private readonly Dictionary<string, Button> _navButtons = new();
    private Button? _activeNav;

    public VersionDownloadPage()
    {
        InitializeComponent();
        _navButtons["Game"] = NavGame;
        _navButtons["Pack"] = NavPack;
        _navButtons["Mod"] = NavMod;
        _navButtons["Resourcepack"] = NavResourcepack;
        _navButtons["Shader"] = NavShader;

        // 主题切换后重新应用左导航选中配色（代码里按 ActualTheme 取色）
        ActualThemeChanged += (_, _) =>
        {
            if (_activeNav?.Tag is string tag) SetActiveNav(tag);
        };
    }

    /// <summary>Uri → ImageSource（x:Bind 函数绑定，供模组图标使用）。</summary>
    public static ImageSource? UrlToImage(Uri? uri) =>
        uri is null ? null : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is VersionDownloadViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;

            SetActiveNav(string.IsNullOrEmpty(vm.SelectedSection) ? "Game" : vm.SelectedSection);

            // 等待版本清单加载完成后强制刷新 x:Bind 绑定：
            // ViewModel 是不可观察属性，绑定可能早于数据加载求值，导致列表空白
            await vm.LoadAsync();
            Bindings.Update();
            UpdateContextText(vm.SelectedSection);
        }
    }

    // ===================== 左侧分区导航 =====================

    private async void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;
        SetActiveNav(section);
        if (ViewModel is not null)
        {
            await ViewModel.EnterSectionAsync(section);
        }

        // 内容区上下文提示（当前游戏版本 / 加载器）——使用 ViewModel 缓存的版本，避免 UI 线程阻塞
        UpdateContextText(section);
    }

    /// <summary>刷新内容区上方的“当前版本”提示（分区切换 / 页面重建时调用）。</summary>
    private void UpdateContextText(string section)
    {
        if (ViewModel is null || ContentContextText is null) return;
        var version = string.IsNullOrEmpty(ViewModel.CurrentGameVersion) ? "未设置" : ViewModel.CurrentGameVersion;
        ContentContextText.Text = section switch
        {
            "Mod" => $"当前版本：{version}（按加载器过滤）",
            _ => $"当前版本：{version}",
        };
    }

    /// <summary>更新导航按钮选中高亮（选中项橙色底 + 深橙字，未选中恢复默认）。</summary>
    private void SetActiveNav(string section)
    {
        if (_navButtons.TryGetValue(section, out var btn))
        {
            _activeNav = btn;
        }
        // 按当前主题取选中配色（主题字典里的画刷无法在代码里直接按主题读取）
        var dark = ActualTheme == ElementTheme.Dark;
        var selectedBg = dark
            ? new SolidColorBrush(new Windows.UI.Color { A = 0xFF, R = 0x3A, G = 0x2A, B = 0x1C })
            : new SolidColorBrush(new Windows.UI.Color { A = 0xFF, R = 0xFD, G = 0xF4, B = 0xE9 });
        var selectedFg = dark
            ? new SolidColorBrush(new Windows.UI.Color { A = 0xFF, R = 0xF0, G = 0xA4, B = 0x5E })
            : new SolidColorBrush(new Windows.UI.Color { A = 0xFF, R = 0xC2, G = 0x5E, B = 0x0E });

        foreach (var (_, button) in _navButtons)
        {
            var isActive = ReferenceEquals(button, _activeNav);
            if (isActive)
            {
                button.Background = selectedBg;
                button.Foreground = selectedFg;
            }
            else
            {
                button.Background = new SolidColorBrush(Colors.Transparent);
                button.ClearValue(Control.ForegroundProperty); // 恢复主题默认前景色
            }
        }
    }

    // ===================== 游戏 / 整合包下载 =====================

    private void VanillaCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SetVanillaCategory(VanillaCategoryBox.SelectedIndex switch
        {
            1 => "快照",
            2 => "旧版",
            _ => "正式版",
        });
    }

    private void VanillaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SelectedVanillaVersion = VanillaList.SelectedItem as DownloadableVersion;
    }

    private void PackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SelectedPack = PackList.SelectedItem as DownloadableVersion;
    }

    /// <summary>下载所选整合包。</summary>
    private async void DownloadPack_Click(object sender, RoutedEventArgs e)
        => await StartDownloadAsync(ViewModel?.SelectedPack);

    /// <summary>下载所选官方版本。</summary>
    private async void DownloadVanilla_Click(object sender, RoutedEventArgs e)
        => await StartDownloadAsync(ViewModel?.SelectedVanillaVersion);

    /// <summary>弹出进度对话框，后台下载所选版本，右下角取消。</summary>
    private async Task StartDownloadAsync(DownloadableVersion? version)
    {
        var vm = ViewModel;
        if (vm is null) return;
        if (version is null || !version.CanDownload)
        {
            vm.SetStatus("请先选择一个可下载的版本。");
            return;
        }

        DownloadDialog.XamlRoot = XamlRoot;
        DialogFileName.Text = version.MainText;
        DialogProgress.IsIndeterminate = true;
        DialogProgress.Value = 0;
        ResetRecentStages();

        // 阶段文字（由安装流程推送：获取清单 / 下载核心 JAR / 下载依赖库 / 资源补全）
        var stageText = "正在连接下载源...";
        DialogProgressText.Text = stageText;
        _lastBytes = 0;
        _lastTimestamp = Stopwatch.GetTimestamp();

        var stageProgress = new Progress<string>(s =>
        {
            stageText = s;
            UpdateStageText(s);
        });
        IProgress<string> stageReporter = stageProgress;

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        // 进度回调：IProgress 自动回到 UI 线程，直接更新对话框控件
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
            {
                DialogProgress.IsIndeterminate = false;
                DialogProgress.Value = p.Percent;
            }

            // 计算速度与预计剩余时间
            var now = Stopwatch.GetTimestamp();
            var elapsedSec = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            var speedBps = elapsedSec > 0.05 ? (p.ReceivedBytes - _lastBytes) / elapsedSec : 0;
            _lastBytes = p.ReceivedBytes;
            _lastTimestamp = now;

            var receivedMb = p.ReceivedBytes / 1024.0 / 1024.0;
            var text = p.TotalBytes > 0
                ? $"{receivedMb:F1} MB / {p.TotalBytes / 1024.0 / 1024.0:F1} MB"
                : $"{receivedMb:F1} MB";

            if (speedBps > 0)
            {
                text += $" · {speedBps / 1024.0 / 1024.0:F1} MB/s";
                if (p.TotalBytes > 0 && p.ReceivedBytes < p.TotalBytes)
                {
                    var remaining = (p.TotalBytes - p.ReceivedBytes) / speedBps;
                    text += $" · 预计剩余 {FormatRemaining(remaining)}";
                }
            }
            DialogProgressText.Text = $"{stageText}\n{text}";
        });

        var dialogTask = DownloadDialog.ShowAsync().AsTask();
        var downloadTask = vm.DownloadAsync(version, progress, ct, s => stageReporter.Report(s));

        var finished = await Task.WhenAny(dialogTask, downloadTask);
        bool succeeded;
        if (finished == dialogTask)
        {
            // 用户点击了"取消"（或按了关闭）
            var result = dialogTask.IsCompletedSuccessfully ? await dialogTask : ContentDialogResult.None;
            if (result == ContentDialogResult.Primary)
            {
                DialogProgressText.Text = "正在取消...";
                _downloadCts?.Cancel();
            }
            try { succeeded = await downloadTask; } catch { succeeded = false; }
        }
        else
        {
            // 下载先完成：自动关闭对话框
            DownloadDialog.Hide();
            try { await dialogTask; } catch { /* 已隐藏 */ }
            try { succeeded = await downloadTask; } catch { succeeded = false; }
        }

        _downloadCts?.Dispose();
        _downloadCts = null;

        // 官方原版安装成功后，询问是否添加模组加载器，再让用户给版本起名字
        if (succeeded && version.DownloadUrl.StartsWith("vanilla://", StringComparison.OrdinalIgnoreCase))
        {
            var finalId = await PromptInstallerAsync(version.Id) ?? version.Id;
            await PromptRenameAsync(finalId, version.Id);
        }
    }

    private static string FormatRemaining(double seconds)
    {
        if (seconds < 60) return $"{Math.Max(1, (int)seconds)} 秒";
        if (seconds < 3600) return $"{Math.Max(1, (int)(seconds / 60))} 分钟";
        return $"{seconds / 3600:F1} 小时";
    }

    // ===================== 模组 / 资源包 / 光影 / 整合包（Modrinth） =====================

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.SearchModrinthAsync();
    }

    private async void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && ViewModel is not null)
        {
            await ViewModel.SearchModrinthAsync();
        }
    }

    private void ContentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SelectedProject = ContentList.SelectedItem as ModrinthProjectItem;
    }

    /// <summary>点击卡片进入项目详情（加载该项目的版本列表）。</summary>
    private async void ContentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel is null || e.ClickedItem is not ModrinthProjectItem item) return;
        ContentList.SelectedItem = item;
        await ViewModel.OpenDetailsAsync(item);
    }

    /// <summary>翻译当前搜索结果（英文简介 → 简体中文）。</summary>
    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TranslateSearchResultsAsync();
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.PrevPageAsync();
        UpdatePagingButtons();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.NextPageAsync();
        UpdatePagingButtons();
    }

    /// <summary>刷新分页按钮可用状态。</summary>
    private void UpdatePagingButtons()
    {
        if (ViewModel is null) return;
        PrevPageButton.IsEnabled = ViewModel.CanPrevPage;
        NextPageButton.IsEnabled = ViewModel.CanNextPage;
    }

    /// <summary>整合包下载源切换（橙猫服官方 / Modrinth），两个下拉框互相同步。</summary>
    private bool _syncingPackSource;

    private void PackSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPackSource || ViewModel is null) return;
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } }) return;

        ViewModel.SetPackSource(tag);

        // 另一张卡片里的下拉框保持同一选择
        _syncingPackSource = true;
        try
        {
            var index = tag == "modrinth" ? 1 : 0;
            if (PackSourceBox is not null) PackSourceBox.SelectedIndex = index;
            if (PackSourceBoxModrinth is not null) PackSourceBoxModrinth.SelectedIndex = index;
        }
        finally
        {
            _syncingPackSource = false;
        }
    }

    /// <summary>只下载所选整合包的源文件到用户指定位置（不安装）。</summary>
    private async void SavePackSource_Click(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;
        var version = vm.SelectedPack;
        if (version is null || !version.CanDownload)
        {
            vm.SetStatus("请先选择一个可下载的整合包。");
            return;
        }

        string suggested;
        try
        {
            suggested = Path.GetFileName(Uri.UnescapeDataString(version.DownloadUrl));
        }
        catch
        {
            suggested = "";
        }
        if (string.IsNullOrWhiteSpace(suggested) || !suggested.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            suggested = version.Id + ".zip";
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggested),
        };
        picker.FileTypeChoices.Add("压缩包", new List<string> { ".zip" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        Windows.Storage.StorageFile? file;
        try
        {
            file = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"选择保存位置失败：{ex.Message}");
            return;
        }
        if (file is null) return;

        DownloadDialog.XamlRoot = XamlRoot;
        DialogFileName.Text = version.MainText;
        DialogProgress.IsIndeterminate = true;
        DialogProgress.Value = 0;
        ResetRecentStages();

        var stageText = "正在连接下载源...";
        DialogProgressText.Text = stageText;
        _lastBytes = 0;
        _lastTimestamp = Stopwatch.GetTimestamp();
        IProgress<string> stageReporter = new Progress<string>(s =>
        {
            stageText = s;
            UpdateStageText(s);
        });

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
            {
                DialogProgress.IsIndeterminate = false;
                DialogProgress.Value = p.Percent;
            }
            var now = Stopwatch.GetTimestamp();
            var elapsedSec = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            var speedBps = elapsedSec > 0.05 ? (p.ReceivedBytes - _lastBytes) / elapsedSec : 0;
            _lastBytes = p.ReceivedBytes;
            _lastTimestamp = now;

            var receivedMb = p.ReceivedBytes / 1024.0 / 1024.0;
            var text = p.TotalBytes > 0
                ? $"{receivedMb:F1} MB / {p.TotalBytes / 1024.0 / 1024.0:F1} MB"
                : $"{receivedMb:F1} MB";
            if (speedBps > 0)
            {
                text += $" · {speedBps / 1024.0 / 1024.0:F1} MB/s";
            }
            DialogProgressText.Text = $"{stageText}\n{text}";
        });

        var dialogTask = DownloadDialog.ShowAsync().AsTask();
        var saveTask = vm.SavePackSourceFileAsync(version, file.Path, progress, ct, s => stageReporter.Report(s));

        var finished = await Task.WhenAny(dialogTask, saveTask);
        if (finished == dialogTask)
        {
            var result = dialogTask.IsCompletedSuccessfully ? await dialogTask : ContentDialogResult.None;
            if (result == ContentDialogResult.Primary)
            {
                DialogProgressText.Text = "正在取消...";
                _downloadCts?.Cancel();
            }
            try { await saveTask; } catch { /* 已记录状态 */ }
        }
        else
        {
            DownloadDialog.Hide();
            try { await dialogTask; } catch { /* 已隐藏 */ }
            try { await saveTask; } catch { /* 已记录状态 */ }
        }

        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    private void BackToResults_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.CloseDetails();
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SelectedProjectVersion = VersionList.SelectedItem as ModrinthVersionItem;
    }

    /// <summary>展开 / 收起项目简介。</summary>
    private void ToggleDescription_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.DescriptionExpanded = !ViewModel.DescriptionExpanded;
    }

    /// <summary>浏览保存位置并下载所选版本：默认定位到当前版本（已装加载器）的 mods 目录。</summary>
    private async void BrowseInstall_Click(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;
        if (vm.SelectedProjectVersion is null)
        {
            vm.SetStatus("请先选择一个版本。");
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.SettingsIdentifier = "ACLauncherDownloadFolder";

            // 默认起始目录：当前版本的 mods（仅装了加载器时）或 .minecraft 对应目录
            var defaultFolder = vm.GetDefaultTargetFolder();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            if (Directory.Exists(defaultFolder))
            {
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            await vm.InstallSelectedVersionToFolderAsync(folder.Path);
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"选择保存位置失败：{ex.Message}");
            vm.SetStatus($"选择保存位置失败：{ex.Message}");
        }
    }

    /// <summary>安装所选 Modrinth 整合包（下载 → 解压 → 安装 → 设为当前版本）。</summary>
    private async void InstallPack_Click(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;
        if (vm.SelectedProjectVersion is null)
        {
            vm.SetStatus("请先选择一个整合包版本。");
            return;
        }

        DownloadDialog.XamlRoot = XamlRoot;
        DialogFileName.Text = vm.SelectedProjectVersion.FileName;
        DialogProgress.IsIndeterminate = true;
        DialogProgress.Value = 0;
        ResetRecentStages();

        var stageText = "正在连接下载源...";
        DialogProgressText.Text = stageText;
        _lastBytes = 0;
        _lastTimestamp = Stopwatch.GetTimestamp();
        IProgress<string> stageReporter = new Progress<string>(s =>
        {
            stageText = s;
            UpdateStageText(s);
        });

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
            {
                DialogProgress.IsIndeterminate = false;
                DialogProgress.Value = p.Percent;
            }
            var now = Stopwatch.GetTimestamp();
            var elapsedSec = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            var speedBps = elapsedSec > 0.05 ? (p.ReceivedBytes - _lastBytes) / elapsedSec : 0;
            _lastBytes = p.ReceivedBytes;
            _lastTimestamp = now;

            var receivedMb = p.ReceivedBytes / 1024.0 / 1024.0;
            var text = p.TotalBytes > 0
                ? $"{receivedMb:F1} MB / {p.TotalBytes / 1024.0 / 1024.0:F1} MB"
                : $"{receivedMb:F1} MB";
            if (speedBps > 0) text += $" · {speedBps / 1024.0 / 1024.0:F1} MB/s";
            DialogProgressText.Text = $"{stageText}\n{text}";
        });

        var dialogTask = DownloadDialog.ShowAsync().AsTask();
        var installTask = vm.InstallSelectedPackAsync(progress, ct, s => stageReporter.Report(s));

        var finished = await Task.WhenAny(dialogTask, installTask);
        if (finished == dialogTask)
        {
            var result = dialogTask.IsCompletedSuccessfully ? await dialogTask : ContentDialogResult.None;
            if (result == ContentDialogResult.Primary)
            {
                DialogProgressText.Text = "正在取消...";
                _downloadCts?.Cancel();
            }
            try { await installTask; } catch { /* 已记录状态 */ }
        }
        else
        {
            DownloadDialog.Hide();
            try { await dialogTask; } catch { /* 已隐藏 */ }
            try { await installTask; } catch { /* 已记录状态 */ }
        }

        _downloadCts?.Dispose();
        _downloadCts = null;
    }

    // ===================== 游戏安装后：询问添加模组加载器 =====================

    /// <summary>
    /// 原版安装完成后询问是否添加加载器（Fabric / Forge / NeoForge），
    /// 选择后自动安装并将加载器版本设为当前版本。
    /// </summary>
    private async Task<string?> PromptInstallerAsync(string mc)
    {
        try
        {
            var combo = new ComboBox
            {
                MinWidth = 260,
                SelectedIndex = 0,
                IsEditable = false,
                ItemsSource = new[] { "不安装", "Fabric", "Forge", "NeoForge", "Quilt" },
            };
            var dialog = new ContentDialog
            {
                Title = $"为 {mc} 安装模组加载器？",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "安装加载器后才能使用对应平台的模组。也可以稍后安装。",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        },
                        combo,
                    },
                },
                PrimaryButtonText = "安装",
                CloseButtonText = "跳过",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

            var kind = combo.SelectedIndex switch
            {
                1 => "fabric",
                2 => "forge",
                3 => "neoforge",
                4 => "quilt",
                _ => null,
            };
            if (kind is null) return null;

            var kindName = kind switch
            {
                "fabric" => "Fabric",
                "forge" => "Forge",
                "quilt" => "Quilt",
                _ => "NeoForge",
            };

            // 获取可用加载器版本，让用户选择具体版本（默认最新）
            ViewModel?.SetStatus($"正在获取 {kindName} 版本列表...");
            var versions = await LoaderService.Instance.GetLoaderVersionsAsync(mc, kind);
            if (versions.Count == 0)
            {
                ViewModel?.SetStatus($"{mc} 暂不支持 {kindName}，请稍后再试。");
                return null;
            }

            var versionCombo = new ComboBox
            {
                MinWidth = 260,
                ItemsSource = versions,
                SelectedIndex = 0,
                IsEditable = false,
            };
            var versionDialog = new ContentDialog
            {
                Title = $"选择 {kindName} 版本",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"将安装到 Minecraft {mc}。默认选择最新版，也可指定其他版本。",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        },
                        versionCombo,
                    },
                },
                PrimaryButtonText = "安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await versionDialog.ShowAsync() != ContentDialogResult.Primary) return null;

            var loaderVersion = versionCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(loaderVersion)) return null;

            // 安装加载器（带进度对话框）
            var statusText = new TextBlock
            {
                Text = $"正在安装 {kindName} {loaderVersion}...",
                TextWrapping = TextWrapping.Wrap,
            };
            var progressDialog = new ContentDialog
            {
                Title = $"安装 {kindName}",
                Content = new StackPanel
                {
                    Spacing = 12,
                    MinWidth = 340,
                    Children = { new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center }, statusText },
                },
                CloseButtonText = "",
                XamlRoot = XamlRoot,
            };
            var dialogTask = progressDialog.ShowAsync();

            try
            {
                ViewModel?.SetStatus($"正在安装 {kindName} {loaderVersion}...");
                var versionName = await LoaderService.Instance.InstallAsync(mc, kind, loaderVersion);

                var cfg = await ConfigService.Instance.LoadAsync();
                cfg.GameVersion = versionName;
                await ConfigService.Instance.SaveAsync(cfg);

                ViewModel?.SetStatus($"已安装并入当前版本：{versionName}");
                await ViewModel!.RefreshCurrentVersionAsync();
                LogService.Instance.Log($"已安装 {kindName} {loaderVersion}，当前版本：{versionName}");
                return versionName;
            }
            catch (Exception ex)
            {
                ViewModel?.SetStatus($"安装 {kindName} 失败：{ex.Message}");
                LogService.Instance.Log($"安装 {kindName} 失败：{ex}");
            }
            finally
            {
                progressDialog.Hide();
                try { await dialogTask; } catch { /* 已关闭 */ }
            }
            return null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"加载器询问失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>安装完成后让用户给版本起昵称（默认沿用原版名 / 安装出的版本名）。</summary>
    private async Task PromptRenameAsync(string installedId, string vanillaId)
    {
        var defaultName = string.Equals(installedId, vanillaId, StringComparison.OrdinalIgnoreCase)
            ? vanillaId
            : installedId;

        var box = new TextBox
        {
            Text = defaultName,
            MaxLength = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            Title = "给这个版本起个名字",
            PrimaryButtonText = "保存",
            CloseButtonText = "保持默认",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 340,
                Children =
                {
                    new TextBlock
                    {
                        Text = "昵称只影响启动器里的显示与版本目录名，不影响游戏内容。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                    box,
                },
            },
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = (box.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, installedId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var error = VersionRenameService.Validate(name);
        if (error is not null)
        {
            await new ContentDialog
            {
                Title = "昵称不可用",
                Content = error,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }

        try
        {
            ViewModel?.SetStatus($"正在重命名版本：{name}...");
            await VersionRenameService.RenameAsync(installedId, name);
            ViewModel?.SetStatus($"版本已重命名为：{name}");
            LogService.Instance.Log($"版本已重命名：{installedId} → {name}");
            if (ViewModel is not null)
            {
                await ViewModel.LoadAsync();
                await ViewModel.RefreshCurrentVersionAsync();
            }
        }
        catch (Exception ex)
        {
            ViewModel?.SetStatus($"重命名失败：{ex.Message}");
            LogService.Instance.Log($"重命名版本失败：{ex}");
        }
    }
}
