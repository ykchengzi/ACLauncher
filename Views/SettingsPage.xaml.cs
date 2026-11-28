using ACLauncher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace ACLauncher.Views;

/// <summary>
/// 设置页：Java 路径、JVM 内存、自定义参数、主题。
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel? ViewModel { get; private set; }

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SettingsViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            vm.Load();
            // 启动时已在后台检测 Java；若列表仍为空（检测尚未完成或失败），进入页面时补检一次
            if (vm.DetectedJavaList.Count == 0 && !vm.IsDetecting)
            {
                _ = vm.DetectJavaAsync();
            }
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 离开页面时把待保存的改动立即落盘（防抖窗口内切页会丢设置）
        ViewModel?.FlushPendingSave();
    }

    /// <summary>
    /// 手动浏览并选择 java.exe / javaw.exe。
    /// FileOpenPicker 需要窗口句柄（WinRT Interop）。
    /// </summary>
    private async void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            FileTypeFilter = { ".exe" },
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.SetManualJavaPath(file.Path);
        }
    }
}
