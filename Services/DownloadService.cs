using System.Net;
using System.Security.Cryptography;

namespace ACLauncher.Services;

/// <summary>
/// 一个待下载的文件。
/// </summary>
public class DownloadTask
{
    /// <summary>候选下载地址（按顺序尝试，第一个成功即停止）。</summary>
    public List<string> Urls { get; init; } = new();

    /// <summary>本地保存路径（完整绝对路径）。</summary>
    public string LocalPath { get; init; } = "";

    /// <summary>期望的 SHA1（可为空，为空则跳过校验）。</summary>
    public string? ExpectedSha1 { get; init; }

    /// <summary>显示名称（日志用）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>是否下载成功。</summary>
    public bool Success { get; set; }

    /// <summary>是否已跳过（本地文件已存在且校验通过）。</summary>
    public bool Skipped { get; set; }
}

/// <summary>字节级下载进度（用于进度条 + 预计剩余时间）。</summary>
public record DownloadProgress(long ReceivedBytes, long TotalBytes, string FileName)
{
    /// <summary>百分比（0-100；总字节未知时为 -1，由 UI 显示为不定态）。</summary>
    public double Percent => TotalBytes > 0 ? Math.Min(100, ReceivedBytes * 100.0 / TotalBytes) : -1;
}

/// <summary>
/// 多线程下载服务。
/// 核心能力（对齐 PCL 设计）：
///  1. 每个文件多个候选 URL，按顺序尝试，全部失败才报错；
///  2. 下载到临时文件后做 SHA1 校验，通过才改名落盘（避免半成品污染）；
///  3. 全局信号量控制并发线程数（用户在“网络设置”中调整）。
/// </summary>
public class DownloadService
{
    public static DownloadService Instance { get; } = new();

    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// 批量下载文件。
    /// </summary>
    /// <param name="tasks">下载任务列表。</param>
    /// <param name="maxThreads">最大并发数（来自设置）。</param>
    /// <param name="progress">进度回调：(已完成数, 总数, 当前任务名)。</param>
    /// <param name="cancellationToken">外部取消令牌（可选；用户主动取消时立即停止全部下载）。</param>
    /// <returns>失败的任务列表（调用方可据此判断是否继续）。</returns>
    public async Task<List<DownloadTask>> DownloadManyAsync(IReadOnlyList<DownloadTask> tasks, int maxThreads,
        IProgress<(int done, int total, string current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tasks.Count == 0) return new List<DownloadTask>();

        maxThreads = Math.Clamp(maxThreads, 1, 16);
        using var semaphore = new SemaphoreSlim(maxThreads);
        var failed = new List<DownloadTask>();
        var completed = 0;
        var total = tasks.Count;

        var pending = tasks.Select(async task =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                progress?.Report((completed, total, task.DisplayName));
                var ok = await DownloadOneAsync(task, cancellationToken: cancellationToken);
                if (!ok) lock (failed) failed.Add(task);
            }
            finally
            {
                semaphore.Release();
                completed++;
                progress?.Report((completed, total, task.DisplayName));
            }
        }).ToList();

        await Task.WhenAll(pending);
        return failed;
    }

    /// <summary>
    /// 下载单个文件：依次尝试候选 URL，校验 SHA1 后落盘。
    /// 健壮性设计（对齐 PCL / HMCL）：
    ///  - 连接建立超时（SocketsHttpHandler.ConnectTimeout，15s）；
    ///  - 传输“停滞”看门狗：60 秒内没有收到任何数据即判定该连接卡死，换源/重连；
    ///  - 单文件整体超时 20 分钟（兼容慢网大文件）；
    ///  - 单一来源自动重试两轮，多个候选地址按顺序尝试。
    /// </summary>
    /// <param name="task">下载任务。</param>
    /// <param name="progress">字节级进度回调（可选）。</param>
    /// <param name="cancellationToken">外部取消令牌（可选；用户主动取消时立即抛出）。</param>
    public async Task<bool> DownloadOneAsync(DownloadTask task,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken? cancellationToken = null)
    {
        // 目标目录
        var dir = Path.GetDirectoryName(task.LocalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 本地已存在且校验通过 → 跳过
        if (File.Exists(task.LocalPath) && VerifySha1(task.LocalPath, task.ExpectedSha1))
        {
            task.Skipped = true;
            return true;
        }

        // 临时文件（同目录，避免跨卷）；先清理上次失败可能残留的 .part，避免被占用导致无法写入
        var tempPath = task.LocalPath + ".part";
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 被占用则让后续 FileStream 处理 */ }

        // 候选地址：整体重试两轮（单源文件也能在卡死后重连，多源文件按顺序尝试）
        var attemptUrls = task.Urls.Concat(task.Urls).ToList();
        var externalToken = cancellationToken ?? CancellationToken.None;

        foreach (var url in attemptUrls)
        {
            // 每个候选地址独立的超时预算
            using var totalCts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, totalCts.Token);
            // 停滞看门狗：挂在总令牌上，收到数据时不断重置为 60 秒
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            stallCts.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                LogService.Instance.Log($"下载：{task.DisplayName}");

                // 强制 HTTP/1.1：个别 CDN 在 HTTP/2 下偶发握手成功后长期不返回数据
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = HttpVersion.Version11,
                };
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stallCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    LogService.Instance.Log($"  - 地址不可用（{(int)response.StatusCode}）：{url}");
                    continue;
                }

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                await using var src = await response.Content.ReadAsStreamAsync(stallCts.Token);

                // FileShare.Read：允许杀软 / Defender 实时扫描读取，避免外部扫描抢锁
                var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                try
                {
                    // 按块拷贝并上报字节级进度（支持进度条与预计剩余时间）
                    var buffer = new byte[81920];
                    long received = 0;
                    while (true)
                    {
                        var read = await src.ReadAsync(buffer, stallCts.Token);
                        if (read == 0) break;
                        await dst.WriteAsync(buffer.AsMemory(0, read), stallCts.Token);
                        received += read;
                        // 收到数据：重置停滞看门狗
                        stallCts.CancelAfter(TimeSpan.FromSeconds(60));
                        progress?.Report(new DownloadProgress(received, totalBytes, task.DisplayName));
                    }
                    await dst.FlushAsync(stallCts.Token);
                }
                finally
                {
                    // 关键：写完必须释放写句柄，否则下方 SHA1 校验 / 改名 / 删除都会报“文件被占用”
                    await dst.DisposeAsync();
                }

                // SHA1 校验（期望值存在时）
                if (!string.IsNullOrWhiteSpace(task.ExpectedSha1))
                {
                    var actual = await ComputeSha1Async(tempPath);
                    if (!string.Equals(actual, task.ExpectedSha1, StringComparison.OrdinalIgnoreCase))
                    {
                        LogService.Instance.Log($"  - 校验失败（SHA1 不匹配）：{task.DisplayName}");
                        File.Delete(tempPath);
                        continue;
                    }
                }

                // 成功落盘（覆盖旧文件；若旧文件被占用，先删再改名）
                try
                {
                    File.Move(tempPath, task.LocalPath, overwrite: true);
                }
                catch (IOException)
                {
                    try { if (File.Exists(task.LocalPath)) File.Delete(task.LocalPath); } catch { /* 忽略 */ }
                    File.Move(tempPath, task.LocalPath);
                }
                task.Success = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消：立即终止，不再重试
                if (externalToken.IsCancellationRequested)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略 */ }
                    LogService.Instance.Log($"  - 已取消：{task.DisplayName}");
                    throw;
                }

                // 连接 / 传输停滞或整体超时：清理半成品后换源 / 重连
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略 */ }
                if (totalCts.IsCancellationRequested)
                    LogService.Instance.Log($"  - 下载超时（超过 20 分钟），正在更换地址重试：{task.DisplayName}");
                else
                    LogService.Instance.Log($"  - 传输长时间无响应（已停滞 60 秒），正在重新连接：{task.DisplayName}");
            }
            catch (Exception ex)
            {
                LogService.Instance.Log($"  - 下载失败：{url}\n    {ex.Message}");
                // 清理半成品，避免残留 .part 占用空间或导致下次下载冲突
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略 */ }
            }
        }

        // 全部失败，清理临时文件
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略 */ }
        return false;
    }

    /// <summary>计算文件的 SHA1（十六进制小写）。</summary>
    public static async Task<string> ComputeSha1Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>校验文件 SHA1（文件不存在返回 false）。</summary>
    public static bool VerifySha1(string path, string? expectedSha1)
    {
        if (string.IsNullOrWhiteSpace(expectedSha1)) return File.Exists(path);
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA1.HashData(stream);
            return string.Equals(Convert.ToHexString(hash).ToLowerInvariant(), expectedSha1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        // IPv4 优先建连（见 HttpFactory）：避免 AAAA 不可达时耗尽连接超时
        var client = new HttpClient(HttpFactory.CreateHandler(TimeSpan.FromSeconds(15)))
        {
            Timeout = TimeSpan.FromMinutes(20),
        };
        // 默认 HTTP/1.1，规避个别 CDN 在 HTTP/2 下握手成功后长时间不返回数据
        client.DefaultRequestVersion = HttpVersion.Version11;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ACLauncher/1.0");
        return client;
    }
}
