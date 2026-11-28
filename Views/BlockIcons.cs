using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ACLauncher.Views;

/// <summary>版本图标加载：支持 Blocks\ 下的贴图文件名，也支持整合包图标的绝对路径。</summary>
public static class BlockIcons
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按文件名（Blocks\ 下）或绝对路径加载图标；文件不存在时返回 null。</summary>
    public static ImageSource? Load(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        var rooted = Path.IsPathRooted(source);
        var path = rooted ? source : Path.Combine(AppContext.BaseDirectory, "Blocks", source);

        // 整合包图标可能随重新安装被替换，绝对路径不缓存
        if (!rooted && Cache.TryGetValue(path, out var cached)) return cached;

        try
        {
            if (!File.Exists(path)) return null;

            var image = new BitmapImage(new Uri(path));
            if (!rooted) Cache[path] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>XAML 函数绑定用到的小工具。</summary>
public static class XamlHelpers
{
    private static readonly SolidColorBrush ReleaseBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3C, 0x85, 0x27));
    private static readonly SolidColorBrush SnapshotBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xB2, 0x7C, 0x00));
    private static readonly SolidColorBrush AncientBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x75, 0x71, 0x6C));
    private static readonly SolidColorBrush BrokenBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));

    /// <summary>bool → Visibility（x:Bind 函数绑定）。</summary>
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>bool 取反 → Visibility（值为 true 时隐藏）。</summary>
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>渠道标签 → 文字颜色。</summary>
    public static Brush ChannelBrush(string? label) => label switch
    {
        "测试版" => SnapshotBrush,
        "远古版" => AncientBrush,
        "版本异常" => BrokenBrush,
        _ => ReleaseBrush,
    };
}
