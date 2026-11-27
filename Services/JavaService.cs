using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ACLauncher.Services;

/// <summary>
/// 检测到的 Java 安装信息。
/// </summary>
public class JavaInfo
{
    /// <summary>java.exe / javaw.exe 的完整路径。</summary>
    public string Path { get; init; } = "";

    /// <summary>Java 版本号，例如 "17.0.9" 或 "1.8.0_202"。</summary>
    public string Version { get; init; } = "";

    /// <summary>主版本号（用于判断是否满足游戏要求）。</summary>
    public int MajorVersion { get; init; }

    /// <summary>下拉框展示文本。</summary>
    public string Display => $"{Version}  ({Path})";

    /// <summary>路径是否可执行。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);
}

/// <summary>
/// Java 环境管理服务：自动检测系统上的 Java 安装（注册表、环境变量、常见厂商目录、
/// 启动器自带 JRE、Minecraft 自动下载的官方运行时），并读取版本号。
/// </summary>
public class JavaService
{
    public static JavaService Instance { get; } = new();

    /// <summary>
    /// 自动检测 Java：
    ///  1. 启动器同目录 jre/ jdk/；
    ///  2. JAVA_HOME 环境变量；
    ///  3. 注册表 JavaSoft（JDK / JRE / Runtime Environment，含 32 位视图）；
    ///  4. PATH 中的 java(.exe)；
    ///  5. 常见厂商安装目录（深度递归，覆盖 Java / Adoptium / Microsoft / Zulu / Corretto /
    ///     BellSoft / Semeru / GraalVM / JetBrains / Temurin 等）；
    ///  6. 用户目录下的 .jdks、scoop、LocalAppData\Programs；
    ///  7. Minecraft 自动下载的官方运行时（.minecraft\runtime\{组件}\{平台}\bin）。
    /// 结果按主版本号从新到旧排序，去重，并尽量优先 java.exe。
    /// </summary>
    public async Task<List<JavaInfo>> DetectAsync()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDirectoryBin(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var clean = dir.Trim().Trim('"');
                AddCandidate(System.IO.Path.Combine(clean, "bin", "java.exe"), candidates);
                AddCandidate(System.IO.Path.Combine(clean, "bin", "javaw.exe"), candidates);
            }
            catch { /* 忽略非法路径 */ }
        }

        // 1) 启动器同目录（便携场景）
        AddDirectoryBin(System.IO.Path.Combine(AppContext.BaseDirectory, "jre"));
        AddDirectoryBin(System.IO.Path.Combine(AppContext.BaseDirectory, "jdk"));

        // 2) JAVA_HOME
        AddDirectoryBin(Environment.GetEnvironmentVariable("JAVA_HOME"));

        // 3) 注册表 JavaSoft（64 位 + 32 位视图）
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var baseKey in new[]
                     {
                         @"SOFTWARE\JavaSoft\Java Development Kit",
                         @"SOFTWARE\JavaSoft\Java Runtime Environment",
                         @"SOFTWARE\JavaSoft\JDK",
                         @"SOFTWARE\JavaSoft\JRE",
                     })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey(baseKey);
                    if (key is null) continue;
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(subName);
                        if (sub?.GetValue("JavaHome") is string home) AddDirectoryBin(home);
                    }
                }
                catch { /* 忽略 */ }
            }
        }

        // 4) PATH
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                AddCandidate(System.IO.Path.Combine(dir.Trim().Trim('"'), "java.exe"), candidates);
            }
            catch { /* 忽略 */ }
        }

        // 5) 常见厂商目录（递归扫描 java.exe / javaw.exe）
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localPrograms = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var vendorDirs = new List<string>();
        foreach (var root in new[] { programFiles, programFilesX86, localPrograms })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var vendor in new[]
                     {
                         "Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Microsoft",
                         "Zulu", "Azul", "Amazon Corretto", "BellSoft", "Liberica", "Semeru", "IBM",
                         "GraalVM", "JetBrains", "Temurin", "OpenJDK",
                     })
            {
                vendorDirs.Add(System.IO.Path.Combine(root, vendor));
            }
        }
        // 用户级目录
        vendorDirs.Add(System.IO.Path.Combine(userProfile, ".jdks"));
        vendorDirs.Add(System.IO.Path.Combine(userProfile, "scoop", "apps"));

        // 6) Minecraft 自动下载的官方运行时
        try
        {
            var runtimeRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
            if (Directory.Exists(runtimeRoot))
            {
                foreach (var componentDir in Directory.GetDirectories(runtimeRoot))
                {
                    foreach (var platformDir in Directory.GetDirectories(componentDir))
                    {
                        AddDirectoryBin(System.IO.Path.Combine(platformDir, ""));
                    }
                }
            }
        }
        catch { /* 忽略 */ }

        foreach (var dir in vendorDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var exe in EnumerateJavaExecutables(dir, maxDepth: 4))
                {
                    AddCandidate(exe, candidates);
                }
            }
            catch { /* 忽略 */ }
        }

        // 逐个读取版本号（并发 6 路，避免几十个候选串行变慢）
        var infos = new List<JavaInfo>();
        using var gate = new SemaphoreSlim(6);
        var tasks = candidates.Select(async path =>
        {
            await gate.WaitAsync();
            try
            {
                var version = await GetJavaVersionAsync(path);
                if (version is null) return;
                var info = new JavaInfo
                {
                    Path = path,
                    Version = version,
                    MajorVersion = ParseMajor(version),
                };
                lock (infos) infos.Add(info);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);

        // 去重（同一 java.exe 只保留一个），按主版本从新到旧，java.exe 优先于 javaw.exe
        return infos
            .GroupBy(i => System.IO.Path.GetDirectoryName(i.Path) ?? i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i =>
                    i.Path.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)).First())
            .OrderByDescending(i => i.MajorVersion)
            .ThenByDescending(i => i.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>从版本字符串解析主版本号（"1.8.0_202" → 8，"17.0.9" → 17）。</summary>
    public static int ParseMajor(string version)
    {
        var match = Regex.Match(version, @"^(?:1\.)?(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var major) ? major : 0;
    }

    /// <summary>
    /// 递归查找 java.exe / javaw.exe（限制深度，跳过明显无关目录，避免全盘扫描过慢）。
    /// </summary>
    private static IEnumerable<string> EnumerateJavaExecutables(string root, int maxDepth)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "java.exe");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maxDepth) continue;

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                var name = System.IO.Path.GetFileName(sub);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("lib", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // bin 下已由 java.exe 命中，lib 无需深入
                }
                pending.Push((sub, depth + 1));
            }
        }
    }

    /// <summary>
    /// 运行 java -version 读取版本号（输出在 stderr）。
    /// </summary>
    public async Task<string?> GetJavaVersionAsync(string javaPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var text = (stderr + "\n" + stdout).Trim();
            var match = Regex.Match(text, @"(?:openjdk|java) version ""([^""]+)""");
            return match.Success ? match.Groups[1].Value : text.Split('\n')[0];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>检查 Java 可执行文件是否存在且可用。</summary>
    public bool IsValidJavaPath(string javaPath)
        => !string.IsNullOrWhiteSpace(javaPath) && File.Exists(javaPath);

    private static void AddCandidate(string path, HashSet<string> candidates)
    {
        try
        {
            if (File.Exists(path)) candidates.Add(System.IO.Path.GetFullPath(path));
        }
        catch { /* 忽略 */ }
    }
}
