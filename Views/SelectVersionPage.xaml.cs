using ACLauncher.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace ACLauncher.Views;

/// <summary>
/// 选择版本页：三栏卡片列出本地已安装版本。
/// 左键选中（选中项高亮、底部按钮启用），右键进入该版本的版本管理页。
/// </summary>
public sealed partial class SelectVersionPage : Page
{
    public SelectVersionViewModel? ViewModel { get; private set; }

    // 卡片状态画刷（静态缓存，避免每次绑定新建对象）
    private static readonly SolidColorBrush SelectedBorderBrush =
        new(new Windows.UI.Color { A = 0xE8, R = 0x79, G = 0x1E, B = 0x00 });
    private static readonly SolidColorBrush NormalBorderBrush =
        new(new Windows.UI.Color { A = 0x22, R = 0x00, G = 0x00, B = 0x00 });
    private static readonly SolidColorBrush SelectedBackgroundBrush =
        new(new Windows.UI.Color { A = 0x14, R = 0xE8, G = 0x79, B = 0x1E });
    private static readonly SolidColorBrush NormalBackgroundBrush =
        new(new Windows.UI.Color { A = 0x0C, R = 0x00, G = 0x00, B = 0x00 });

    public SelectVersionPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SelectVersionViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            _ = vm.LoadAsync();
        }
        else if (e.Parameter is null)
        {
            // 从版本管理页返回时未携带 VM：复用新实例并加载
            ViewModel = new SelectVersionViewModel();
            DataContext = ViewModel;
            _ = ViewModel.LoadAsync();
        }
    }

    /// <summary>卡片边框：选中橙色 / 未选中淡描边。</summary>
    public static Brush GetCardBorder(bool isSelected) => isSelected ? SelectedBorderBrush : NormalBorderBrush;

    /// <summary>卡片背景：选中淡橙 / 未选中极淡灰。</summary>
    public static Brush GetCardBackground(bool isSelected) => isSelected ? SelectedBackgroundBrush : NormalBackgroundBrush;

    /// <summary>按版本名取条目（ItemsRepeater 不设置 DataContext，用 Tag 传版本名）。</summary>
    private InstalledVersionItem? FindItem(object sender) =>
        sender is FrameworkElement { Tag: string id }
            ? ViewModel?.Versions.FirstOrDefault(v => v.Id == id)
            : null;

    /// <summary>搜索框输入：实时过滤版本卡片。</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SearchText = SearchBox.Text;
    }

    /// <summary>左键点按卡片：选中为待操作版本。</summary>
    private void VersionCard_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (FindItem(sender) is { } item)
        {
            ViewModel.SelectCard(item);
        }
    }

    /// <summary>双击版本卡片：直接设为当前版本。</summary>
    private void VersionCard_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (FindItem(sender) is { } item)
        {
            ViewModel.SelectCard(item);
            if (ViewModel.SelectCommand.CanExecute(null)) ViewModel.SelectCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>右键卡片：进入该版本的版本管理页。</summary>
    private void VersionCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = FindItem(sender);
        if (item is null) return;
        e.Handled = true;
        Frame.Navigate(typeof(VersionManagePage), item.Id);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedVersion is not { } selected) return;

        var isCurrent = selected.IsCurrent;
        var confirm = new ContentDialog
        {
            Title = "删除版本",
            Content = $"确定要删除版本「{selected.Id}」吗？\n\n该版本的文件将从磁盘删除；共享的库与资源文件不受影响，其他版本仍可正常启动。"
                + (isCurrent ? "\n\n这是当前版本，删除后会自动切换到剩余的版本。" : ""),
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteAsync();
        }
    }
}
