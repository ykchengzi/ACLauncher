using ACLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ACLauncher.Views;

/// <summary>在线更新提示对话框（启动时与关于页共用）。</summary>
public static class UpdatePrompt
{
    /// <summary>展示更新提示；用户确认后下载并启动安装器（成功后关闭启动器）。</summary>
    public static async Task ShowAsync(XamlRoot xamlRoot, UpdateInfo info)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"新版本 {info.Version}（当前 {UpdateService.Instance.CurrentVersion}）",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            content.Children.Add(new TextBlock
            {
                Text = info.Notes,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        var dialog = new ContentDialog
        {
            Title = "发现新版本",
            Content = new ScrollViewer { Content = content, MaxHeight = 320 },
            PrimaryButtonText = "立即更新",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // 下载进度对话框
        var progressBar = new ProgressBar { Maximum = 100, IsIndeterminate = true, MinWidth = 340 };
        var statusText = new TextBlock
        {
            Text = "正在下载更新...",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var progressDialog = new ContentDialog
        {
            Title = "正在更新",
            Content = new StackPanel { Spacing = 12, Children = { statusText, progressBar } },
            CloseButtonText = "",
            XamlRoot = xamlRoot,
        };
        var dialogTask = progressDialog.ShowAsync();

        var progress = new Progress<(long received, long total)>(p =>
        {
            if (p.total > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = p.received * 100.0 / p.total;
                statusText.Text = $"正在下载更新：{p.received / 1024.0 / 1024.0:F1} / {p.total / 1024.0 / 1024.0:F1} MB";
            }
            else
            {
                statusText.Text = $"正在下载更新：{p.received / 1024.0 / 1024.0:F1} MB";
            }
        });

        var ok = await UpdateService.Instance.DownloadAndRunAsync(info, progress);

        progressDialog.Hide();
        try { await dialogTask; } catch { /* 已关闭 */ }

        if (ok)
        {
            LogService.Instance.Log("更新包已下载并启动安装程序，启动器即将退出。");
            // 安装器会自动关闭本进程并覆盖安装；稍等片刻后退出
            await Task.Delay(800);
            Environment.Exit(0);
        }
        else
        {
            var fail = new ContentDialog
            {
                Title = "更新失败",
                Content = "更新包下载或校验失败，请检查网络后重试。",
                CloseButtonText = "确定",
                XamlRoot = xamlRoot,
            };
            await fail.ShowAsync();
        }
    }
}
