using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;

namespace ACLauncher.Services;

/// <summary>
/// 7-Zip 解压服务。
/// 主方案：随启动器内置的 7za.exe（官方 7-Zip 19.00 源码编译，位于程序目录 7z\7za.exe），
/// 支持 .zip / .7z / .tar 等格式，无需用户安装任何软件。
/// 兜底：系统安装的 7z.exe → .NET 内置 ZipFile。
/// </summary>
public class SevenZipService
{
    public static SevenZipService Instance { get; } = new();

    /// <summary>内置 7za.exe 的路径（程序目录 7z\7za.exe）。</summary>
    public string? BundledSevenZaPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "7z", "7za.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// 查找 7-Zip 内核路径（内置 7za.exe > 手动配置 > 系统常见路径 > 注册表 > PATH）。
    /// 找不到返回 null。
    /// </summary>
    public string? FindSevenZip()
    {
        // 0. 内置 7za.exe（官方源码编译，随启动器分发）
        var bundled = BundledSevenZaPath;
        if (bundled is not null) return bundled;

        // 1. 手动配置
        var manual = SettingsService.Instance.Load().SevenZipPath;
        if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual))
        {
            return manual;
        }

        // 2. 常见安装路径
        var candidates = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            @"C:\Program Files\7-Zip\7za.exe",
            @"C:\Program Files (x86)\7-Zip\7za.exe",
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        // 3. 注册表（7-Zip 安装时写入）
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\7-Zip");
            var path = key?.GetValue("Path") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var exe = Path.Combine(path.TrimEnd('\\'), "7z.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch
        {
            // 注册表不可读时忽略
        }

        // 4. PATH 中的 7z
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var exe = Path.Combine(dir.Trim(), "7z.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    /// <summary>
    /// 解压 zip / 7z 到目标目录（保留目录结构，自动覆盖同名文件）。
    /// 优先级：内置 7za.exe（官方源码编译） → 系统 7z.exe → .NET 内置。
    /// </summary>
    /// <returns>使用的解压方式描述（"7-Zip 内核（内置）" / "7-Zip" / ".NET 内置"）。</returns>
    public async Task<string> ExtractAsync(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);

        // ---- 1. 内置 7za.exe（官方 7-Zip 19.00 源码编译，随启动器分发） ----
        if (BundledSevenZaPath is not null)
        {
            var result = await ExtractWithProcessAsync(BundledSevenZaPath, archivePath, destDir);
            if (result is not null) return result;
        }

        // ---- 2. 系统安装的 7-Zip（7z.exe） ----
        var sysSevenZip = FindSevenZip();
        if (sysSevenZip is not null)
        {
            var result = await ExtractWithProcessAsync(sysSevenZip, archivePath, destDir);
            if (result is not null) return result;
        }
        else
        {
            LogService.Instance.Log("未找到可用的 7-Zip，使用 .NET 内置解压（仅支持标准 zip）。");
        }

        // ---- 3. .NET 内置解压（仅支持标准 zip） ----
        System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, destDir, true);
        return ".NET 内置";
    }

    /// <summary>用指定 7z 可执行文件解压；不可用或失败时返回 null。</summary>
    private async Task<string?> ExtractWithProcessAsync(string sevenZipExe, string archivePath, string destDir)
    {
        var isBundled = string.Equals(Path.GetFileName(sevenZipExe), "7za.exe", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = sevenZipExe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // 7z x -y -o<目标目录> <压缩包>
        psi.ArgumentList.Add("x");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add($"-o{destDir}");
        psi.ArgumentList.Add(archivePath);

        using var process = Process.Start(psi);
        if (process is null) return null;
        await process.WaitForExitAsync();
        if (process.ExitCode == 0)
        {
            LogService.Instance.Log($"已使用 {(isBundled ? "内置 7-Zip 内核" : "7-Zip")} 解压：{Path.GetFileName(archivePath)}");
            return isBundled ? "7-Zip 内核（内置）" : "7-Zip";
        }
        LogService.Instance.Log($"7-Zip 解压失败（退出码 {process.ExitCode}），尝试下一方案。");
        return null;
    }
}
