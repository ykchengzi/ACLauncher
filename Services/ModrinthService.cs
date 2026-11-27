using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACLauncher.Services;

/// <summary>Modrinth 搜索结果中的一个项目。</summary>
public class ModrinthProjectItem : System.ComponentModel.INotifyPropertyChanged
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private string? _translatedTitle;
    private string? _translatedDescription;

    /// <summary>展示标题（翻译后优先）。</summary>
    [JsonIgnore]
    public string DisplayTitle => _translatedTitle ?? Title;

    /// <summary>展示描述（翻译后优先）。</summary>
    [JsonIgnore]
    public string DisplayDescription => _translatedDescription ?? Description;

    /// <summary>图标 Uri（无图标时为 null）。</summary>
    [JsonIgnore]
    public Uri? IconUri => !string.IsNullOrWhiteSpace(IconUrl) &&
                           Uri.TryCreate(IconUrl, UriKind.Absolute, out var uri)
        ? uri
        : null;

    /// <summary>下载量展示文本。</summary>
    [JsonIgnore]
    public string DownloadsText => Downloads switch
    {
        >= 10_000_000 => $"{Downloads / 10_000_000.0:F1} 千万次下载",
        >= 10_000 => $"{Downloads / 10_000.0:F1} 万次下载",
        _ => $"{Downloads} 次下载",
    };

    /// <summary>应用翻译结果（刷新列表显示）。</summary>
    public void ApplyTranslation(string? title, string? description)
    {
        _translatedTitle = title;
        _translatedDescription = description;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayTitle)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayDescription)));
    }
}

/// <summary>
/// Modrinth 内容服务：搜索模组 / 资源包 / 光影，并按当前游戏版本与加载器安装到 .minecraft。
/// 接口文档：https://docs.modrinth.com/api/
/// </summary>
public class ModrinthService
{
    public static ModrinthService Instance { get; } = new();

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // 连接超时与整体超时分开设置：国内访问 Modrinth 偶尔会先卡在建连阶段，
        // 15 秒内建连失败比等满整体超时更快给出反馈。
        // IPv4 优先建连，避免 AAAA 记录不可达时耗满整个连接超时（见 HttpFactory）。
        var client = new HttpClient(HttpFactory.CreateHandler(TimeSpan.FromSeconds(15)))
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        // Modrinth 要求标识调用方
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ACLauncher/0.99.3 (Minecraft launcher)");
        return client;
    }

    /// <summary>
    /// GET 并读取响应文本：失败自动重试一次，异常统一转换成面向用户的中文提示。
    /// </summary>
    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await Http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(ct);
                }
                if ((int)response.StatusCode == 429)
                {
                    throw new InvalidOperationException("Modrinth 请求过于频繁，请稍后再试。");
                }
                last = new InvalidOperationException(
                    $"Modrinth 返回错误（HTTP {(int)response.StatusCode}），请稍后重试。");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient 超时在 .NET 中表现为未携带调用方令牌的取消
                last = new InvalidOperationException("Modrinth 请求超时，请检查网络后重试。");
            }
            catch (System.Net.Http.HttpRequestException)
            {
                last = new InvalidOperationException("无法连接 Modrinth，请检查网络后重试。");
            }
            catch (InvalidOperationException) when (last is not null)
            {
                throw; // 429 等已转换的提示直接抛出，不再重试
            }

            if (attempt == 0)
            {
                try { await Task.Delay(800, ct); }
                catch (OperationCanceledException) { throw; }
            }
        }

        throw last ?? new InvalidOperationException("Modrinth 请求失败，请稍后重试。");
    }

    /// <summary>搜索项目。projectType: mod / resourcepack / shader / modpack。</summary>
    public async Task<List<ModrinthProjectItem>> SearchAsync(string query, string projectType,
        string gameVersion, string? loader, int limit = 20, CancellationToken ct = default)
    {
        var (items, _) = await SearchPagedAsync(query, projectType, gameVersion, loader, 0, limit, ct: ct);
        return items;
    }

    /// <summary>
    /// 分页搜索：返回当前页项目与总条数（Modrinth total_hits）。
    /// category 为 Modrinth 类别 id（如 optimization / adventure / 16x），可为空。
    /// </summary>
    public async Task<(List<ModrinthProjectItem> Items, int TotalHits)> SearchPagedAsync(string query,
        string projectType, string gameVersion, string? loader, int offset, int limit = 20,
        string? category = null, CancellationToken ct = default)
    {
        var facets = new List<string> { $"[\"project_type:{projectType}\"]" };
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            facets.Add($"[\"versions:{gameVersion}\"]");
        }
        if (projectType == "mod" && !string.IsNullOrWhiteSpace(loader))
        {
            facets.Add($"[\"categories:{loader}\"]");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            facets.Add($"[\"categories:{category}\"]");
        }

        var url = "https://api.modrinth.com/v2/search" +
                  $"?query={Uri.EscapeDataString(query)}" +
                  $"&limit={limit}&offset={offset}&index=relevance" +
                  $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        using var doc = JsonDocument.Parse(await GetStringAsync(url, ct));

        var result = new List<ModrinthProjectItem>();
        var totalHits = 0;
        if (doc.RootElement.TryGetProperty("total_hits", out var totalEl) && totalEl.TryGetInt32(out var total))
        {
            totalHits = total;
        }
        if (doc.RootElement.TryGetProperty("hits", out var hits))
        {
            foreach (var hit in hits.EnumerateArray())
            {
                var item = JsonSerializer.Deserialize<ModrinthProjectItem>(hit.GetRawText());
                if (item is not null && !string.IsNullOrEmpty(item.ProjectId))
                {
                    // 净化显示文案：去掉 HTML 标签与 § 颜色码（避免把标记直接展示给用户）
                    item.Title = SanitizeText(item.Title);
                    item.Description = SanitizeText(item.Description);
                    result.Add(item);
                }
            }
        }
        return (result, totalHits);
    }

    /// <summary>去掉文案中的 HTML 标签与 Minecraft § 颜色码。</summary>
    private static string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, "§.", "");
        return cleaned.Trim();
    }

    // ===================== 详情页：版本列表与下载 =====================

    /// <summary>获取项目的全部版本（供详情页选择具体版本安装）。</summary>
    public async Task<List<ModrinthVersionItem>> GetVersionsAsync(string projectId, CancellationToken ct = default)
    {
        var url = $"https://api.modrinth.com/v2/project/{projectId}/version";
        using var doc = JsonDocument.Parse(await GetStringAsync(url, ct));

        var result = new List<ModrinthVersionItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var ver in doc.RootElement.EnumerateArray())
        {
            if (!ver.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;

            JsonElement? primary = null;
            foreach (var f in files.EnumerateArray())
            {
                if (f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True)
                {
                    primary = f;
                    break;
                }
                primary ??= f;
            }
            if (primary is null) continue;

            var fileUrl = primary.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
            var fileName = primary.Value.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
            if (string.IsNullOrEmpty(fileUrl) || string.IsNullOrEmpty(fileName)) continue;

            string? sha1 = null;
            if (primary.Value.TryGetProperty("hashes", out var hashes) &&
                hashes.TryGetProperty("sha1", out var sha1El))
            {
                sha1 = sha1El.GetString();
            }

            var gameVersions = new List<string>();
            if (ver.TryGetProperty("game_versions", out var gv) && gv.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gv.EnumerateArray())
                {
                    if (g.GetString() is { Length: > 0 } value) gameVersions.Add(value);
                }
            }

            result.Add(new ModrinthVersionItem
            {
                Id = ver.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                VersionNumber = ver.TryGetProperty("version_number", out var vn) ? vn.GetString() ?? "" : "",
                GameVersions = gameVersions,
                FileName = fileName,
                FileUrl = fileUrl,
                Sha1 = sha1,
                VersionType = ver.TryGetProperty("version_type", out var vt) ? vt.GetString() ?? "release" : "release",
            });
        }
        return result;
    }

    /// <summary>下载指定版本文件到用户选择的目标文件夹。</summary>
    public async Task DownloadVersionFileAsync(ModrinthVersionItem version, string targetFolder,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetFolder);
        status?.Report($"正在下载 {version.FileName}...");
        var task = new DownloadTask
        {
            Urls = new List<string> { version.FileUrl },
            LocalPath = Path.Combine(targetFolder, version.FileName),
            ExpectedSha1 = version.Sha1,
            DisplayName = version.FileName,
        };
        var ok = await DownloadService.Instance.DownloadOneAsync(task, cancellationToken: ct);
        if (!ok) throw new IOException($"{version.FileName} 下载失败。");
    }

    /// <summary>
    /// 安装项目的最新兼容版本（含必需依赖，一级递归）到目标目录。
    /// 返回安装成功的文件数。targetDir 为 mods / resourcepacks / shaderpacks 的完整路径。
    /// </summary>
    public async Task<int> InstallLatestAsync(ModrinthProjectItem project, string projectType,
        string targetDir, string gameVersion, string? loader,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return await InstallProjectRecursiveAsync(project.ProjectId, project.Title, projectType, targetDir,
            gameVersion, loader, visited, status, ct);
    }

    private async Task<int> InstallProjectRecursiveAsync(string projectId, string displayName, string projectType,
        string targetDir, string gameVersion, string? loader, HashSet<string> visited,
        IProgress<string>? status, CancellationToken ct)
    {
        if (!visited.Add(projectId)) return 0;

        var version = await GetLatestVersionAsync(projectId, gameVersion, projectType == "mod" ? loader : null, ct);
        if (version is null)
        {
            throw new InvalidOperationException($"{displayName} 没有适配 {gameVersion} 的版本。");
        }

        var file = version.FileUrl;
        var fileName = version.FileName;
        status?.Report($"正在安装 {displayName}（{fileName}）...");

        var task = new DownloadTask
        {
            Urls = new List<string> { file },
            LocalPath = Path.Combine(targetDir, fileName),
            ExpectedSha1 = version.Sha1,
            DisplayName = fileName,
        };
        var ok = await DownloadService.Instance.DownloadOneAsync(task, cancellationToken: ct);
        if (!ok) throw new IOException($"{displayName} 下载失败。");
        var installed = 1;

        // 必需依赖：一级递归安装
        foreach (var depId in version.RequiredDependencies)
        {
            try
            {
                installed += await InstallProjectRecursiveAsync(depId, $"依赖 {depId[..Math.Min(8, depId.Length)]}",
                    projectType, targetDir, gameVersion, loader, visited, status, ct);
            }
            catch (Exception ex)
            {
                LogService.Instance.Log($"依赖安装失败（{depId}）：{ex.Message}");
            }
        }

        return installed;
    }

    private sealed record VersionFile(string FileUrl, string FileName, string? Sha1, List<string> RequiredDependencies);

    /// <summary>查询项目在指定游戏版本 / 加载器下的最新版本并取出主文件。</summary>
    private static async Task<VersionFile?> GetLatestVersionAsync(string projectId, string gameVersion,
        string? loader, CancellationToken ct)
    {
        var url = $"https://api.modrinth.com/v2/project/{projectId}/version";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
        }
        if (!string.IsNullOrWhiteSpace(loader))
        {
            query.Add($"loaders={Uri.EscapeDataString($"[\"{loader}\"]")}");
        }
        if (query.Count > 0) url += "?" + string.Join("&", query);

        using var doc = JsonDocument.Parse(await GetStringAsync(url, ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        foreach (var ver in doc.RootElement.EnumerateArray())
        {
            if (!ver.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;

            JsonElement? primary = null;
            foreach (var f in files.EnumerateArray())
            {
                if (f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True)
                {
                    primary = f;
                    break;
                }
                primary ??= f;
            }
            if (primary is null) continue;

            var fileUrl = primary.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
            var fileName = primary.Value.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
            if (string.IsNullOrEmpty(fileUrl) || string.IsNullOrEmpty(fileName)) continue;

            string? sha1 = null;
            if (primary.Value.TryGetProperty("hashes", out var hashes) &&
                hashes.TryGetProperty("sha1", out var sha1El))
            {
                sha1 = sha1El.GetString();
            }

            var deps = new List<string>();
            if (ver.TryGetProperty("dependencies", out var depsEl) && depsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in depsEl.EnumerateArray())
                {
                    if (!d.TryGetProperty("dependency_type", out var t) || t.GetString() != "required") continue;
                    if (d.TryGetProperty("project_id", out var pid) && pid.GetString() is { Length: > 0 } id)
                    {
                        deps.Add(id);
                    }
                }
            }

            return new VersionFile(fileUrl, fileName, sha1, deps);
        }

        return null;
    }
}

/// <summary>项目的一个可下载版本（详情页列表项）。</summary>
public sealed record ModrinthVersionItem
{
    public string Id { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public List<string> GameVersions { get; init; } = new();
    public string FileName { get; init; } = "";
    public string FileUrl { get; init; } = "";
    public string? Sha1 { get; init; }

    /// <summary>Modrinth 版本通道：release / beta / alpha。</summary>
    public string VersionType { get; init; } = "release";

    /// <summary>通道标签：正式版 / 测试版 / 内测版。</summary>
    public string ChannelText => VersionType switch
    {
        "beta" => "测试版",
        "alpha" => "内测版",
        _ => "正式版",
    };

    /// <summary>列表展示：游戏版本 + 版本号 + 文件名。</summary>
    public string Display => $"{string.Join(" / ", GameVersions.Take(3))}  ·  {VersionNumber}  ·  {FileName}";
}
