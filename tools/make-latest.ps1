param(
  [Parameter(Mandatory)] [string]$Version,
  [Parameter(Mandatory)] [string]$Url,
  [string]$Notes = "",
  [string]$Installer = "Installer\ACLauncher-Setup-$Version-beta.exe"
)

# 生成在线更新清单 latest.json（版本号 + 下载直链 + SHA-256）
# 用法见 docs\ONLINE_UPDATE.md

if (-not (Test-Path $Installer)) {
  Write-Error "找不到安装包：$Installer`n请先运行 ISCC.exe Installer\ACLauncher-Setup.iss"
  exit 1
}

$sha = (Get-FileHash $Installer -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $Installer).Length / 1MB

# 用有序字典保持字段顺序，并以无 BOM 的 UTF-8 写出（部分 JSON 解析器不接受 BOM）
$json = [ordered]@{
  version   = $Version
  notes     = $Notes
  url       = $Url
  sha256    = $sha
  mandatory = $false
} | ConvertTo-Json -Depth 3

[System.IO.File]::WriteAllText(
  (Join-Path (Get-Location) "latest.json"),
  $json,
  (New-Object System.Text.UTF8Encoding $false)
)

Write-Host "已生成 latest.json"
Write-Host ("  version : " + $Version)
Write-Host ("  file    : " + $Installer + "  (" + [math]::Round($size, 1) + " MB)")
Write-Host ("  sha256  : " + $sha)
Write-Host "把 latest.json 上传到 Pages 即可（记得带 _headers 防缓存）。"
