using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ACLauncher.Models;

/// <summary>
/// 账户类型。
/// </summary>
public enum AccountType
{
    /// <summary>Microsoft 正版账户（MSA）。</summary>
    Microsoft,

    /// <summary>离线账户（盗版/单人），仅需自定义用户名。</summary>
    Offline,

    /// <summary>第三方外置登录（Yggdrasil 协议，如 LittleSkin）。</summary>
    Yggdrasil,
}

/// <summary>
/// 账户模型。持久化到启动器目录下的 accounts.json。
/// </summary>
public class Account : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isCurrent;
    /// <summary>本地唯一标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>账户类型。</summary>
    public AccountType Type { get; set; }

    /// <summary>玩家名。</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>玩家 UUID（离线账户为根据用户名生成的 UUID）。</summary>
    public string Uuid { get; set; } = "";

    /// <summary>Microsoft 账户的 Access Token（离线账户为空）。</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Microsoft 账户的 Refresh Token（用于自动续期）。</summary>
    public string RefreshToken { get; set; } = "";

    /// <summary>Access Token 过期时间（离线账户忽略）。</summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Xbox 用户标识（xuid），可选。</summary>
    public string Xuid { get; set; } = "";

    /// <summary>
    /// 外置登录（Yggdrasil）服务器 API 根地址，如 https://littleskin.cn/api/yggdrasil。
    /// 仅 Yggdrasil 类型账户使用。
    /// </summary>
    public string AuthServer { get; set; } = "";

    /// <summary>外置登录服务器的展示名（如 "LittleSkin"），仅 UI 展示。</summary>
    public string AuthServerName { get; set; } = "";

    /// <summary>是否为当前账户（仅 UI 展示用，不持久化）。</summary>
    [JsonIgnore]
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
    }

    private bool _isSelected;

    /// <summary>是否被点选（仅 UI 展示用，不持久化；选中卡片描边高亮）。</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    private string? _headImagePath;

    /// <summary>
    /// 头像图片的本地路径（仅 UI 展示用，不持久化；由 SkinService 异步生成）。
    /// 为空时界面显示默认头像占位。
    /// </summary>
    [JsonIgnore]
    public string? HeadImagePath
    {
        get => _headImagePath;
        set
        {
            if (!string.Equals(_headImagePath, value, StringComparison.Ordinal))
            {
                _headImagePath = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeadImagePath)));
            }
        }
    }

    /// <summary>展示名称。</summary>
    [JsonIgnore]
    public string DisplayName => Type switch
    {
        AccountType.Microsoft => $"{PlayerName}（Microsoft）",
        AccountType.Yggdrasil => $"{PlayerName}（{TypeDisplay}）",
        _ => $"{PlayerName}（离线）",
    };

    /// <summary>账户类型中文名。</summary>
    [JsonIgnore]
    public string TypeDisplay => Type switch
    {
        AccountType.Microsoft => "Microsoft 账户",
        AccountType.Yggdrasil => string.IsNullOrEmpty(AuthServerName) ? "外置登录" : $"外置登录 · {AuthServerName}",
        _ => "离线账户",
    };
}
