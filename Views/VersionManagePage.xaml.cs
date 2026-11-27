using System.Diagnostics;
using ACLauncher.Services;
using ACLauncher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace ACLauncher.Views;

/// <summary>
/// 版本管理页：单个已安装版本的详细信息与常用操作
/// （设为当前、打开版本/mods/存档目录、复制路径、删除）。
/// 从「选择版本」页右键版本卡片进入。
/// </summary>
public sealed partial class VersionManagePage : Page
{
    public VersionManageViewModel ViewModel { get; } = new();

    public VersionManagePage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string versionName && !string.IsNullOrWhiteSpace(versionName))
        {
            _ = ViewModel.LoadAsync(versionName);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // 返回选择版本页
        var mainWindow = App.MainWindow as MainWindow;
        if (mainWindow is not null)
        {
            mainWindow.NavigateTo("Select");
            return;
        }
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolder(ViewModel.VersionDir);

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ViewModel.ModsDir);
        OpenFolder(ViewModel.ModsDir);
    }

    private void OpenSavesFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ViewModel.SavesDir);
        OpenFolder(ViewModel.SavesDir);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(ViewModel.VersionDir);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"复制路径失败：{ex.Message}");
        }
    }

    private async void SetCurrent_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SetCurrentAsync();
    }

    /// <summary>“模组”页未安装加载器时的跳转：切到“管理”标签页。</summary>
    private void GoInstallLoader_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedIndex = 1;
    }

    /// <summary>安装 / 更换模组加载器（会新建版本目录）。</summary>
    private async void InstallLoader_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.InstallSelectedLoaderAsync();
    }

    /// <summary>跳到下载页的模组分区挑选模组。</summary>
    private async void GoDownloadMods_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mainWindow)
        {
            await mainWindow.NavigateToDownloadSectionAsync("Mod");
        }
    }

    /// <summary>删除所选的模组文件。</summary>
    private async void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedMod is not { } mod) return;

        var confirm = new ContentDialog
        {
            Title = "删除模组",
            Content = $"确定要删除「{mod.FileName}」吗？\n\n文件会从 mods 目录中直接删除，下次启动游戏生效。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        ViewModel.DeleteSelectedMod();
        await ViewModel.RefreshModsAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "删除版本",
            Content = $"确定要删除版本「{ViewModel.VersionName}」吗？\n\n"
                      + "该版本的文件将从磁盘删除；共享的库与资源文件不受影响，其他版本仍可正常启动。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        if (await ViewModel.DeleteAsync())
        {
            // 删除成功后回到选择版本页
            (App.MainWindow as MainWindow)?.NavigateTo("Select");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { path },
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"打开文件夹失败：{ex.Message}");
        }
    }
}
