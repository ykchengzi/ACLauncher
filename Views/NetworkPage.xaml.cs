using ACLauncher.Services;
using ACLauncher.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ACLauncher.Views;

/// <summary>
/// 网络设置页：调整下载线程数、配置原版与整合包下载源。
/// </summary>
public sealed partial class NetworkPage : Page
{
    public NetworkViewModel? ViewModel { get; private set; }

    public NetworkPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is NetworkViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            vm.Load();
        }
    }

    /// <summary>切换原版 Minecraft 下载源（Mojang 官方 / BMCLAPI 镜像），纳入自动保存。</summary>
    private void VanillaSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        if (VanillaSourceBox?.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            ViewModel.SelectVanillaSource(tag);
        }
    }
}
