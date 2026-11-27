# ACLauncher 任务栏固定脚本（安装器调用，替代 syspin）
# 用法：powershell -ExecutionPolicy Bypass -File pin-taskbar.ps1 "<快捷方式完整路径>"
# 原理：
#   1) 优先通过 Shell COM 的 taskbarpin verb 固定（Windows 10 支持，不重启 explorer）；
#   2) 无论 verb 是否成功，都把快捷方式复制到用户任务栏固定目录作为兜底
#      （explorer 下次读取 Taskband 时生效）。
param([string]$LnkPath)
$ErrorActionPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($LnkPath) -or -not (Test-Path $LnkPath)) {
    exit 1
}

$name = Split-Path $LnkPath -Leaf

# 1) Shell verb 固定（不重启 explorer）
try {
    $shell = New-Object -ComObject Shell.Application
    $dir = Split-Path $LnkPath
    $item = $shell.Namespace($dir).ParseName($name)
    if ($null -ne $item) {
        $item.InvokeVerb('taskbarpin')
        Start-Sleep -Milliseconds 1500
    }
} catch {
    # verb 失败不阻断，继续走兜底
}

# 2) 兜底：复制到用户任务栏固定目录
$pinDir = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
if (-not (Test-Path $pinDir)) {
    New-Item -ItemType Directory -Force -Path $pinDir | Out-Null
}
Copy-Item $LnkPath (Join-Path $pinDir $name) -Force

exit 0
