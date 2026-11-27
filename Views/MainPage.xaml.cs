using ACLauncher.ViewModels;
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
            // 每次进入主页重新读取配置（config.json 可能被修改）
            _ = vm.InitializeAsync();
        }
    }
}
