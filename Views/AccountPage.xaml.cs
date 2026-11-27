using ACLauncher.Models;
using ACLauncher.Services;
using ACLauncher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ACLauncher.Views;

/// <summary>
/// 账户页：账户列表、添加（Microsoft / 离线）、切换、删除。
/// </summary>
public sealed partial class AccountPage : Page
{
    public AccountViewModel? ViewModel { get; private set; }

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

    public AccountPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is AccountViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            _ = vm.InitializeAsync();
        }
    }

    // 卡片状态画刷（静态缓存）
    private static readonly SolidColorBrush SelectedBorderBrush =
        new(new Windows.UI.Color { A = 0xE8, R = 0x79, G = 0x1E, B = 0x00 });
    private static readonly SolidColorBrush NormalBorderBrush =
        new(new Windows.UI.Color { A = 0x22, R = 0x00, G = 0x00, B = 0x00 });
    private static readonly SolidColorBrush SelectedBackgroundBrush =
        new(new Windows.UI.Color { A = 0x14, R = 0xE8, G = 0x79, B = 0x1E });
    private static readonly SolidColorBrush NormalBackgroundBrush =
        new(new Windows.UI.Color { A = 0x0C, R = 0x00, G = 0x00, B = 0x00 });

    public static Brush GetCardBorder(bool isSelected) => isSelected ? SelectedBorderBrush : NormalBorderBrush;

    public static Brush GetCardBackground(bool isSelected) => isSelected ? SelectedBackgroundBrush : NormalBackgroundBrush;

    /// <summary>点击账户卡片：按 Id 定位账户并选中（ItemsRepeater 不设置 DataContext，用 Tag 传 Id）。</summary>
    private void AccountCard_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is FrameworkElement { Tag: string id })
        {
            var account = ViewModel.Accounts.FirstOrDefault(a => a.Id == id);
            if (account is not null) ViewModel.SelectCard(account);
        }
    }

    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.AddMicrosoftAccountAsync(XamlRoot);
    }

    private async void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.AddOfflineAccountAsync(XamlRoot);
    }

    /// <summary>内置 LittleSkin 外置登录。</summary>
    private async void AddLittleSkin_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.AddExternalAccountAsync(XamlRoot, YggdrasilService.LittleSkin);
    }

    /// <summary>自定义外置登录源：输入 API 根地址后登录。</summary>
    private async void AddCustomExternal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var urlBox = new TextBox
        {
            PlaceholderText = "API 根地址，如 https://example.com/api/yggdrasil",
        };
        var nameBox = new TextBox
        {
            PlaceholderText = "显示名称（可选），如 My Server",
        };
        var dialog = new ContentDialog
        {
            Title = "自定义外置登录源",
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 360,
                Children =
                {
                    new TextBlock
                    {
                        Text = "填写支持 Yggdrasil 协议（authlib-injector）的服务器 API 根地址。",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                    },
                    urlBox,
                    nameBox,
                },
            },
            PrimaryButtonText = "下一步",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var url = urlBox.Text.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await new ContentDialog
            {
                Title = "地址无效",
                Content = "请填写以 http:// 或 https:// 开头的完整地址。",
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? new Uri(url).Host : nameBox.Text.Trim();
        await ViewModel.AddExternalAccountAsync(XamlRoot,
            new YggdrasilServer { ApiRoot = url, Name = name });
    }

    private async void Relogin_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ReloginExternalAsync(XamlRoot);
    }

    /// <summary>双击账户卡片：直接切换为该账户。</summary>
    private void AccountCard_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is FrameworkElement { Tag: string id } &&
            ViewModel.Accounts.FirstOrDefault(a => a.Id == id) is { } account)
        {
            ViewModel.SelectCard(account);
            ViewModel.SwitchSelected();
            e.Handled = true;
        }
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.SwitchSelected();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedAccount is not Account selected) return;

        // 删除前二次确认，避免误删
        var confirm = new ContentDialog
        {
            Title = "删除账户",
            Content = $"确定要删除账户“{selected.PlayerName}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        var result = await confirm.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.DeleteSelected();
        }
    }
}
