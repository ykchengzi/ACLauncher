using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ACLauncher.Views;

/// <summary>累计启动次数达到节点时的“考虑购买正版”提醒（启动器内弹窗）。</summary>
public static class SupportPrompt
{
    /// <summary>展示提醒；点「前往 MC 官网」打开浏览器，点「下次一定」关闭。</summary>
    public static async Task ShowAsync(XamlRoot? xamlRoot, int count)
    {
        if (xamlRoot is null) return;

        var text = count >= Services.LaunchCounterService.StrongToneFrom
            ? $"哇（），以防你不知道，你已经使用本启动器启动了 {count} 次了O.O，要不买一下正版awa？"
            : $"你已经用 ACLauncher 启动了 {count} 次游戏了……考虑一下购买正版？qwq";

        var dialog = new ContentDialog
        {
            Title = "支持正版《我的世界》",
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "前往 MC 官网",
            CloseButtonText = "下次一定",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        catch
        {
            // 已有其它对话框打开时忽略本次提醒，不打断用户
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://minecraft.net/",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开浏览器失败时忽略，用户可手动访问官网
        }
    }
}
