using System.Text.Json;

namespace ACLauncher.Services;

/// <summary>
/// 每日一言：从 dddn.net 的公开接口取当天的句子，用于主页副标题。
/// 接口返回 JSON：{"ok":true,"data":{"date":"…","zh":"…","en":"…"}}。
/// 取不到时返回 null，界面回退到默认文案。
/// </summary>
public static class DailyQuoteService
{
    private const string ApiUrl = "https://dddn.net/api.php?action=today";

    private static readonly HttpClient Http = new(HttpFactory.CreateHandler(TimeSpan.FromSeconds(8)))
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private static string? _cachedDate;
    private static string? _cachedQuote;

    /// <summary>取今天的句子（同一天只请求一次）。失败返回 null。</summary>
    public static async Task<string?> GetTodayAsync()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_cachedDate == today && !string.IsNullOrEmpty(_cachedQuote)) return _cachedQuote;

        try
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("ACLauncher/0.99.3");
            var json = await Http.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                return null;

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("zh", out var zh) || zh.GetString() is not { Length: > 0 } quote)
            {
                return null;
            }

            _cachedDate = today;
            _cachedQuote = quote;
            return quote;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"获取每日一言失败：{ex.Message}");
            return null;
        }
    }
}
