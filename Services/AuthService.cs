using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACLauncher.Helpers;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>
/// 认证服务：Microsoft 正版登录（OAuth 2.0 设备码流程）+ 离线模式登录 + 账户管理。
///
/// Microsoft 登录链路（设备码）：
///   MSA 设备码授权 → MSA Access Token → Xbox Live Token → XSTS Token
///   → Minecraft Services Token → 玩家 Profile（UUID + 玩家名）
/// </summary>
public class AuthService
{
    public static AuthService Instance { get; } = new();

    // =====================================================================
    // Microsoft OAuth Client ID：
    //   - 默认使用 Prism Launcher 公开注册的客户端 ID（GitHub 开源仓库
    //     PrismLauncher/PrismLauncher CMakeLists.txt，已获得微软/Mojang 授权，
    //     可直接完成 Minecraft Java 版登录，实测设备码端点可用）。
    //   - 高级用户可在"设置"页填写自己的 Azure 应用 Client ID 覆盖默认值：
    //     1. 打开 https://portal.azure.com → "Microsoft Entra ID" → "应用注册"；
    //     2. 新建注册：受支持的账户类型选 "个人 Microsoft 账户"；
    //     3. 平台配置：添加 "移动和桌面应用程序"，重定向 URI 填 http://localhost；
    //     4. 记下 "应用程序(客户端) ID"，填入设置页。
    // =====================================================================
    private const string DefaultClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";

    /// <summary>生效的 Client ID：设置中配置了非空值则优先，否则用内置默认值。</summary>
    private static string EffectiveClientId
    {
        get
        {
            var custom = SettingsService.Instance.Load().MicrosoftClientId;
            return string.IsNullOrWhiteSpace(custom) ? DefaultClientId : custom.Trim();
        }
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>账户文件路径（与启动器 exe 同级）。</summary>
    public string AccountsPath => Path.Combine(AppContext.BaseDirectory, "accounts.json");

    /// <summary>全部已保存账户。</summary>
    public List<Account> Accounts { get; private set; } = new();

    /// <summary>当前选中账户。</summary>
    public Account? CurrentAccount { get; private set; }

    /// <summary>账户列表或当前账户发生变化时触发（UI 线程刷新）。</summary>
    public event Action? AccountsChanged;

    private record StoredAccounts(List<Account> Accounts, string? CurrentAccountId);

    /// <summary>从本地加载账户列表与上次登录的账户。</summary>
    public async Task LoadAsync()
    {
        if (!File.Exists(AccountsPath))
        {
            Accounts = new List<Account>();
            CurrentAccount = null;
            return;
        }

        try
        {
            var data = await JsonHelper.DeserializeFromFileAsync<StoredAccounts>(AccountsPath);
            Accounts = data?.Accounts ?? new List<Account>();
            var currentId = data?.CurrentAccountId;
            CurrentAccount = Accounts.FirstOrDefault(a => a.Id == currentId);
        }
        catch
        {
            // 账户文件损坏时重置，不影响启动器使用
            Accounts = new List<Account>();
            CurrentAccount = null;
        }
    }

    /// <summary>保存账户列表与当前账户到本地。</summary>
    public async Task SaveAsync()
    {
        var data = new StoredAccounts(Accounts, CurrentAccount?.Id);
        await JsonHelper.SerializeToFileAsync(AccountsPath, data);
    }

    /// <summary>
    /// Microsoft 设备码登录。
    /// </summary>
    /// <param name="onDeviceCode">设备码就绪时的回调（UI 展示给用户）。</param>
    /// <param name="ct">取消令牌（用户在 UI 上取消时触发）。</param>
    public async Task<Account> LoginMicrosoftAsync(Action<string> onDeviceCode, CancellationToken ct)
    {
        var clientId = EffectiveClientId;

        // ---------- 1. 申请设备码 ----------
        var deviceForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "XboxLive.signin offline_access",
        });

        using var deviceResp = await Http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", deviceForm, ct);
        deviceResp.EnsureSuccessStatusCode();

        var deviceJson = JsonDocument.Parse(await deviceResp.Content.ReadAsStringAsync(ct)).RootElement;
        var userCode = deviceJson.GetProperty("user_code").GetString() ?? "";
        var deviceCode = deviceJson.GetProperty("device_code").GetString() ?? "";
        var interval = Math.Max(deviceJson.GetProperty("interval").GetInt32(), 5);

        // 通知 UI 展示设备码
        onDeviceCode(userCode);

        // ---------- 2. 轮询等待用户授权 ----------
        string msaAccessToken;
        string msaRefreshToken;
        var expiresIn = 3600;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval * 1000, ct);

            var pollForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = deviceCode,
            });

            using var pollResp = await Http.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token", pollForm, ct);
            var pollJson = JsonDocument.Parse(await pollResp.Content.ReadAsStringAsync(ct)).RootElement;

            if (!pollResp.IsSuccessStatusCode)
            {
                var error = pollJson.GetProperty("error").GetString();
                switch (error)
                {
                    case "authorization_pending":
                        continue; // 用户尚未完成授权，继续等待
                    case "authorization_declined":
                        throw new InvalidOperationException("用户拒绝了授权。");
                    case "expired_token":
                        throw new InvalidOperationException("设备码已过期，请重新发起登录。");
                    case "bad_verification_code":
                        throw new InvalidOperationException("设备码校验失败，请重试。");
                    default:
                        throw new InvalidOperationException($"Microsoft 登录失败：{error}");
                }
            }

            msaAccessToken = pollJson.GetProperty("access_token").GetString() ?? "";
            msaRefreshToken = pollJson.GetProperty("refresh_token").GetString() ?? "";
            expiresIn = pollJson.GetProperty("expires_in").GetInt32();
            break;
        }

        // ---------- 3. 兑换 Minecraft 令牌并获取玩家资料 ----------
        var (mcToken, uhs, xuid) = await ExchangeToMinecraftTokenAsync(msaAccessToken, ct);
        var (uuid, playerName) = await FetchProfileAsync(mcToken, ct);

        var account = new Account
        {
            Type = AccountType.Microsoft,
            PlayerName = playerName,
            Uuid = uuid,
            AccessToken = mcToken,
            RefreshToken = msaRefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Xuid = xuid,
        };

        // 同一 Microsoft 账户（Uuid 相同）重复登录时更新已有记录，避免出现重复条目
        var existing = Accounts.FirstOrDefault(a =>
            a.Type == AccountType.Microsoft && string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.PlayerName = account.PlayerName;
            existing.AccessToken = account.AccessToken;
            existing.RefreshToken = account.RefreshToken;
            existing.ExpiresAt = account.ExpiresAt;
            existing.Xuid = account.Xuid;
            CurrentAccount = existing;
            await SaveAsync();
            AccountsChanged?.Invoke();
            return existing;
        }

        Accounts.Add(account);
        CurrentAccount = account;
        await SaveAsync();
        AccountsChanged?.Invoke();
        return account;
    }

    /// <summary>
    /// 使用 Refresh Token 自动刷新 Microsoft 账户（Access Token 过期时调用）。
    /// </summary>
    public async Task<Account> RefreshAsync(Account account)
    {
        if (account.Type != AccountType.Microsoft || string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            throw new InvalidOperationException("该账户不是可刷新的 Microsoft 账户。");
        }

        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = EffectiveClientId,
            ["refresh_token"] = account.RefreshToken,
            ["scope"] = "XboxLive.signin offline_access",
        });

        using var resp = await Http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token", refreshForm);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Refresh Token 已失效，请重新登录该账户。");
        }

        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var msaAccessToken = json.GetProperty("access_token").GetString() ?? "";
        var newRefreshToken = json.GetProperty("refresh_token").GetString() ?? "";
        var expiresIn = json.GetProperty("expires_in").GetInt32();

        // 重新走 Xbox / Minecraft 兑换链
        var (mcToken, uhs, xuid) = await ExchangeToMinecraftTokenAsync(msaAccessToken, CancellationToken.None);
        var (uuid, playerName) = await FetchProfileAsync(mcToken, CancellationToken.None);

        account.AccessToken = mcToken;
        account.RefreshToken = newRefreshToken;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        account.Uuid = uuid;
        account.PlayerName = playerName;
        account.Xuid = xuid;

        await SaveAsync();
        AccountsChanged?.Invoke();
        return account;
    }

    /// <summary>检查 Microsoft 账户令牌是否临近过期，临近则自动刷新。</summary>
    public async Task EnsureValidAsync(Account account)
    {
        // 外置登录：先用 validate 检查，失效则尝试 refresh
        if (account.Type == AccountType.Yggdrasil)
        {
            if (await YggdrasilService.Instance.ValidateAsync(account)) return;
            if (await YggdrasilService.Instance.RefreshAsync(account))
            {
                await SaveAsync();
                return;
            }
            throw new InvalidOperationException(
                $"外置登录（{account.AuthServerName}）令牌已失效，请重新登录该账户。");
        }

        if (account.Type != AccountType.Microsoft) return;
        if (string.IsNullOrWhiteSpace(account.AccessToken)) return;
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return;
        await RefreshAsync(account);
    }

    /// <summary>
    /// 创建离线账户（无需网络）。
    /// </summary>
    public Account CreateOfflineAccount(string playerName)
    {
        var name = playerName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("玩家名不能为空。");
        }
        if (name.Length < 3 || name.Length > 16)
        {
            throw new ArgumentException("玩家名长度需为 3-16 个字符。");
        }
        // Minecraft Java 版用户名规则：仅字母、数字、下划线
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]+$"))
        {
            throw new ArgumentException("玩家名只能包含英文字母、数字和下划线（_），不能有中文、空格或特殊符号。");
        }
        // 重名检查（不区分大小写）
        if (Accounts.Any(a => string.Equals(a.PlayerName, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"已存在名为“{name}”的账户，请换一个名字。");
        }

        var account = new Account
        {
            Type = AccountType.Offline,
            PlayerName = name,
            Uuid = GetOfflineUuid(name),
            AccessToken = "",
            RefreshToken = "",
        };

        Accounts.Add(account);
        CurrentAccount = account;
        _ = SaveAsync();
        AccountsChanged?.Invoke();
        return account;
    }

    /// <summary>
    /// 保存（或更新）一个外置登录账户并设为当前账户。
    /// </summary>
    public async Task SaveExternalAccountAsync(Account account)
    {
        var existing = Accounts.FirstOrDefault(a =>
            a.Type == AccountType.Yggdrasil &&
            string.Equals(a.AuthServer, account.AuthServer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            Accounts.Add(account);
            CurrentAccount = account;
        }
        else
        {
            existing.PlayerName = account.PlayerName;
            existing.Uuid = account.Uuid;
            existing.AccessToken = account.AccessToken;
            existing.RefreshToken = account.RefreshToken;
            existing.AuthServer = account.AuthServer;
            existing.AuthServerName = account.AuthServerName;
            CurrentAccount = existing;
        }

        await SaveAsync();
        AccountsChanged?.Invoke();
    }

    /// <summary>切换当前账户。</summary>
    public void SetCurrent(Account account)
    {
        CurrentAccount = account;
        _ = SaveAsync();
        AccountsChanged?.Invoke();
    }

    /// <summary>删除账户（若为当前账户则清空当前选择）。</summary>
    public void Delete(Account account)
    {
        Accounts.Remove(account);
        if (ReferenceEquals(CurrentAccount, account))
        {
            CurrentAccount = Accounts.FirstOrDefault();
        }
        _ = SaveAsync();
        AccountsChanged?.Invoke();
    }

    /// <summary>
    /// 根据玩家名生成离线 UUID（Mojang 离线模式约定：MD5("OfflinePlayer:" + 玩家名)）。
    /// </summary>
    public static string GetOfflineUuid(string playerName)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        // 设置版本 3（UUID v3，名称空间方式）与变体位
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }

    // ---------- 内部：Xbox / Minecraft 令牌兑换 ----------

    private static async Task<(string McToken, string Uhs, string Xuid)> ExchangeToMinecraftTokenAsync(
        string msaToken, CancellationToken ct)
    {
        // --- Xbox Live 认证 ---
        var xblBody = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + msaToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        });

        using var xblReq = new HttpRequestMessage(HttpMethod.Post, "https://user.auth.xboxlive.com/user/authenticate")
        {
            Content = new StringContent(xblBody, Encoding.UTF8, "application/json"),
        };
        xblReq.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");

        using var xblResp = await Http.SendAsync(xblReq, ct);
        if (!xblResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Xbox Live 认证失败（HTTP {(int)xblResp.StatusCode}），请确认该 Microsoft 账户可正常登录。");
        }
        var xblJson = JsonDocument.Parse(await xblResp.Content.ReadAsStringAsync(ct)).RootElement;
        var xblToken = xblJson.GetProperty("Token").GetString() ?? "";

        // --- XSTS 认证 ---
        var xstsBody = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        });

        using var xstsReq = new HttpRequestMessage(HttpMethod.Post, "https://xsts.auth.xboxlive.com/xsts/authorize")
        {
            Content = new StringContent(xstsBody, Encoding.UTF8, "application/json"),
        };
        xstsReq.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");

        using var xstsResp = await Http.SendAsync(xstsReq, ct);
        if (!xstsResp.IsSuccessStatusCode)
        {
            var errText = await xstsResp.Content.ReadAsStringAsync(ct);
            long xErr = 0;
            try
            {
                // XErr 错误码以 uint 形式返回（如 2148916233），使用 long 读取
                if (JsonDocument.Parse(errText).RootElement.TryGetProperty("XErr", out var xe))
                {
                    xErr = xe.GetInt64();
                }
            }
            catch { }
            throw new InvalidOperationException(xErr switch
            {
                2148916233L => "该账户无法使用 Xbox Live（未成年人账户需在 Xbox 官网接受服务条款）。",
                2148916235L => "该账户没有 Xbox 主机在线服务（Xbox Live），无法用于 Minecraft。",
                2148916238L => "该账户尚未成年，需由家长在 Xbox 账户设置中授权。",
                _ => $"Xbox Live 授权失败（HTTP {(int)xstsResp.StatusCode}" +
                     (xErr != 0 ? $"，错误码 {xErr}" : "") + "）。",
            });
        }
        var xstsJson = JsonDocument.Parse(await xstsResp.Content.ReadAsStringAsync(ct)).RootElement;
        var xstsToken = xstsJson.GetProperty("Token").GetString() ?? "";
        var xui = xstsJson.GetProperty("DisplayClaims").GetProperty("xui")[0];
        var uhs = xui.GetProperty("uhs").GetString() ?? "";
        var xuid = xui.TryGetProperty("xid", out var xid) ? xid.GetString() ?? "" : "";

        // --- Minecraft Services 登录 ---
        var mcBody = JsonSerializer.Serialize(new
        {
            identityToken = $"XBL3.0 x={uhs};{xstsToken}",
        });

        using var mcResp = await Http.PostAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            new StringContent(mcBody, Encoding.UTF8, "application/json"), ct);
        if (!mcResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Minecraft 服务认证失败，请确认该账户已购买 Minecraft Java 版。");
        }
        var mcJson = JsonDocument.Parse(await mcResp.Content.ReadAsStringAsync(ct)).RootElement;
        var mcToken = mcJson.GetProperty("access_token").GetString() ?? "";

        return (mcToken, uhs, xuid);
    }

    /// <summary>获取玩家资料（UUID + 玩家名）。</summary>
    private static async Task<(string Uuid, string PlayerName)> FetchProfileAsync(string mcToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);

        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("该 Microsoft 账户未购买《我的世界》Java 版，无法登录。");
        }
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("获取玩家资料失败，请稍后重试。");
        }

        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
        var uuid = json.GetProperty("id").GetString() ?? "";
        var name = json.GetProperty("name").GetString() ?? "";
        return (uuid, name);
    }
}
