# 在线更新部署说明（Cloudflare Pages）

启动器内置在线更新：启动时后台检查 + 关于页「检查更新」按钮。
只需要在 Cloudflare Pages 上托管两个文件：

- `latest.json` —— 更新清单（每次发版更新）
- 安装包 exe —— 建议放 GitHub Release（走 gh-proxy 加速）或 Cloudflare R2（免费 10 GB）

> Cloudflare Pages 单个静态文件上限 25 MB，56 MB 的安装包不能直接放 Pages，放 R2 / GitHub Release 即可。

## 1. latest.json 格式

```json
{
  "version": "0.99.2",
  "notes": "本次更新内容：\n· 修复 xxx\n· 优化 yyy",
  "url": "https://github.com/<你的仓库>/releases/download/v0.99.2/ACLauncher-Setup-0.99.2.exe",
  "sha256": "<安装包的 SHA-256，小写十六进制>",
  "mandatory": false
}
```

- `version`：必须大于当前程序版本才会提示更新（按数字段比较）
- `url`：安装包直链；如果是 GitHub 地址，启动器会自动附加 gh-proxy 加速备用
- `sha256`：可选，提供则下载后强制校验，不匹配则拒绝安装
- `mandatory`：保留字段，暂无强制界面

## 2. 生成 sha256

Windows PowerShell：

```powershell
Get-FileHash "Installer\ACLauncher-Setup-0.99.2.exe" -Algorithm SHA256 |
  Select-Object -ExpandProperty Hash
```

## 3. 部署到 Cloudflare Pages

1. 在 Cloudflare 控制台创建 Pages 项目（直接上传模式即可），根目录放 `latest.json`；
2. 部署后得到地址，如 `https://aclauncher.pages.dev/latest.json`；
3. 把 `Services/UpdateService.cs` 里的默认清单地址改成你的实际地址，重新编译发布。

也可以用一个极简 Worker 代替 Pages：

```js
export default {
  async fetch() {
    return new Response(await (await fetch("https://你的备份地址/latest.json")).text(), {
      headers: { "content-type": "application/json", "cache-control": "no-cache" },
    });
  },
};
```

## 4. 发版流程

1. 改版本号（csproj 的 `Version/FileVersion`、iss 的 `MyAppVersion`、窗口标题、关于页文字）；
2. 编译 + ISCC 打包；
3. 上传安装包到 GitHub Release / R2；
4. 计算 SHA-256，更新 `latest.json` 并重新部署 Pages；
5. 老版本启动器下次启动（或点「检查更新」）即可看到更新提示，下载校验后自动静默安装并重启。
