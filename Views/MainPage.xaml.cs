using ACLauncher.Services;
using ACLauncher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ACLauncher.Views;

/// <summary>
/// 主页：展示版本号、游戏目录、当前账户，提供启动按钮与状态文字。
/// </summary>
public sealed partial class MainPage : Page
{
    public MainViewModel? ViewModel { get; private set; }

    /// <summary>本地头像路径 → ImageSource（x:Bind 函数绑定）。</summary>
    public static ImageSource? PathToImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
        try
        {
            return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        }
        catch
        {
            return null;
        }
    }

    public MainPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            // 主页复用的同一个视图模型实例，先退订避免重复弹窗
            vm.LaunchMilestoneReached -= OnLaunchMilestoneReached;
            vm.LaunchMilestoneReached += OnLaunchMilestoneReached;
            // 每次进入主页重新读取配置（config.json 可能被修改）
            _ = vm.InitializeAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (ViewModel is not null)
        {
            ViewModel.LaunchMilestoneReached -= OnLaunchMilestoneReached;
        }
    }

    private void OnLaunchMilestoneReached(int count) => _ = ShowBuyGamePromptAsync(count);

    /// <summary>启动次数达到节点时的“考虑购买正版”提醒（100 次起换用更强的文案）。</summary>
    private async Task ShowBuyGamePromptAsync(int count)
    {
        if (XamlRoot is null) return;

        var text = count >= LaunchCounterService.StrongToneFrom
            ? $"哇（），以防你不知道，你已经使用本启动器启动了 {count} 次了O.O，要不买一下正版awa？"
            : $"你已经用 ACLauncher 启动了 {count} 次游戏了……考虑一下购买正版？qwq";

        var dialog = new ContentDialog
        {
            Title = "支持正版《我的世界》",
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "前往 MC 官网",
            CloseButtonText = "下次一定",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
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
