using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ACLauncher.Services;

/// <summary>
/// 翻译服务：把英文内容（模组简介）翻译成简体中文。
/// 接口优先级（均为免费、无需 API Key）：
///   1. 腾讯交互翻译 transmart.qq.com —— 国内直连、支持批量，主通道；
///   2. Edge 翻译（edge.microsoft.com）—— 部分网络不可用；
///   3. Google 翻译公共接口 —— 部分网络不可用；
///   4. MyMemory —— 最后兜底（慢）。
/// 不可用的通道在本次运行期间会被标记跳过，避免每条内容都等待超时。
/// 结果按原文缓存在内存中。
/// </summary>
public class TranslationService
{
    public static TranslationService Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient ShortHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly Dictionary<string, string> Cache = new();
    private static string? _edgeToken;
    private static bool _edgeUnavailable;
    private static bool _googleUnavailable;

    /// <summary>把英文翻译成简体中文；失败返回 null。</summary>
    public async Task<string?> TranslateToChineseAsync(string text)
    {
        var batch = await TranslateBatchToChineseAsync(new[] { text });
        return batch.Count > 0 ? batch[0] : null;
    }

    /// <summary>
    /// 批量翻译：先查缓存；未翻译内容优先一次批量提交腾讯接口；
    /// 剩余条目才走兜底通道（带并发限制）。
    /// </summary>
    public async Task<List<string?>> TranslateBatchToChineseAsync(IReadOnlyList<string> texts)
    {
        var results = new string?[texts.Count];
        var pending = new List<(int Index, string Text)>();

        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (string.IsNullOrWhiteSpace(text) || !ContainsAsciiLetters(text))
            {
                results[i] = null;
                continue;
            }
            lock (Cache)
            {
                if (Cache.TryGetValue(text, out var cached))
                {
                    results[i] = cached;
                    continue;
                }
            }
            pending.Add((i, text));
        }

        if (pending.Count == 0) return results.ToList();

        // ---- 1) 腾讯批量接口（每批 50 条，国内通常 1-2 秒返回） ----
        const int batchSize = 50;
        for (var start = 0; start < pending.Count; start += batchSize)
        {
            var chunk = pending.Skip(start).Take(batchSize).ToList();
            var translated = await TranslateBatchViaTencentAsync(chunk.Select(c => c.Text).ToList());
            if (translated is null) break;

            for (var j = 0; j < chunk.Count; j++)
            {
                var value = SanitizeTranslated(translated[j]);
                if (string.IsNullOrWhiteSpace(value)) continue;
                results[chunk[j].Index] = value;
                lock (Cache)
                {
                    Cache[chunk[j].Text] = value;
                }
            }
        }

        // ---- 2) 兜底通道（仅处理腾讯仍未翻译的条目） ----
        var leftovers = pending.Where(p => results[p.Index] is null).ToList();
        if (leftovers.Count > 0)
        {
            // Edge 批量（整批一次）
            if (!_edgeUnavailable)
            {
                var edgeBatch = await TranslateBatchViaEdgeAsync(leftovers.Select(p => p.Text).ToList());
                if (edgeBatch is null)
                {
                    _edgeUnavailable = true;
                }
                else
                {
                    for (var j = 0; j < leftovers.Count; j++)
                    {
                        var value = SanitizeTranslated(edgeBatch[j]);
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        results[leftovers[j].Index] = value;
                        lock (Cache) Cache[leftovers[j].Text] = value;
                    }
                }
            }

            // 逐条 Google / MyMemory
            using var gate = new SemaphoreSlim(8);
            var fallbackTasks = leftovers.Where(p => results[p.Index] is null).Select(async p =>
            {
                await gate.WaitAsync();
                try
                {
                    string? value = null;
                    if (!_googleUnavailable)
                    {
                        value = await TranslateViaGoogleAsync(p.Text);
                        if (value is null) _googleUnavailable = true; // 一旦失败标记跳过，避免反复超时
                    }
                    value ??= await TranslateViaMyMemoryAsync(p.Text);
                    value = SanitizeTranslated(value);
                    if (string.IsNullOrWhiteSpace(value)) return;
                    results[p.Index] = value;
                    lock (Cache) Cache[p.Text] = value;
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();
            await Task.WhenAll(fallbackTasks);
        }

        return results.ToList();
    }

    /// <summary>清理翻译结果：去掉占位标签（如 &lt;g id="1070"&gt;）和 Minecraft 颜色码。</summary>
    private static string? SanitizeTranslated(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "§.", "");
        return cleaned.Trim();
    }

    private static bool ContainsAsciiLetters(string s)
    {
        foreach (var c in s)
        {
            if (char.IsAsciiLetter(c)) return true;
        }
        return false;
    }

    // ===================== 腾讯交互翻译（主通道，支持批量） =====================

    private static async Task<List<string?>?> TranslateBatchViaTencentAsync(IReadOnlyList<string> texts)
    {
        try
        {
            var payload = new
            {
                header = new { fn = "auto_translation", client_key = "browser-chrome-120.0.0-windows-10.0" },
                type = "plain",
                model_category = "normal",
                source = new { lang = "en", text_list = texts },
                target = new { lang = "zh" },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://transmart.qq.com/api/imt");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("auto_translation", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<string?>();
            foreach (var item in arr.EnumerateArray())
            {
                result.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : null);
            }
            return result.Count == texts.Count ? result : null;
        }
        catch
        {
            return null;
        }
    }

    // ===================== Edge 翻译（兜底） =====================

    private static async Task<List<string?>?> TranslateBatchViaEdgeAsync(IReadOnlyList<string> texts)
    {
        try
        {
            var token = _edgeToken;
            if (string.IsNullOrEmpty(token))
            {
                token = (await ShortHttp.GetStringAsync("https://edge.microsoft.com/translate/auth")).Trim();
                if (string.IsNullOrEmpty(token)) return null;
                _edgeToken = token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://api-edge.cognitive.microsofttranslator.com/translate" +
                "?from=en&to=zh-Hans&api-version=3.0&includeSentenceLength=false");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(texts.Select(t => new { Text = t })),
                Encoding.UTF8, "application/json");

            using var response = await ShortHttp.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _edgeToken = null;
                return null;
            }
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var result = new List<string?>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? text = null;
                if (item.TryGetProperty("translations", out var translations) &&
                    translations.ValueKind == JsonValueKind.Array &&
                    translations.GetArrayLength() > 0 &&
                    translations[0].TryGetProperty("text", out var textEl))
                {
                    text = textEl.GetString();
                }
                result.Add(text);
            }
            return result.Count == texts.Count ? result : null;
        }
        catch
        {
            return null;
        }
    }

    // ===================== Google 公共接口（兜底） =====================

    private static async Task<string?> TranslateViaGoogleAsync(string text)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single" +
                      $"?client=gtx&sl=en&tl=zh-CN&dt=t&q={Uri.EscapeDataString(text)}";
            using var doc = JsonDocument.Parse(await ShortHttp.GetStringAsync(url));
            var sb = new StringBuilder();
            foreach (var part in doc.RootElement[0].EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0)
                {
                    var piece = part[0].GetString();
                    if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    // ===================== MyMemory（最后兜底） =====================

    private static async Task<string?> TranslateViaMyMemoryAsync(string text)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get" +
                      $"?q={Uri.EscapeDataString(text)}&langpair=en|zh-CN";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
            return string.IsNullOrWhiteSpace(translated) ? null : translated;
        }
        catch
        {
            return null;
        }
    }
}
