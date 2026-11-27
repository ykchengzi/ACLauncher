using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACLauncher.Helpers;
using ACLauncher.Models;

namespace ACLauncher.Services;

/// <summary>
/// 一次游戏启动所需的全部参数（由 MainViewModel 汇总配置与账户信息后传入）。
/// </summary>
public class LaunchOptions
{
    /// <summary>版本号（来自 config.json）。</summary>
    public string Version { get; init; } = "";

    /// <summary>当前账户。</summary>
    public Account Account { get; init; } = new();

    /// <summary>Java 可执行文件路径。</summary>
    public string JavaPath { get; init; } = "";

    /// <summary>JVM 最小内存（MB）。</summary>
    public double MinMemoryMB { get; init; } = 1024;

    /// <summary>JVM 最大内存（MB）。</summary>
    public double MaxMemoryMB { get; init; } = 4096;

    /// <summary>额外 JVM 参数（空格分隔的字符串）。</summary>
    public string ExtraJvmArgs { get; init; } = "";

    /// <summary>额外游戏参数（空格分隔的字符串）。</summary>
    public string ExtraGameArgs { get; init; } = "";

    /// <summary>游戏进程优先级（"普通" / "高" / "低" / "最高" / "最低"）。</summary>
    public string GamePriority { get; init; } = "普通";

    /// <summary>启动前是否检查并补全缺失文件。</summary>
    public bool CheckResources { get; init; } = true;
}

/// <summary>
/// 游戏启动核心服务。
/// 流程：解析版本 JSON → 解压 Native 库 → 构建 ClassPath → 构建 JVM/游戏参数 → 启动 Java 进程。
/// </summary>
public class GameLaunchService
{
    public static GameLaunchService Instance { get; } = new();

    /// <summary>启动状态变化事件（如 "正在解析版本信息..."、"游戏已启动"）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 启动进度变化事件：null 表示不定态（解析 / 构建参数等阶段），0~1 为资源补全 / 运行时下载的确定进度。
    /// </summary>
    public event Action<double?>? LaunchProgressChanged;

    /// <summary>是否正在运行游戏。</summary>
    public bool IsRunning { get; private set; }

    private CancellationTokenSource? _launchCts;
    private bool _cancelRequested;

    /// <summary>游戏运行状态变化事件：窗口弹出（true）/ 进程退出（false）。</summary>
    public event Action<bool>? GameRunningChanged;

    private bool _isGameRunning;

    /// <summary>游戏窗口已弹出、进程正在运行（“强制结束”按钮据此显示）。</summary>
    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value) return;
            _isGameRunning = value;
            GameRunningChanged?.Invoke(value);
        }
    }

    private Process? _process;

    /// <summary>强制结束游戏进程（整个进程树）。</summary>
    public void KillGame()
    {
        var p = _process;
        if (p is null) return;
        try
        {
            p.Refresh();
            if (p.HasExited) return;
            LogService.Instance.Log("正在强制结束游戏进程...");
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"强制结束游戏失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 取消正在进行的启动：中止解析 / 下载等步骤；若游戏进程已拉起但窗口尚未弹出，则终止整个进程树。
    /// 游戏窗口已弹出后不可取消（此时交给“强制结束游戏”）。
    /// </summary>
    public void CancelLaunch()
    {
        if (!IsRunning || IsGameRunning) return;

        var cts = _launchCts;
        if (cts is null || cts.IsCancellationRequested) return;

        _cancelRequested = true;
        SetStatus("正在取消启动...");
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 启动流程刚结束，无需取消
            return;
        }

        var p = _process;
        if (p is { HasExited: false })
        {
            try
            {
                LogService.Instance.Log("正在终止游戏进程...");
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                LogService.Instance.Log($"终止游戏进程失败：{ex.Message}");
            }
        }
    }

    private void SetStatus(string status)
    {
        LogService.Instance.Log(status);
        StatusChanged?.Invoke(status);
    }

    // =====================================================================
    // Java Launch Wrapper（按需启用，修复 UTF-8 Beta 下 Java 命令行乱码）
    // =====================================================================

    private static readonly ConcurrentDictionary<string, int> JavaMajorCache = new();

    /// <summary>
    /// 判断是否需要内置的 Java Launch Wrapper（java_launch_wrapper.jar，MIT，Copyright (c) 2022 00ll00）。
    /// 需要时返回 jar 绝对路径，否则返回 null。
    /// 启用条件：系统开启了“Beta：使用 Unicode UTF-8 提供全球语言支持”（ACP/OEMCP=65001），
    /// 且 Java 主版本早于 19（JDK-8272352 在 Java 19 修复）。检测失败时保守启用。
    /// </summary>
    private string? ResolveWrapperJarIfNeeded(string javaPath)
    {
        try
        {
            if (!IsUtf8BetaEnabled())
            {
                return null; // 正常系统：无需包装，保持命令行干净
            }

            var major = TryGetJavaMajorVersion(javaPath);
            if (major is >= 19)
            {
                return null; // Java 19+ 已修复该编码缺陷
            }

            var jar = Path.Combine(AppContext.BaseDirectory, "java_launch_wrapper", "java_launch_wrapper.jar");
            if (File.Exists(jar))
            {
                LogService.Instance.Log("检测到系统开启 UTF-8 Beta 且 Java 版本早于 19，启用 Java Launch Wrapper。");
                return jar;
            }

            LogService.Instance.Log("需要 Java Launch Wrapper，但未找到内置文件，已跳过。");
            return null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"Java Launch Wrapper 检测异常，已跳过：{ex.Message}");
            return null;
        }
    }

    /// <summary>读取系统代码页，判断是否开启 UTF-8 Beta（ACP 或 OEMCP 为 65001）。</summary>
    private static bool IsUtf8BetaEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Nls\CodePage");
            if (key is null) return false;
            var acp = key.GetValue("ACP")?.ToString();
            var oemcp = key.GetValue("OEMCP")?.ToString();
            return acp == "65001" || oemcp == "65001";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>运行 java -version 解析主版本号（结果缓存）。无法解析返回 null。</summary>
    private static int? TryGetJavaMajorVersion(string javaPath)
    {
        if (JavaMajorCache.TryGetValue(javaPath, out var cached)) return cached;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-version");

            using var p = Process.Start(psi);
            if (p is null) return null;
            // java -version 输出在 stderr
            var stderr = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var text = stderr + "\n" + stdout;

            var match = Regex.Match(text, @"version\s+""(\d+)(?:\.(\d+))?");
            if (!match.Success) return null;

            int major;
            if (match.Groups[1].Value == "1" && match.Groups[2].Success)
            {
                major = int.Parse(match.Groups[2].Value); // 1.8 -> 8
            }
            else
            {
                major = int.Parse(match.Groups[1].Value); // 11 / 17 / 21
            }

            JavaMajorCache[javaPath] = major;
            return major;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 选择满足版本要求的 Java 可执行文件：
    ///  1. 版本 JSON 声明了 javaVersion（如 26.2 要求 Java 25）时，优先使用用户指定且满足要求的 Java；
    ///  2. 否则在本机检测到的 Java 中挑选满足要求的最高版本；
    ///  3. 本机都不满足时，自动下载 Mojang 官方运行时（java-runtime-epsilon 等）。
    /// 旧版本未声明 javaVersion 时，沿用用户指定 / 本机最高版本。
    /// </summary>
    private async Task<string> ResolveJavaAsync(VersionJson versionJson, LaunchOptions options, CancellationToken ct)
    {
        var requiredMajor = versionJson.JavaVersion?.MajorVersion ?? 0;
        var component = versionJson.JavaVersion?.Component;

        // 收集本机可用 Java：用户指定优先，再加上自动检测结果
        var candidatePaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.JavaPath)) candidatePaths.Add(options.JavaPath);
        try
        {
            var detected = await JavaService.Instance.DetectAsync();
            candidatePaths.AddRange(detected.Select(j => j.Path));
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"检测本机 Java 失败：{ex.Message}");
        }

        // 解析各候选的主版本号
        var candidates = new List<(string Path, int Major)>();
        foreach (var p in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(p)) continue;
            var major = TryGetJavaMajorVersion(p);
            if (major is { } m) candidates.Add((p, m));
        }

        // 有明确版本要求：优先满足要求的候选（用户指定优先，其次版本最高）
        if (requiredMajor > 0)
        {
            var ok = candidates.Where(c => c.Major >= requiredMajor).ToList();
            if (ok.Count > 0)
            {
                // 用户指定的若满足则优先
                var userOk = ok.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(options.JavaPath) &&
                    c.Path.Equals(options.JavaPath, StringComparison.OrdinalIgnoreCase));
                if (userOk.Path is not null)
                {
                    LogService.Instance.Log($"使用用户指定的 Java {userOk.Major}：{userOk.Path}");
                    return userOk.Path;
                }

                var best = ok.OrderByDescending(c => c.Major).First();
                LogService.Instance.Log($"该版本要求 Java {requiredMajor}+，使用本机 Java {best.Major}：{best.Path}");
                return best.Path;
            }

            // 本机不满足：自动下载官方运行时
            if (string.IsNullOrWhiteSpace(component))
            {
                var have = candidates.Count > 0 ? string.Join("、", candidates.Select(c => c.Major)) : "无";
                throw new InvalidOperationException(
                    $"该版本要求 Java {requiredMajor} 或更高版本，但本机仅有 Java {have}，且未提供可自动下载的运行时组件。\n请安装 Java {requiredRequired(requiredMajor)} 后在设置中选择。");
            }

            var haveMsg = candidates.Count > 0 ? string.Join("、", candidates.Select(c => c.Major)) : "无";
            SetStatus($"该版本要求 Java {requiredMajor}，本机仅有 {haveMsg}，正在自动下载 Java 运行时...");
            LogService.Instance.Log($"本机 Java（{haveMsg}）不满足要求（{requiredMajor}+），开始自动下载 {component}。");

            var runtimeJavaw = await JavaRuntimeService.Instance.EnsureRuntimeAsync(component,
                new Progress<(int done, int total, string current)>(p =>
                {
                    LaunchProgressChanged?.Invoke(p.total == 0 ? null : (double?)p.done / p.total);
                    if (p.done % 20 == 0 || p.done == p.total)
                    {
                        SetStatus($"正在下载 Java 运行时：{p.done}/{p.total}");
                    }
                }), ct);

            // 带日志重定向启动时优先使用 bin/java.exe（无窗口且可读取输出）
            var runtimeJava = Path.Combine(Path.GetDirectoryName(runtimeJavaw)!, "java.exe");
            return File.Exists(runtimeJava) ? runtimeJava : runtimeJavaw;
        }

        // 旧版本无明确要求：用户指定优先，否则本机最高
        if (candidates.Count > 0)
        {
            var user = candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(options.JavaPath) &&
                c.Path.Equals(options.JavaPath, StringComparison.OrdinalIgnoreCase));
            if (user.Path is not null) return user.Path;
            return candidates.OrderByDescending(c => c.Major).First().Path;
        }

        if (!string.IsNullOrWhiteSpace(options.JavaPath))
        {
            return options.JavaPath; // 无法解析版本但用户显式指定，交予启动阶段报错
        }

        throw new InvalidOperationException("未找到可用的 Java，请在设置中选择 Java 可执行文件，或安装 Java 后重试。");
    }

    private static string requiredRequired(int major) => major switch
    {
        >= 25 => "25",
        21 => "21",
        17 => "17",
        8 => "8",
        _ => major.ToString(),
    };

    /// <summary>
    /// 启动游戏（异步，不阻塞 UI 线程）。
    /// 返回 true 表示游戏进程已进入运行（窗口已弹出或等待超时但进程仍在运行）；
    /// 返回 false 表示窗口弹出前进程已退出（退出信息已通过 StatusChanged 推送），或启动被用户取消。
    /// </summary>
    /// <param name="options">启动参数。</param>
    /// <param name="cancellationToken">外部取消令牌（可选，配合 CancelLaunch 使用）。</param>
    /// <exception cref="InvalidOperationException">游戏已在运行。</exception>
    /// <exception cref="FileNotFoundException">必要文件缺失。</exception>
    /// <exception cref="FormatException">版本 JSON 格式错误。</exception>
    public async Task<bool> LaunchAsync(LaunchOptions options, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("游戏已在运行中，请勿重复启动。");
        }

        IsRunning = true;
        _cancelRequested = false;
        _launchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _launchCts.Token;
        LaunchProgressChanged?.Invoke(null);
        try
        {
            var minecraftDir = ConfigService.Instance.MinecraftDirectory;
            if (!Directory.Exists(minecraftDir))
            {
                throw new DirectoryNotFoundException($"未找到 .minecraft 目录：{minecraftDir}");
            }

            // ---------- 1. 定位并解析版本 JSON ----------
            SetStatus("正在解析版本信息...");
            var versionJson = await VersionParserService.Instance.ParseAsync(options.Version);
            ct.ThrowIfCancellationRequested();
            SetStatus($"版本 \"{options.Version}\" 解析完成");

            // 游戏目录：版本隔离生效时切换到 versions/<版本>，
            // 世界 / 服务器列表 / 截图 / 配置 / 日志等互不互通；libraries 与 assets 仍共享。
            _gameDirectory = minecraftDir;
            if (VersionIsolationService.IsIsolated(options.Version))
            {
                var isolatedDir = Path.Combine(minecraftDir, "versions", options.Version);
                Directory.CreateDirectory(isolatedDir);
                _gameDirectory = isolatedDir;
                SetStatus($"已启用版本隔离：{isolatedDir}");
            }

            // ---------- 1.2 选择满足版本要求的 Java（本机不满足则自动下载官方运行时） ----------
            var javaPath = await ResolveJavaAsync(versionJson, options, ct);
            ct.ThrowIfCancellationRequested();

            // ---------- 1.5 资源完整性检测与自动补全（缺失文件自动下载） ----------
            if (options.CheckResources)
            {
                SetStatus("正在检查并补全缺失文件...");
                await EnsureResourcesAsync(versionJson, minecraftDir, ct);
            }
            ct.ThrowIfCancellationRequested();

            // ---------- 2. 准备 natives 目录并解压 Native 库 ----------
            var versionDir = Path.Combine(minecraftDir, "versions", options.Version);
            var nativesDir = Path.Combine(versionDir, "natives");

            SetStatus("正在解压 Native 库...");
            PrepareNativesDirectory(nativesDir);
            ExtractNatives(versionJson, minecraftDir, nativesDir);
            ct.ThrowIfCancellationRequested();
            SetStatus("Native 库解压完成");

            // ---------- 3. 构建 ClassPath ----------
            SetStatus("正在构建启动参数...");
            var classPath = BuildClassPath(versionJson, minecraftDir);

            // 游戏核心 JAR（自动找目录下最大的 jar）
            var mainJar = versionJson.GetMainJarPath();
            if (!File.Exists(mainJar))
            {
                throw new FileNotFoundException($"未找到游戏核心 JAR：{mainJar}");
            }
            classPath = classPath.Length == 0 ? mainJar : classPath + Path.PathSeparator + mainJar;

            // ---------- 4. 构建 JVM 参数与游戏参数 ----------
            var jvmArgs = BuildJvmArgs(versionJson, options, classPath, nativesDir);
            var gameArgs = BuildGameArgs(versionJson, options);

            var fullArgs = new List<string>();
            fullArgs.AddRange(jvmArgs);

            // 外置登录（Yggdrasil）：注入 authlib-injector 与服务地址
            if (options.Account.Type == AccountType.Yggdrasil &&
                !string.IsNullOrWhiteSpace(options.Account.AuthServer))
            {
                var injector = await EnsureAuthlibInjectorAsync(ct);
                if (injector is not null)
                {
                    fullArgs.Add($"-javaagent:{injector}={options.Account.AuthServer}");
                    fullArgs.Add("-Dauthlibinjector.side=client");
                    LogService.Instance.Log($"已启用外置登录：{options.Account.AuthServerName}（{options.Account.AuthServer}）");
                }
                else
                {
                    LogService.Instance.Log("警告：未能获取 authlib-injector，外置登录账户可能无法通过服务器验证。");
                }
            }

            // Java Launch Wrapper（MIT, 00ll00）：仅当系统开启“UTF-8 Beta 全球语言支持”且
            // Java 版本早于 19（JDK-8272352 未修复）时启用，修复中文/特殊字符导致的 ClassPath 乱码。
            // 命令形式：java [jvm参数含 -cp] -jar wrapper.jar <真正主类> [游戏参数]
            var wrapperJar = ResolveWrapperJarIfNeeded(javaPath);
            if (wrapperJar is not null)
            {
                SetStatus("已启用 Java 启动参数编码保护（Java Launch Wrapper）");
                fullArgs.Add("-jar");
                fullArgs.Add(wrapperJar);
            }

            fullArgs.Add(versionJson.MainClass!); // 主类
            fullArgs.AddRange(gameArgs);

            // ---------- 5. 启动 Java 进程 ----------
            SetStatus("启动命令构建完成，正在启动游戏...");

            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = minecraftDir,
            };
            // 使用 ArgumentList 逐个添加参数，自动处理引号转义，避免注入问题
            foreach (var arg in fullArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            // 实时输出日志
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) LogService.Instance.Log(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) LogService.Instance.Log(e.Data);
            };

            // 进程退出（后台线程触发，UI 侧自行调度到 UI 线程）
            process.Exited += (_, _) =>
            {
                IsRunning = false;
                IsGameRunning = false;
                var exitCode = process.ExitCode;
                SetStatus(_cancelRequested
                    ? "已取消启动，游戏进程已结束"
                    : exitCode == 0
                        ? "游戏已正常退出"
                        : $"游戏已退出（退出代码 {exitCode}）");
            };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Java 进程启动失败。");
                }
            }
            catch (Exception ex)
            {
                IsRunning = false;
                throw new InvalidOperationException($"无法启动 Java 进程：{javaPath}\n{ex.Message}", ex);
            }

            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 设置游戏进程优先级（PCL 同款：优先提升 + 按设置分配优先级类）
            try
            {
                process.PriorityBoostEnabled = true;
                process.PriorityClass = Models.GameProcessPriority.ToPriorityClass(options.GamePriority);
            }
            catch
            {
                // 优先级设置失败不影响游戏启动
            }

            // 进程已拉起，但游戏窗口通常要等 JVM→LWJGL→渲染初始化后才出现。
            // 此时保持启动按钮为灰色、小字提示“窗口即将弹出”，直到真正检测到窗口。
            SetStatus("游戏已启动，游戏窗口即将弹出");
            var windowState = await WaitForGameWindowAsync(process, ct);

            // 窗口出现（或等待超时但进程仍在运行）视为成功并显示“强制结束”按钮；
            // 进程提前退出则失败（保留退出信息）。
            var started = windowState != GameWindowState.Exited;
            if (started) IsGameRunning = true;
            return started;
        }
        catch (OperationCanceledException)
        {
            // 启动被取消（CancelLaunch 或外部令牌）：终止已拉起的进程并恢复状态
            IsRunning = false;
            var p = _process;
            if (p is { HasExited: false })
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程可能刚好自行退出
                }
            }
            SetStatus("已取消启动");
            return false;
        }
        catch
        {
            IsRunning = false;
            throw;
        }
        finally
        {
            _launchCts?.Dispose();
            _launchCts = null;
        }
    }

    /// <summary>等待游戏窗口出现的结果。</summary>
    private enum GameWindowState
    {
        /// <summary>已检测到游戏主窗口。</summary>
        Appeared,
        /// <summary>窗口出现前进程已退出。</summary>
        Exited,
        /// <summary>超过等待时限仍未检测到窗口，但进程还在运行（保守视为已启动）。</summary>
        Timeout,
    }

    /// <summary>
    /// 轮询等待游戏窗口弹出（Java/LWJGL 从进程启动到创建 GLFW 窗口可能需要数十秒到数分钟）。
    /// 判定依据：进程主窗口句柄变为非零。进程提前退出立即返回 Exited；超时返回 Timeout；取消则抛出。
    /// </summary>
    private static async Task<GameWindowState> WaitForGameWindowAsync(Process process, CancellationToken cancellationToken)
    {
        const int timeoutSeconds = 240;
        var deadline = TimeSpan.FromSeconds(timeoutSeconds);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                return GameWindowState.Exited;
            }

            try
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    LogService.Instance.Log("检测到游戏窗口已弹出。");
                    return GameWindowState.Appeared;
                }
            }
            catch
            {
                // 进程可能在本轮退出，下一轮由 HasExited 处理
            }

            await Task.Delay(1000, cancellationToken);
        }

        LogService.Instance.Log($"等待游戏窗口超过 {timeoutSeconds} 秒仍未出现，但进程仍在运行，按已启动处理。");
        return GameWindowState.Timeout;
    }

    // =====================================================================
    // 资源完整性检测与自动补全（缺失文件自动下载）
    // =====================================================================

    /// <summary>authlib-injector 下载地址（官方发布，GitHub 直连失败时走 gh-proxy 镜像）。</summary>
    private const string AuthlibInjectorUrl =
        "https://github.com/yushijinhun/authlib-injector/releases/download/v1.2.8/authlib-injector-1.2.8.jar";

    /// <summary>
    /// 确保本地存在 authlib-injector.jar（外置登录必需），返回其路径；失败返回 null。
    /// 存放于 .minecraftuthlib-injectoruthlib-injector.jar。
    /// </summary>
    /// <summary>本次启动实际使用的游戏目录（版本隔离时为 versions/&lt;版本&gt;）。</summary>
    private string _gameDirectory = "";

    private static async Task<string?> EnsureAuthlibInjectorAsync(CancellationToken ct)
    {
        var target = Path.Combine(ConfigService.Instance.MinecraftDirectory,
            "authlib-injector", "authlib-injector.jar");
        if (File.Exists(target) && new FileInfo(target).Length > 100_000)
        {
            return target;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            SetStatusPublic("正在下载外置登录组件（authlib-injector）...");
            var task = new DownloadTask
            {
                Urls = new List<string>
                {
                    AuthlibInjectorUrl,
                    "https://gh-proxy.org/" + AuthlibInjectorUrl,
                },
                LocalPath = target,
                DisplayName = "authlib-injector.jar",
            };
            var ok = await DownloadService.Instance.DownloadOneAsync(task, cancellationToken: ct);
            return ok ? target : null;
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"下载 authlib-injector 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>静态上下文里转发状态（供静态方法使用）。</summary>
    private static void SetStatusPublic(string status) => Instance.SetStatus(status);

    /// <summary>
    /// 检查并补全缺失的游戏文件：主 jar、依赖库（含 natives）、资源索引、资源对象。
    /// 缺失或损坏的文件自动加入下载任务，多线程下载（线程数来自网络设置）。
    /// </summary>
    private async Task EnsureResourcesAsync(VersionJson versionJson, string minecraftDir, CancellationToken ct)
    {
        // 与安装阶段的补全逻辑共用 ResourceService，避免两处规则不一致
        SetStatus("正在检查缺失的游戏文件...");
        var tasks = await ResourceService.CollectMissingAsync(versionJson, minecraftDir, SetStatus, ct);
        if (tasks.Count == 0)
        {
            SetStatus("文件检查完成，无需补全");
            return;
        }

        var maxThreads = SettingsService.Instance.Load().DownloadThreads;
        SetStatus($"发现 {tasks.Count} 个缺失文件，开始下载（{maxThreads} 线程）...");
        var failed = await DownloadService.Instance.DownloadManyAsync(tasks, maxThreads,
            new Progress<(int done, int total, string current)>(p =>
            {
                LaunchProgressChanged?.Invoke(p.total == 0 ? null : (double?)p.done / p.total);
                if (p.done % 10 == 0 || p.done == p.total)
                {
                    SetStatus($"正在补全文件：{p.done}/{p.total}  {p.current}");
                }
            }), ct);

        if (failed.Count > 0)
        {
            var names = string.Join("、", failed.Take(5).Select(f => f.DisplayName));
            throw new IOException($"游戏文件补全失败（{failed.Count} 个）：{names}");
        }
        SetStatus("文件补全完成");
    }

    /// <summary>
    /// 为单个库构造候选下载地址（官方源优先，BMCLAPI 镜像兜底；Fabric 库走 maven.fabricmc.net）。
    /// </summary>
    private static List<string> BuildLibraryUrls(MinecraftLibrary lib, string relPath)
    {
        var urls = new List<string>();

        // 1. 版本 JSON 显式提供的 artifact URL
        if (!string.IsNullOrWhiteSpace(lib.Downloads?.Artifact?.Url))
        {
            urls.Add(lib.Downloads.Artifact.Url);
        }
        // 2. 顶层 url 基址 + 路径（如 maven.fabricmc.net/...）
        else if (!string.IsNullOrWhiteSpace(lib.Url))
        {
            urls.Add(lib.Url.TrimEnd('/') + "/" + relPath);
        }
        // 3. Fabric 系库默认源
        else if (lib.Name?.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) == true)
        {
            urls.Add("https://maven.fabricmc.net/" + relPath);
        }
        // 4. Mojang 官方库默认源
        else
        {
            urls.Add("https://libraries.minecraft.net/" + relPath);
        }

        // BMCLAPI 镜像兜底（所有库通用）
        urls.Add("https://bmclapi2.bangbang93.com/maven/" + relPath);

        return urls.Distinct().ToList();
    }

    /// <summary>
    /// 解析资源索引，找出缺失 / 大小不符的资源对象文件。
    /// </summary>
    private static async Task<List<DownloadTask>> BuildMissingAssetsAsync(string indexPath, string minecraftDir)
    {
        var result = new List<DownloadTask>();
        if (!File.Exists(indexPath)) return result;

        try
        {
            using var doc = await JsonDocument.ParseAsync(File.OpenRead(indexPath));
            if (!doc.RootElement.TryGetProperty("objects", out var objects)) return result;

            foreach (var obj in objects.EnumerateObject())
            {
                if (!obj.Value.TryGetProperty("hash", out var hashEl)) continue;
                var hash = hashEl.GetString();
                if (string.IsNullOrEmpty(hash)) continue;

                long size = 0;
                if (obj.Value.TryGetProperty("size", out var sizeEl)) size = sizeEl.GetInt64();

                var local = Path.Combine(minecraftDir, "assets", "objects", hash[..2], hash);
                if (File.Exists(local))
                {
                    // 存在但大小不符视为损坏
                    var fi = new FileInfo(local);
                    if (size == 0 || fi.Length == size) continue;
                }

                result.Add(new DownloadTask
                {
                    Urls = new List<string>
                    {
                        $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                        $"https://bmclapi2.bangbang93.com/assets/{hash[..2]}/{hash}",
                    },
                    LocalPath = local,
                    ExpectedSha1 = hash,
                    DisplayName = obj.Name,
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"解析资源索引失败：{ex.Message}");
        }

        return result;
    }

    // =====================================================================
    // ClassPath 构建
    // =====================================================================

    private string BuildClassPath(VersionJson versionJson, string minecraftDir)
    {
        var paths = new List<string>();
        var librariesRoot = Path.Combine(minecraftDir, "libraries");

        if (versionJson.Libraries is not null)
        {
            foreach (var lib in versionJson.Libraries)
            {
                // 旧版（LWJGL 3.3 及以前）的 native 库：不进 ClassPath，由启动器解压到 natives 目录。
                // 新版（LWJGL 3.4+，如 26.2，坐标带 :natives-windows）的 native jar 需要在 ClassPath 上，
                // 由 LWJGL / JNA / Netty 自行解压到 -D...ExtractPath 指定的子目录。
                var isLegacyNative = lib.Natives is not null && lib.Natives.ContainsKey("windows");
                if (isLegacyNative) continue;

                // 规则不匹配当前系统则跳过
                if (!VersionParserService.RulesMatch(lib.Rules)) continue;

                var relativePath = lib.GetPath();
                if (string.IsNullOrEmpty(relativePath)) continue;

                var fullPath = Path.Combine(librariesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    LogService.Instance.Log($"警告：依赖库不存在，已跳过：{relativePath}");
                    continue;
                }
                paths.Add(fullPath);
            }
        }

        return string.Join(Path.PathSeparator, paths);
    }

    /// <summary>
    /// 判断一个库是否是 Native 库（需要解压 dll）。
    /// 兼容两种格式：
    ///   1. 旧版：natives 字段含 windows
    ///   2. 新版：Maven 坐标或 path 里含 "natives-windows"
    /// </summary>
    private static bool IsNativeLibrary(MinecraftLibrary lib)
    {
        // 旧格式
        if (lib.Natives is not null && lib.Natives.ContainsKey("windows")) return true;

        // 新格式：name 里带 :natives-windows 或 path 里含 natives-windows
        var name = lib.Name ?? "";
        if (name.Contains(":natives-windows", StringComparison.OrdinalIgnoreCase)) return true;
        var path = lib.Downloads?.Artifact?.Path ?? "";
        if (path.Contains("natives-windows", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    // =====================================================================
    // JVM 参数构建
    // =====================================================================

    private List<string> BuildJvmArgs(VersionJson versionJson, LaunchOptions options, string classPath, string nativesDir)
    {
        var args = new List<string>();

        // 内存参数（用户设置，始终置于最前）
        if (options.MaxMemoryMB > 0)
        {
            args.Add($"-Xmx{(int)options.MaxMemoryMB}M");
        }
        if (options.MinMemoryMB > 0)
        {
            args.Add($"-Xms{(int)options.MinMemoryMB}M");
        }

        if (versionJson.Arguments?.Jvm is { Count: > 0 } jvmTemplate)
        {
            // 新版本：按模板展开（含规则过滤）
            foreach (var element in jvmTemplate)
            {
                var item = VersionParserService.ParseArgumentItem(element);
                if (item.HasRules && !VersionParserService.RulesMatch(item.Rules)) continue;

                foreach (var value in item.Values)
                {
                    args.Add(ReplaceTokens(value, versionJson, options, classPath, nativesDir));
                }
            }

            // 26.2 起：默认用户 JVM 参数（GC、CompactObjectHeaders 等）。
            // 内存由用户设置决定，这里跳过 -Xms/-Xmx 以免重复；其余按系统规则展开。
            if (versionJson.Arguments.DefaultUserJvm is { Count: > 0 } defaultJvm)
            {
                foreach (var element in defaultJvm)
                {
                    var item = VersionParserService.ParseArgumentItem(element);
                    if (item.HasRules && !VersionParserService.RulesMatch(item.Rules)) continue;

                    foreach (var value in item.Values)
                    {
                        if (value.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) ||
                            value.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        args.Add(ReplaceTokens(value, versionJson, options, classPath, nativesDir));
                    }
                }
            }
        }
        else
        {
            // 旧版本（1.12.2 及以前）没有 arguments.jvm，按标准方式补全
            args.Add($"-Djava.library.path={nativesDir}");
            args.Add("-cp");
            args.Add(classPath);
        }

        // 用户自定义额外 JVM 参数
        foreach (var arg in CommandLineHelper.Split(options.ExtraJvmArgs))
        {
            args.Add(ReplaceTokens(arg, versionJson, options, classPath, nativesDir));
        }

        return args;
    }

    // =====================================================================
    // 游戏参数构建
    // =====================================================================

    private List<string> BuildGameArgs(VersionJson versionJson, LaunchOptions options)
    {
        var args = new List<string>();

        if (versionJson.Arguments?.Game is { Count: > 0 } gameTemplate)
        {
            // 新版本：按模板展开
            foreach (var element in gameTemplate)
            {
                var item = VersionParserService.ParseArgumentItem(element);
                if (item.HasRules && !VersionParserService.RulesMatch(item.Rules)) continue;
                args.AddRange(item.Values);
            }
        }
        else if (!string.IsNullOrWhiteSpace(versionJson.MinecraftArguments))
        {
            // 旧版本：拆分单条参数字符串
            args.AddRange(CommandLineHelper.Split(versionJson.MinecraftArguments));
        }

        // 占位符替换
        for (var i = 0; i < args.Count; i++)
        {
            args[i] = ReplaceTokens(args[i], versionJson, options, "", "");
        }

        // 用户自定义额外游戏参数
        foreach (var arg in CommandLineHelper.Split(options.ExtraGameArgs))
        {
            args.Add(ReplaceTokens(arg, versionJson, options, "", ""));
        }

        // 默认连接橙猫生存服务器（用户未显式指定直连参数时自动加入）。
        // 新版本（支持快速游戏，如 26.2）使用 --quickPlayMultiplayer；旧版本使用 --server / --port。
        var autoJoin = SettingsService.Instance.Load().AutoJoinServer;
        var hasServer = args.Any(a => a.Equals("--server", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("--quickPlayMultiplayer", StringComparison.OrdinalIgnoreCase));
        if (autoJoin && !hasServer)
        {
            if (SupportsQuickPlay(versionJson))
            {
                args.Add("--quickPlayMultiplayer");
                args.Add($"{DefaultServerHost}:{DefaultServerPort}");
            }
            else
            {
                args.Add("--server");
                args.Add(DefaultServerHost);
                args.Add("--port");
                args.Add(DefaultServerPort);
            }
        }

        return args;
    }

    /// <summary>默认直连服务器地址与端口（橙猫生存服务器）。</summary>
    public const string DefaultServerHost = "mc.ambercat.top";
    public const string DefaultServerPort = "25565";

    /// <summary>
    /// 判断该版本是否支持快速游戏直连（--quickPlayMultiplayer）。
    /// 依据：arguments.game 模板中出现 quickPlay 占位符（约 1.20 起）。
    /// </summary>
    private static bool SupportsQuickPlay(VersionJson versionJson)
    {
        var game = versionJson.Arguments?.Game;
        if (game is null) return false;
        foreach (var element in game)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var s = element.GetString();
                if (s is not null && s.Contains("quickPlay", StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.GetRawText().Contains("quickPlay", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    // =====================================================================
    // 占位符替换
    // =====================================================================

    private string ReplaceTokens(string input, VersionJson versionJson, LaunchOptions options, string classPath, string nativesDir)
    {
        // 游戏目录（版本隔离时是 versions/<版本>）；资源目录始终在 .minecraft 根下
        var gameDir = string.IsNullOrEmpty(_gameDirectory)
            ? ConfigService.Instance.MinecraftDirectory
            : _gameDirectory;
        var assetsRoot = Path.Combine(ConfigService.Instance.MinecraftDirectory, "assets");
        var account = options.Account;
        var isMicrosoft = account.Type == AccountType.Microsoft;

        return input
            .Replace("${auth_player_name}", account.PlayerName)
            .Replace("${auth_uuid}", account.Uuid)
            .Replace("${auth_access_token}", account.AccessToken)
            // 旧版本（1.7.10 及以前）使用的会话参数
            .Replace("${auth_session}", account.AccessToken)
            .Replace("${client_id}", "ACLauncher")
            // 正版为 msa，外置登录（Yggdrasil）与旧版离线都用 mojang
            .Replace("${user_type}", isMicrosoft ? "msa"
                : account.Type == AccountType.Yggdrasil ? "mojang" : "offline")
            .Replace("${version_name}", options.Version)
            .Replace("${version_type}", "release")
            .Replace("${game_directory}", gameDir)
            .Replace("${assets_root}", assetsRoot)
            .Replace("${assets_index_name}", versionJson.GetAssetIndexName())
            .Replace("${user_properties}", "{}")
            .Replace("${natives_directory}", nativesDir)
            .Replace("${classpath}", classPath)
            .Replace("${launcher_name}", "ACLauncher")
            .Replace("${launcher_version}", "1.0.0")
            .Replace("${auth_xuid}", account.Xuid)
            // 旧版本分辨率占位符（默认 854x480，可在额外游戏参数中覆盖）
            .Replace("${resolution_width}", "854")
            .Replace("${resolution_height}", "480");
    }

    // =====================================================================
    // Native 库解压
    // =====================================================================

    /// <summary>清空并重建 natives 目录（上次游戏异常退出导致文件被占用时，复用旧目录）。</summary>
    private void PrepareNativesDirectory(string nativesDir)
    {
        try
        {
            if (Directory.Exists(nativesDir))
            {
                Directory.Delete(nativesDir, true);
            }
        }
        catch
        {
            // 目录被占用（如 DLL 被加载），复用现有文件
            LogService.Instance.Log("警告：natives 目录被占用，将复用已有文件。");
        }
        Directory.CreateDirectory(nativesDir);

        // 新版（LWJGL 3.4+，如 26.2）各原生库自行解压到不同子目录（对应 -D...ExtractPath 等参数）
        foreach (var sub in new[] { "java", "jna", "lwjgl", "netty" })
        {
            Directory.CreateDirectory(Path.Combine(nativesDir, sub));
        }
    }

    /// <summary>解压所有适用于当前系统的 Native 库中的 .dll / .so / .dylib 到 natives 目录。</summary>
    private void ExtractNatives(VersionJson versionJson, string minecraftDir, string nativesDir)
    {
        if (versionJson.Libraries is null) return;

        foreach (var lib in versionJson.Libraries)
        {
            // 规则不匹配当前系统则跳过
            if (!VersionParserService.RulesMatch(lib.Rules)) continue;

            // 判断是否是 windows native 库（新旧两种格式）
            string? classifier = null;
            string? relativePath = null;

            if (lib.Natives is not null && lib.Natives.TryGetValue("windows", out var oldClassifier))
            {
                // 旧格式：natives: { windows: "natives-windows" }
                classifier = oldClassifier;
                relativePath = lib.GetPath(classifier);
            }
            else if (IsNativeLibrary(lib))
            {
                // 新格式：直接用 artifact.path（已经是 natives-windows jar）
                relativePath = lib.GetPath();
            }

            if (string.IsNullOrEmpty(relativePath)) continue;

            var nativeJar = Path.Combine(minecraftDir, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(nativeJar))
            {
                LogService.Instance.Log($"警告：Native 库不存在，已跳过：{relativePath}");
                continue;
            }

            try
            {
                using var archive = ZipFile.OpenRead(nativeJar);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;

                    var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (extension is not (".dll" or ".so" or ".dylib" or ".properties" or ".cfg" or ".json" or ".xml" or ".txt" or ".dat"))
                    {
                        continue;
                    }

                    var destPath = Path.Combine(nativesDir, entry.Name);
                    if (File.Exists(destPath)) continue;
                    entry.ExtractToFile(destPath, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Log($"警告：解压 Native 库失败（{relativePath}）：{ex.Message}");
            }
        }
    }
}
