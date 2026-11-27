using System.Diagnostics;
using ACLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ACLauncher.Views;

/// <summary>
/// 关于页：展示启动器版本、服务器信息、功能说明与致谢。
/// </summary>
public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 品牌图标：从应用目录加载 PNG（未打包应用不能用 ms-appx 相对资源）
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "ACLauncher.png");
            if (File.Exists(iconPath))
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(iconPath);
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmap.SetSourceAsync(await file.OpenAsync(Windows.Storage.FileAccessMode.Read));
                BrandIcon.Source = bitmap;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"加载关于页图标失败：{ex.Message}");
        }

        // 版本号已由 XAML 固定展示（"v0.99.0 内测版"）；程序集信息版本仅在发布时同步更新
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "ACLauncher.exe");
            var fv = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (!string.IsNullOrWhiteSpace(fv) && !fv.StartsWith("0.99"))
            {
                VersionText.Text = $"程序集版本 v{fv}";
            }
        }
        catch
        {
            VersionText.Text = "";
        }
    }

    /// <summary>手动检查更新。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新...";
        try
        {
            var info = await UpdateService.Instance.CheckAsync();
            if (info is null)
            {
                UpdateStatusText.Text = "已是最新版本";
                return;
            }
            UpdateStatusText.Text = $"发现新版本 {info.Version}";
            await UpdatePrompt.ShowAsync(XamlRoot, info);
        }
        finally
        {
            if (sender is Button b) b.IsEnabled = true;
        }
    }
}
