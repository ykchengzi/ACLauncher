# 在线更新配置教程

启动器内置在线更新：**启动 4 秒后后台静默检查** + **关于页「检查更新」按钮**。
有新版本时弹窗提示，用户确认后下载安装包、校验 SHA-256、静默运行安装器覆盖安装并重启。

整条链路只需要两样东西：

| 需要的东西 | 说明 |
| --- | --- |
| `latest.json` | 更新清单，几十字节的纯文本，每次发版改一次 |
| 安装包 `.exe` | 由 `ISCC.exe Installer\ACLauncher-Setup.iss` 生成，约 57 MB |

> ⚠️ **先看这里**：`Services/UpdateService.cs` 里的默认清单地址是
> `https://aclauncher.pages.dev/latest.json`，这个域名**并不存在**。
> 也就是说在你自己配置之前，在线更新永远不会生效，日志里会持续出现
> `检查更新失败：不知道这样的主机。（aclauncher.pages.dev:443）`。
> 完成下面第 5 步把地址换成你自己的，功能才会真正可用。

---

## 1. 准备安装包

```bash
# 1) 编译（Release + x64）
dotnet build ACLauncher.csproj -c Release -p:Platform=x64

# 2) 打包（需先安装 Inno Setup 7）
"C:\Program Files\Inno Setup 7\ISCC.exe" Installer\ACLauncher-Setup.iss
```

产物：`Installer\ACLauncher-Setup-<版本>.exe`

打包前确认版本号三处一致：

| 文件 | 字段 |
| --- | --- |
| `ACLauncher.csproj` | `<Version>` / `<FileVersion>` |
| `Installer\ACLauncher-Setup.iss` | `#define MyAppVersion` |
| `MainWindow.xaml`、`Views\AboutPage.xaml` | 标题里显示的版本文字 |

> 启动器读取的是 **exe 的文件版本号**（`FileVersionInfo.FileVersion`），
> 所以 `ACLauncher.csproj` 里的 `<FileVersion>` 必须改，改界面文字不会影响版本比较。

## 2. 计算 SHA-256

```powershell
Get-FileHash "Installer\ACLauncher-Setup-0.99.4-beta.exe" -Algorithm SHA256 |
  Select-Object -ExpandProperty Hash
```

输出 64 位小写十六进制，例如 `a3f5…9c21`，复制到清单的 `sha256` 字段。
**强烈建议填写**：填了就会强制校验，下载被截断或中间被篡改都会拒绝安装。

## 3. 上传安装包

Cloudflare Pages 单个文件上限 **25 MiB**，57 MB 的安装包放不进去，必须换地方托管。

| 方案 | 免费额度 | 直链示例 | 备注 |
| --- | --- | --- | --- |
| **GitHub Release**（推荐） | 单文件 2 GB | `https://github.com/ykchengzi/ACLauncher/releases/download/v0.99.4/ACLauncher-Setup-0.99.4-beta.exe` | 国内直连不稳，启动器会自动追加 `gh-proxy.org` 加速重试 |
| **Cloudflare R2** | 10 GB 存储 + 免费出站 | `https://<你的r2域名>/ACLauncher-Setup-0.99.4-beta.exe` | 需在 R2 里开公开访问域名 |
| 任意对象存储 / 自建 | — | 你自己的 URL | 必须是 **HTTPS 直链**，不能要登录、不能是下载页 |

GitHub Release 上传：

```bash
git tag v0.99.4 && git push origin v0.99.4
gh release create v0.99.4 "Installer/ACLauncher-Setup-0.99.4-beta.exe" \
  --title "v0.99.4-beta" --notes "更新说明"
```

> 没有 `gh` 的话，也可以直接在 GitHub 网页上创建 Release 并拖入文件。

## 4. 写 latest.json

```json
{
  "version": "0.99.4",
  "notes": "本次更新内容：\n· 修复游戏窗口检测\n· 新增每日一言",
  "url": "https://github.com/ykchengzi/ACLauncher/releases/download/v0.99.4/ACLauncher-Setup-0.99.4-beta.exe",
  "sha256": "在这里填 64 位小写十六进制",
  "mandatory": false
}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `version` | ✅ | 最新版本号。**只有大于当前程序版本才会提示**，按数字段逐段比较（`0.99.4` > `0.99.3`）。带 `-beta` 后缀不影响比较，只取数字段 |
| `notes` | — | 更新说明，弹窗里原样显示，`\n` 换行 |
| `url` | ✅ | 安装包 **HTTPS 直链**。含 `github.com` 时启动器会自动追加 `https://gh-proxy.org/` 前缀作为备用地址 |
| `sha256` | 建议 | 安装包 SHA-256，小写；填写后强制校验，不匹配则删除并中止 |
| `mandatory` | — | 预留字段，当前版本不区分强制更新 |

写完建议先本地校验 JSON 合法性（多余逗号、中文引号是最常见的坑）：

```powershell
Get-Content latest.json -Raw | ConvertFrom-Json | Format-List
```

## 5. 部署 latest.json 并改代码地址

### 5.1 部署到 Cloudflare Pages

1. Cloudflare 控制台 → **Workers & Pages** → **Create** → **Pages** → **Upload assets**；
2. 上传一个只含 `latest.json` 的文件夹（建议顺带放一个 `_headers`，见下）；
3. 部署后得到 `https://<项目名>.pages.dev`，清单地址就是 `https://<项目名>.pages.dev/latest.json`。

**务必加防缓存**。Pages 默认会缓存静态资源，改完 `latest.json` 用户可能还拿到旧版本。
在根目录放一个 `_headers` 文件：

```
/latest.json
  Cache-Control: no-cache, no-store, must-revalidate
```

### 5.2 或者用极简 Worker 转发

如果清单放在别处（比如另一个仓库），可以用 Worker 做一层转发并强制不缓存：

```js
export default {
  async fetch() {
    const res = await fetch("https://你的原始清单地址/latest.json", { cf: { cacheTtl: 0 } });
    return new Response(await res.text(), {
      headers: { "content-type": "application/json", "cache-control": "no-cache" },
    });
  },
};
```

### 5.3 改代码里的清单地址（**必做**）

打开 `Services/UpdateService.cs`，把第 44 行改成你自己的地址：

```csharp
public static string ManifestUrl { get; set; } = "https://你的项目.pages.dev/latest.json";
```

改完重新编译并重新打包发布。**这一步之前，在线更新一律不生效。**

验证清单地址是否可用：

```bash
curl -s https://你的项目.pages.dev/latest.json
```

能直接看到 JSON 内容才算通；返回 HTML（登录页 / 404 页面）都不行。

## 6. 发版流程清单

每次发版按顺序走一遍：

- [ ] 改版本号：`ACLauncher.csproj` 的 `<Version>` / `<FileVersion>`、iss 的 `MyAppVersion`、界面标题文字
- [ ] `dotnet build -c Release -p:Platform=x64`
- [ ] `ISCC.exe Installer\ACLauncher-Setup.iss`
- [ ] 上传新安装包到 GitHub Release / R2
- [ ] `Get-FileHash` 算 SHA-256
- [ ] 更新 `latest.json`（version / notes / url / sha256）
- [ ] 重新部署 Pages（或刷新 Worker 缓存）
- [ ] 用旧版启动器点「关于 → 检查更新」自测一次

## 7. 常见问题排查

| 现象 | 原因 | 解决 |
| --- | --- | --- |
| 一直提示「已是最新版本」 | 清单里 `version` 不大于 exe 的 `FileVersion` | 确认 csproj 的 `<FileVersion>` 真的改了，并重新打包 |
| 日志报「不知道这样的主机」 | 清单域名不存在或拼错 | 换成真实可访问的地址（默认的 `aclauncher.pages.dev` 就是这种情况） |
| 清单能打开，但仍提示最新 | 返回的是 HTML 而不是 JSON | `curl` 检查内容，确认 Content-Type 与内容 |
| 改了 json 但客户端拿到旧内容 | CDN / Pages 缓存 | 加 `_headers` 或改用带版本号的地址 |
| 提示发现新版本，下载后没反应 | 安装包地址不是直链（跳转到下载页 / 需要登录） | 换成 Release 资源直链或 R2 公开链接 |
| 下载完成但被拒绝安装 | `sha256` 与实际文件不一致 | 重新计算并更新，注意大小写和是否多了空格 |
| 安装后版本没变 | 安装器没覆盖成功 | 确认安装包是 `Setup.iss` 产物；手动装一次看是否报错 |

日志位置：启动器 **日志页** 可直接看，相关行形如：

```
[12:00:01] 检查更新失败：不知道这样的主机。（aclauncher.pages.dev:443）
[12:00:05] 更新包校验失败（SHA-256 不匹配）：期望 …，实际 …
```

## 8. 附：一键生成 latest.json

把下面存成 `tools\make-latest.ps1`，每次发版填两个参数即可：

```powershell
param(
  [Parameter(Mandatory)] [string]$Version,
  [Parameter(Mandatory)] [string]$Url,
  [string]$Notes = "",
  [string]$Installer = "Installer\ACLauncher-Setup-$Version-beta.exe"
)

$sha = (Get-FileHash $Installer -Algorithm SHA256).Hash.ToLowerInvariant()

@{
  version   = $Version
  notes     = $Notes
  url       = $Url
  sha256    = $sha
  mandatory = $false
} | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 latest.json

Write-Host "已生成 latest.json"
Write-Host "  version: $Version"
Write-Host "  sha256 : $sha"
```

用法：

```powershell
.\tools\make-latest.ps1 -Version 0.99.4 `
  -Url "https://github.com/ykchengzi/ACLauncher/releases/download/v0.99.4/ACLauncher-Setup-0.99.4-beta.exe" `
  -Notes "修复游戏窗口检测；新增每日一言"
```

然后把生成的 `latest.json` 上传到 Pages 即可。
