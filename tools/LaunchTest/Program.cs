using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ACLauncher.Models;
using ACLauncher.Services;

// =====================================================================
// LaunchTest —— 非 GUI 的端到端验证 harness（控制台）
// 直接链接生产源码（见 LaunchTest.csproj 的 <Compile Include>），验证的是与交付物同一份代码。
// 用途：在无法使用 GUI 自动化时，真实调用 整合包安装 / 游戏启动 全链路。
//
// 用法：
//   LaunchTest.exe install <整合包解压目录>          仅安装整合包
//   LaunchTest.exe launch  <版本目录名> [观察秒数]    启动并观察（默认 120s）
//   LaunchTest.exe cancel  <版本目录名> [取消延迟秒数] 启动后中途取消（默认 8s 后取消）
//   LaunchTest.exe all     <整合包解压目录> [观察秒数] 安装后启动
//
// 日志：运行目录下 launchtest.log（UTF-8，实时追加）；stdout 重定向到文件时中文可能乱码，以日志为准。
// 测试结束后 java 进程需外部清理：
//   Get-CimInstance Win32_Process -Filter "Name='java.exe'" |
//     Where-Object { $_.CommandLine -match 'KnotClient|net.minecraft|minecraft' } |
//     ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
// =====================================================================

// 实时打印启动器日志
var logLock = new object();
LogService.Instance.LineAdded += line =>
{
    Console.WriteLine(line);
    // 立即落盘，便于后台运行时观察（进度回调来自线程池，需串行化写入）
    lock (logLock)
    {
        try { File.AppendAllText(LaunchLogPath(), line + "\n", Encoding.UTF8); }
        catch (IOException) { /* 文件被占用时丢弃这一行，不影响测试 */ }
    }
};

var cmd = args.Length > 0 ? args[0] : "all";

try
{
    if (cmd == "install")
    {
        var extractDir = args[1];
        var ver = await PackInstallService.Instance.TryInstallPackAsync(extractDir, "SadEPa的整合包 (26.2)");
        Console.WriteLine($"INSTALL_RESULT version={ver ?? "<null-通用合并>"}");
    }
    else if (cmd == "launch")
    {
        var version = args[1];
        var seconds = args.Length > 2 ? int.Parse(args[2]) : 120;
        await LaunchAndObserve(version, seconds);
    }
    else if (cmd == "ygglaunch")
    {
        // 外置登录启动链路验证：Yggdrasil 账户 -> authlib-injector 下载与 javaagent 注入
        var version = args.Length > 1 ? args[1] : "1.21.11";
        var seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
        var baseOptions = await BuildLaunchOptionsAsync(version);
        var options = new LaunchOptions
        {
            Version = baseOptions.Version,
            Account = new Account
            {
                Type = AccountType.Yggdrasil,
                PlayerName = "ACLTest",
                Uuid = "00000000000000000000000000000000",
                AccessToken = "invalid-token-for-smoke-test",
                RefreshToken = "invalid-client",
                AuthServer = YggdrasilService.LittleSkin.ApiRoot,
                AuthServerName = "LittleSkin",
            },
            JavaPath = baseOptions.JavaPath,
            MinMemoryMB = baseOptions.MinMemoryMB,
            MaxMemoryMB = baseOptions.MaxMemoryMB,
            CheckResources = baseOptions.CheckResources,
            GamePriority = baseOptions.GamePriority,
        };
        await GameLaunchService.Instance.LaunchAsync(options);
        Console.WriteLine("YGG_LAUNCH_RETURNED_OK");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(2000);
            if (!GameLaunchService.Instance.IsRunning) break;
        }
        Console.WriteLine(GameLaunchService.Instance.IsRunning
            ? "YGG_GAME_STILL_RUNNING -> 注入后游戏进程存活"
            : "YGG_GAME_EXITED（注入参数或令牌问题，详见日志）");
        var jar = Path.Combine(ConfigService.Instance.MinecraftDirectory, "authlib-injector", "authlib-injector.jar");
        Console.WriteLine("AUTHLIB_JAR " + (File.Exists(jar) ? $"{new FileInfo(jar).Length} bytes" : "MISSING"));
    }
    else if (cmd == "cancel")
    {
        var version = args[1];
        var delaySeconds = args.Length > 2 ? int.Parse(args[2]) : 8;
        await LaunchAndCancel(version, delaySeconds);
    }
    else if (cmd == "vanilla")
    {
        // 官方原版安装验证（Mojang 清单 + 核心 JAR + 依赖库，含镜像回退）
        var version = args[1];
        Console.WriteLine($"VANILLA_INSTALL {version}");
        await PackInstallService.Instance.InstallVanillaVersionAsync(version);
        Console.WriteLine("VANILLA_OK");
    }
    else if (cmd == "netdiag")
    {
        var host = args.Length > 1 ? args[1] : "api.modrinth.com";
        try
        {
            foreach (var a in System.Net.Dns.GetHostAddresses(host))
                Console.WriteLine($"DNS {host} -> {a} ({a.AddressFamily})");
        }
        catch (Exception ex) { Console.WriteLine("DNS_FAIL " + ex.Message); }

        async Task TryAsync(string label, System.Net.Http.HttpClient c)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var resp = await c.GetAsync($"https://{host}/v2/search?query=sodium&limit=1");
                var body = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"{label}: HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms len={body.Length}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: FAILED in {sw.ElapsedMilliseconds}ms {ex.GetType().Name} {ex.Message}");
            }
        }

        using (var h = new System.Net.Http.SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        using (var c = new System.Net.Http.HttpClient(h) { Timeout = TimeSpan.FromSeconds(20) })
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ACLauncher/0.99.3 (Minecraft launcher)");
            await TryAsync("noproxy", c);
        }
        using (var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) })
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ACLauncher/0.99.3 (Minecraft launcher)");
            await TryAsync("default", c);
        }
        using (var h = new System.Net.Http.SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = System.Net.DecompressionMethods.All })
        using (var c = new System.Net.Http.HttpClient(h) { Timeout = TimeSpan.FromSeconds(45) })
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ACLauncher/0.99.3 (Minecraft launcher)");
            await TryAsync("current-config", c);
        }
    }
    else if (cmd == "modrinth")
    {
        // Modrinth 搜索验证：modrinth <关键词> <类型:mod|resourcepack|shader> <游戏版本> [加载器]
        var query = args.Length > 1 ? args[1] : "sodium";
        var type = args.Length > 2 ? args[2] : "mod";
        var ver = args.Length > 3 ? args[3] : "26.2";
        var loader = args.Length > 4 ? args[4] : (type == "mod" ? "fabric" : null);
        var results = await ModrinthService.Instance.SearchAsync(query, type, ver, loader);
        Console.WriteLine($"SEARCH {type} '{query}' mc={ver} loader={loader ?? "-"} -> {results.Count} 个结果");
        foreach (var r in results.Take(5))
        {
            Console.WriteLine($"  {r.Title}  |  {r.DownloadsText}");
        }
    }
    else if (cmd == "translate")
    {
        // 批量翻译计时验证：translate <关键词>（默认取 20 条模组简介）
        var query = args.Length > 1 ? args[1] : "sodium";
        var projects = await ModrinthService.Instance.SearchAsync(query, "mod", "26.2", "fabric");
        var descs = projects.Take(20).Select(p => p.Description).ToList();
        var sw = Stopwatch.StartNew();
        var translated = await TranslationService.Instance.TranslateBatchToChineseAsync(descs);
        Console.WriteLine($"TRANSLATE_BATCH count={descs.Count} elapsed={sw.ElapsedMilliseconds}ms");
        for (var i = 0; i < Math.Min(3, translated.Count); i++)
        {
            Console.WriteLine($"  {descs[i]}");
            Console.WriteLine($"  -> {translated[i] ?? "<null>"}");
        }
    }
    else if (cmd == "searchfilter")
    {
        // 筛选验证：searchfilter <关键词> <类型> <游戏版本> <加载器|-> <类别|->
        var q = args.Length > 1 ? args[1] : "sodium";
        var t = args.Length > 2 ? args[2] : "mod";
        var gv = args.Length > 3 ? args[3] : "1.20.1";
        var ld = args.Length > 4 && args[4] != "-" ? args[4] : null;
        var cat = args.Length > 5 && args[5] != "-" ? args[5] : null;
        var (items, total) = await ModrinthService.Instance.SearchPagedAsync(q, t, gv, ld, 0, 5, cat);
        Console.WriteLine($"SEARCH q={q} type={t} ver={gv} loader={ld ?? "-"} cat={cat ?? "-"} total={total}");
        foreach (var it in items.Take(3)) Console.WriteLine($"  {it.Title} | {it.DownloadsText}");
    }
    else if (cmd == "javas")
    {
        var javas = await JavaService.Instance.DetectAsync();
        Console.WriteLine($"JAVAS {javas.Count} 个");
        foreach (var j in javas.Take(8)) Console.WriteLine($"  {j.Version} (major {j.MajorVersion})  {j.Path}");
    }
    else if (cmd == "yggdrasil")
    {
        try
        {
            await YggdrasilService.Instance.LoginAsync(YggdrasilService.LittleSkin, "invalid@example.com", "wrongpass");
            Console.WriteLine("YGGDRASIL_LOGIN_UNEXPECTED_SUCCESS");
        }
        catch (Exception ex)
        {
            Console.WriteLine("YGGDRASIL_ERROR " + ex.Message);
        }

        // 自定义源（无效域名）应给出可读错误而不是崩溃
        try
        {
            await YggdrasilService.Instance.LoginAsync(
                new YggdrasilServer { ApiRoot = "https://nonexistent.invalid/api/yggdrasil", Name = "Bad" },
                "a@b.c", "x");
            Console.WriteLine("YGG_BAD_UNEXPECTED_SUCCESS");
        }
        catch (Exception ex)
        {
            Console.WriteLine("YGG_BAD_ERROR " + ex.GetType().Name + ": " + ex.Message);
        }

        // 无效令牌的 validate / refresh 应返回 false
        var fake = new Account { Type = AccountType.Yggdrasil, AuthServer = YggdrasilService.LittleSkin.ApiRoot, AuthServerName = "LittleSkin", AccessToken = "invalid-token", RefreshToken = "invalid-client" };
        Console.WriteLine("YGG_VALIDATE " + await YggdrasilService.Instance.ValidateAsync(fake));
        Console.WriteLine("YGG_REFRESH " + await YggdrasilService.Instance.RefreshAsync(fake));
    }
    else if (cmd == "updatecheck")
    {
        // 更新清单解析验证：updatecheck <manifestUrl>
        UpdateService.ManifestUrl = args.Length > 1 ? args[1] : UpdateService.ManifestUrl;
        Console.WriteLine($"CURRENT {UpdateService.Instance.CurrentVersion} manifest={UpdateService.ManifestUrl}");
        var info = await UpdateService.Instance.CheckAsync();
        if (info is null) Console.WriteLine("NO_UPDATE");
        else Console.WriteLine($"UPDATE version={info.Version} url={info.Url} sha256={(info.Sha256 ?? "-")} mandatory={info.Mandatory}");
    }
    else if (cmd == "loader")
    {
        // 加载器版本解析：loader <mc> <fabric|forge|neoforge>
        var mcVer = args.Length > 1 ? args[1] : "1.20.1";
        var kind = args.Length > 2 ? args[2] : "fabric";
        var ver = await LoaderService.Instance.GetLatestLoaderVersionAsync(mcVer, kind);
        Console.WriteLine($"LOADER {kind} for {mcVer}: {ver ?? "<未找到>"}");
    }
    else if (cmd == "icon")
    {
        // 版本图标解析：icon <版本名>
        var ver = args.Length > 1 ? args[1] : "";
        var versionsDir = ConfigService.Instance.VersionsDirectory;
        Console.WriteLine($"LOADER {ConfigService.Instance.GetVersionLoaderLabel(ver)}");
        Console.WriteLine($"CHANNEL {VersionChannelService.Label(VersionChannelService.DetectInstalled(ver, versionsDir))}");
        Console.WriteLine($"ICON {VersionIconService.Resolve(ver)}");
    }
    else if (cmd == "iso")
    {
        // 版本隔离解析：iso <版本名>
        var ver = args.Length > 1 ? args[1] : "";
        Console.WriteLine($"MODE {VersionIsolationService.ToValue(VersionIsolationService.GetGlobalMode())}");
        Console.WriteLine($"SETTINGS_EXISTED {SettingsService.Instance.LastLoadHadFile}");
        if (ver.Length > 0)
        {
            Console.WriteLine($"OVERRIDE {VersionIsolationService.GetOverride(ver) ?? "<follow>"}");
            Console.WriteLine($"ISOLATED {VersionIsolationService.IsIsolated(ver)}");
            Console.WriteLine($"GAMEDIR {VersionIsolationService.GetGameDirectory(ver)}");
            Console.WriteLine($"MODSDIR {VersionIsolationService.GetModsDirectory(ver)}");
        }
    }
    else if (cmd == "modversions")
    {
        // 项目版本列表：modversions <projectId>
        var pid = args.Length > 1 ? args[1] : "";
        var versions = await ModrinthService.Instance.GetVersionsAsync(pid);
        Console.WriteLine($"VERSIONS {pid}: {versions.Count} 个");
        foreach (var v in versions.Take(3)) Console.WriteLine($"  {v.Display}");
    }
    else if (cmd == "watch")
    {
        // 游戏窗口检测：watch <进程PID> [超时秒数]
        var pid = int.Parse(args[1]);
        var timeout = args.Length > 2 ? int.Parse(args[2]) : 60;
        using var target = System.Diagnostics.Process.GetProcessById(pid);
        Console.WriteLine($"WATCH pid={pid} class-agnostic window detection");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var state = await GameWindowWatcher.WaitAsync(target,
                msg => Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F0}s] {msg}"), cts.Token);
            Console.WriteLine($"RESULT {state} after {sw.Elapsed.TotalSeconds:F1}s");
            Environment.ExitCode = state == GameWindowState.Appeared ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"RESULT Timeout after {sw.Elapsed.TotalSeconds:F1}s");
            Environment.ExitCode = 2;
        }
    }
    else if (cmd == "count")
    {
        // 启动次数与买正版提醒节点：count <次数>（在 harness 自己的 settings.json 上累加）
        var times = args.Length > 1 ? int.Parse(args[1]) : 1;
        Console.WriteLine($"SETTINGS {SettingsService.Instance.SettingsPath}");
        for (var i = 0; i < times; i++)
        {
            var (count, milestone) = LaunchCounterService.RegisterLaunch();
            Console.WriteLine($"LAUNCH {count} milestone={(milestone?.ToString() ?? "-")}");
        }
    }
    else if (cmd == "status")
    {
        // 服务器在线状态检测（主页服务器卡的底层服务）
        var host = args.Length > 1 ? args[1] : ServerStatusService.DefaultQueryHost;
        var port = args.Length > 2 ? int.Parse(args[2]) : ServerStatusService.DefaultQueryPort;
        Console.WriteLine($"QUERY {host}:{port}");
        var sw = Stopwatch.StartNew();
        var st = await ServerStatusService.Instance.QueryAsync(host, port);
        Console.WriteLine($"STATUS online={st.Online} latency={st.LatencyMs}ms total={sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"PLAYERS {st.PlayersOnline}/{st.PlayersMax}");
        Console.WriteLine($"VERSION {st.VersionName}");
        Console.WriteLine($"MOTD {st.Motd}");
        Environment.ExitCode = st.Online ? 0 : 1;
    }
    else if (cmd == "all")
    {
        var extractDir = args[1];
        var seconds = args.Length > 2 ? int.Parse(args[2]) : 120;
        var ver = await PackInstallService.Instance.TryInstallPackAsync(extractDir, "SadEPa的整合包 (26.2)");
        Console.WriteLine($"INSTALL_RESULT version={ver ?? "<null-通用合并>"}");
        if (string.IsNullOrWhiteSpace(ver))
        {
            Console.WriteLine("安装返回 null（通用合并格式），无法自动启动。");
            return;
        }
        await LaunchAndObserve(ver!, seconds);
    }
}
catch (Exception ex)
{
    Console.WriteLine("FATAL: " + ex);
    File.AppendAllText(LaunchLogPath(), "FATAL: " + ex + "\n", Encoding.UTF8);
    Environment.ExitCode = 2;
}

static string LaunchLogPath() => Path.Combine(AppContext.BaseDirectory, "launchtest.log");

static string OfflineUuid(string name)
{
    using var md5 = MD5.Create();
    var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
    hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // UUID v3
    hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // IETF variant
    var hex = Convert.ToHexString(hash).ToLowerInvariant();
    return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
}

static async Task LaunchAndObserve(string version, int seconds)
{
    var options = await BuildLaunchOptionsAsync(version);

    await GameLaunchService.Instance.LaunchAsync(options);
    Console.WriteLine("LAUNCH_RETURNED_OK");

    // 观察窗口：进程提前退出则报告退出码；持续运行到窗口结束视为成功
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        await Task.Delay(2000);
        if (!GameLaunchService.Instance.IsRunning)
        {
            Console.WriteLine("GAME_EXITED_EARLY（详见日志中的退出代码）");
            return;
        }
    }

    if (GameLaunchService.Instance.IsRunning)
    {
        Console.WriteLine("GAME_STILL_RUNNING_AFTER_WINDOW -> 启动成功（已进入游戏窗口/加载阶段）");
        Console.WriteLine("（测试结束后由外部脚本清理 java 进程）");
    }
}

static async Task<LaunchOptions> BuildLaunchOptionsAsync(string version)
{
    var javas = await JavaService.Instance.DetectAsync();
    var javaPath = javas.FirstOrDefault(j => !string.IsNullOrWhiteSpace(j.Path))?.Path;
    if (string.IsNullOrWhiteSpace(javaPath))
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        javaPath = !string.IsNullOrWhiteSpace(home) ? Path.Combine(home, "bin", "java.exe") : "java.exe";
    }
    Console.WriteLine($"USING_JAVA={javaPath}");

    var account = new Account
    {
        Type = AccountType.Offline,
        PlayerName = "ACLTest",
        Uuid = OfflineUuid("ACLTest"),
        AccessToken = "0",
    };

    return new LaunchOptions
    {
        Version = version,
        Account = account,
        JavaPath = javaPath,
        MinMemoryMB = 1024,
        MaxMemoryMB = 4096,
        CheckResources = true,
        GamePriority = "普通",
    };
}

/// <summary>
/// 取消验证：启动后等待 delaySeconds 调用 CancelLaunch()，
/// 验证 LaunchAsync 返回 false、进程被终止、状态为“已取消启动”。
/// </summary>
static async Task LaunchAndCancel(string version, int delaySeconds)
{
    var options = await BuildLaunchOptionsAsync(version);

    var launchTask = GameLaunchService.Instance.LaunchAsync(options);

    Console.WriteLine($"CANCEL_WAIT_SECONDS={delaySeconds}");
    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

    // 取消时机可能在进程拉起前（解析/下载阶段）或后（等待窗口阶段），都应能中止
    GameLaunchService.Instance.CancelLaunch();

    var started = await launchTask;

    Console.WriteLine($"CANCEL_RESULT launchReturned={started} isRunning={GameLaunchService.Instance.IsRunning}");

    // 给终止一点时间，确认没有残留的游戏 java 进程
    await Task.Delay(3000);
    var remaining = Process.GetProcessesByName("java")
        .Concat(Process.GetProcessesByName("javaw"))
        .Where(p =>
        {
            try
            {
                return p.MainModule!.FileName!.Contains(".minecraft", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        })
        .ToList();

    foreach (var p in remaining)
    {
        try { p.Kill(entireProcessTree: true); } catch { /* 竞态退出 */ }
    }

    if (!started && !GameLaunchService.Instance.IsRunning && remaining.Count == 0)
    {
        Console.WriteLine("CANCEL_OK（启动已取消，游戏进程已终止）");
    }
    else
    {
        Console.WriteLine($"CANCEL_FAILED remainingJava={remaining.Count}");
        Environment.ExitCode = 3;
    }
}
