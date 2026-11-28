using System.Net.Http;
using System.Text;
using System.Text.Json;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>Yggdrasil 外置登录服务器（内置 + 用户自定义）。</summary>
public class YggdrasilServer
{
    /// <summary>API 根地址（不含末尾斜杠），如 https://littleskin.cn/api/yggdrasil。</summary>
    public string ApiRoot { get; init; } = "";

    /// <summary>展示名。</summary>
    public string Name { get; init; } = "";

    public override string ToString() => Name;
}

/// <summary>外置登录服务器上的一个角色（Minecraft profile）。</summary>
public sealed record YggdrasilProfile(string Id, string Name);

/// <summary>Yggdrasil authenticate 的返回：令牌 + 该账号下的全部角色。</summary>
public sealed class YggdrasilAuthResult
{
    public string AccessToken { get; init; } = "";
    public string ClientToken { get; init; } = "";

    /// <summary>可用角色列表（selectedProfile 排在最前）。</summary>
    public List<YggdrasilProfile> Profiles { get; } = new();
}

/// <summary>
/// Yggdrasil 外置登录服务（Minecraft 第三方认证协议）。
/// 支持内置 LittleSkin 与用户自定义服务器：
///   POST {root}/authserver/authenticate   账号密码登录，返回 accessToken / clientToken / availableProfiles
///   POST {root}/authserver/refresh        用 accessToken 续期
///   POST {root}/authserver/validate       校验 accessToken 是否有效
/// 登录成功后账户以 Yggdrasil 类型保存，启动时通过 authlib-injector 参数接入游戏。
/// </summary>
public class YggdrasilService
{
    public static YggdrasilService Instance { get; } = new();

    /// <summary>内置 LittleSkin。</summary>
    public static readonly YggdrasilServer LittleSkin =
        new() { ApiRoot = "https://littleskin.cn/api/yggdrasil", Name = "LittleSkin" };

    private static readonly HttpClient Http = new(HttpFactory.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };

    // ===================== 记住账户 / 记住密码 =====================

    /// <summary>读取某服务器记住的登录信息；没有则返回 null。</summary>
    public SavedYggdrasilLogin? GetSavedCredentials(string apiRoot)
    {
        try
        {
            var settings = SettingsService.Instance.Load();
            var key = NormalizeRoot(apiRoot);
            if (settings.SavedYggdrasilLogins is not { } map) return null;

            foreach (var pair in map)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存登录信息：记住账户必定保存，密码仅在 rememberPassword 为 true 时保存。
    /// 未勾选记住账户时清除该服务器的记录。
    /// </summary>
    public void SaveCredentials(string apiRoot, string account, string password,
        bool rememberAccount, bool rememberPassword)
    {
        try
        {
            var settings = SettingsService.Instance.Load();
            settings.SavedYggdrasilLogins ??= new Dictionary<string, SavedYggdrasilLogin>(StringComparer.OrdinalIgnoreCase);

            var key = NormalizeRoot(apiRoot);
            var existingKey = settings.SavedYggdrasilLogins.Keys
                .FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (existingKey is not null) settings.SavedYggdrasilLogins.Remove(existingKey);

            if (rememberAccount)
            {
                settings.SavedYggdrasilLogins[key] = new SavedYggdrasilLogin
                {
                    Account = account,
                    Password = rememberPassword ? password : "",
                };
            }

            SettingsService.Instance.Save(settings);
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"保存外置登录信息失败：{ex.Message}");
        }
    }

    private static string NormalizeRoot(string apiRoot) => apiRoot.Trim().TrimEnd('/');

    /// <summary>
    /// 用账号密码登录外置服务器，返回令牌与该账号下的全部角色（不挑选角色）。
    /// </summary>
    public async Task<YggdrasilAuthResult> AuthenticateAsync(YggdrasilServer server, string email,
        string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server.ApiRoot))
        {
            throw new InvalidOperationException("外置登录服务器地址为空。");
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("邮箱/用户名和密码不能为空。");
        }

        var body = new
        {
            agent = new { name = "Minecraft", version = 1 },
            username = email.Trim(),
            password,
            requestUser = false,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            server.ApiRoot.TrimEnd('/') + "/authserver/authenticate");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.UserAgent.ParseAdd("ACLauncher/0.99.3");

        using var response = await Http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ParseYggdrasilError(json, "登录失败"));
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.TryGetProperty("accessToken", out var at) ? at.GetString() ?? "" : "";
        var clientToken = root.TryGetProperty("clientToken", out var ctEl) ? ctEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new InvalidOperationException("登录失败：服务器未返回访问令牌。");
        }

        var result = new YggdrasilAuthResult { AccessToken = accessToken, ClientToken = clientToken };

        // selectedProfile 排在最前，其余角色随后（去重）
        if (root.TryGetProperty("selectedProfile", out var sel) && sel.ValueKind == JsonValueKind.Object)
        {
            var id = sel.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var name = sel.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
            {
                result.Profiles.Add(new YggdrasilProfile(id, name));
            }
        }
        if (root.TryGetProperty("availableProfiles", out var profiles) &&
            profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in profiles.EnumerateArray())
            {
                var id = p.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";
                var name = p.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                if (result.Profiles.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                result.Profiles.Add(new YggdrasilProfile(id, name));
            }
        }

        return result;
    }

    /// <summary>把登录结果中的一个角色组装成待保存的账户。</summary>
    public Account CreateAccount(YggdrasilServer server, YggdrasilAuthResult auth, YggdrasilProfile profile)
    {
        return new Account
        {
            Type = AccountType.Yggdrasil,
            PlayerName = profile.Name,
            Uuid = profile.Id,
            AccessToken = auth.AccessToken,
            RefreshToken = auth.ClientToken, // 复用字段保存 clientToken
            AuthServer = server.ApiRoot,
            AuthServerName = server.Name,
            ExpiresAt = DateTimeOffset.MaxValue, // Yggdrasil 无固定过期时间，靠 validate 判断
        };
    }

    /// <summary>
    /// 用账号密码登录外置服务器；返回账户（含 accessToken / Uuid / PlayerName）。
    /// 多角色时优先使用服务器返回的 selectedProfile（需要选择时请改用 AuthenticateAsync）。
    /// </summary>
    public async Task<Account> LoginAsync(YggdrasilServer server, string email, string password,
        CancellationToken ct = default)
    {
        var auth = await AuthenticateAsync(server, email, password, ct);
        var profile = auth.Profiles.FirstOrDefault();
        if (profile is null)
        {
            throw new InvalidOperationException(
                $"登录成功，但该账号在 {server.Name} 尚未创建角色。请先到网站创建一个 Minecraft 角色后再登录。");
        }
        return CreateAccount(server, auth, profile);
    }

    /// <summary>用已保存的令牌续期；失败返回 false。</summary>
    public async Task<bool> RefreshAsync(Account account, CancellationToken ct = default)
    {
        if (account.Type != AccountType.Yggdrasil || string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return false;
        }

        try
        {
            var body = new
            {
                accessToken = account.AccessToken,
                clientToken = account.RefreshToken,
                requestUser = false,
            };
            using var request = new HttpRequestMessage(HttpMethod.Post,
                account.AuthServer.TrimEnd('/') + "/authserver/refresh");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd("ACLauncher/0.99.3");

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("accessToken", out var at) && at.GetString() is { Length: > 0 } token)
            {
                account.AccessToken = token;
            }
            if (root.TryGetProperty("selectedProfile", out var sel) && sel.ValueKind == JsonValueKind.Object)
            {
                if (sel.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } id)
                    account.Uuid = id;
                if (sel.TryGetProperty("name", out var nEl) && nEl.GetString() is { Length: > 0 } n)
                    account.PlayerName = n;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验令牌是否仍然有效。
    /// 注意：不少第三方外置登录源没有实现 /authserver/validate（返回 404 / 405 / 501），
    /// 这并不代表令牌失效，因此只有明确的“令牌无效”（403）才返回 false；
    /// 接口不存在、网络异常、服务端故障等情况返回 true（交给游戏内 authlib-injector 判定），
    /// 避免出现“重新登录也没用、始终提示令牌失效”的误报。
    /// </summary>
    public async Task<bool> ValidateAsync(Account account, CancellationToken ct = default)
    {
        if (account.Type != AccountType.Yggdrasil || string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                account.AuthServer.TrimEnd('/') + "/authserver/validate");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { accessToken = account.AccessToken, clientToken = account.RefreshToken }),
                Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd("ACLauncher/0.99.3");

            using var response = await Http.SendAsync(request, ct);

            // 2xx：令牌有效
            if (response.IsSuccessStatusCode) return true;

            var status = (int)response.StatusCode;

            // 403：协议规定的“令牌无效”
            if (status == 403) return false;

            // 接口未实现 / 服务端故障 / 其它异常状态：不能据此判定令牌失效
            LogService.Instance.Log($"外置登录：validate 返回 HTTP {status}，跳过令牌校验。");
            return true;
        }
        catch (Exception ex)
        {
            // 网络异常同样不代表令牌失效
            LogService.Instance.Log($"外置登录：validate 请求失败（{ex.Message}），跳过令牌校验。");
            return true;
        }
    }

    /// <summary>从 Yggdrasil 错误响应中提取可读信息。</summary>
    private static string ParseYggdrasilError(string json, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errorMessage", out var msg) && msg.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch { /* 非 JSON */ }
        return fallback;
    }
}
