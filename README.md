# ACLauncher

> 橙猫生存服务器 专属《我的世界》Java 版启动器

基于 **C# / .NET 8 + WinUI 3（Windows App SDK）** 开发的 Minecraft: Java Edition 启动器，
采用 MVVM 架构，支持微软正版、离线、外置（Yggdrasil）三种登录方式，
并内置版本安装、模组与整合包下载等完整功能。

当前版本：**v0.99.3-beta（内测版）** · 平台：Windows 10 1809 及以上（x64） · 界面语言：简体中文 · 许可：GPL-3.0-or-later

---

## 简介

ACLauncher 面向橙猫生存服务器玩家，把「装游戏 → 装模组 → 登录 → 进服」压缩成几步点击。
程序为**自包含部署**：安装包已内置 .NET 8 运行时、Windows App SDK 运行时与 VC++ 运行库，
目标机器无需预装任何依赖，双击安装即可使用。

## 功能特性

### 账户

- **微软正版登录**：OAuth 2.0 设备码流程，一次登录长期有效，令牌过期自动刷新。
- **离线登录**：输入玩家名即可；头像按离线 UUID 对应 Steve / Alex / Ari / Efe / Kai / Makena / Noor / Sunny / Zuri 的皮肤头部。
- **外置登录**：内置 LittleSkin，也可填写任意支持 Yggdrasil 协议（authlib-injector）的服务器地址。
- **多角色选择**：账号下有多个角色时，登录后由玩家自行选择使用哪一个。
- **真实头像**：正版与外置账号读取当前角色的皮肤头部。

### 游戏版本

- 支持原版及 **Fabric / Quilt / Forge / NeoForge** 加载器安装。
- 安装时同步补全核心 JAR、依赖库、Natives 与全部资源文件，**结束前做完整性校验**，缺失即明确报错。
- 下载过程实时显示当前正在下载的文件与总进度。
- 安装后可自定义版本昵称，默认填入原版版本号。
- 内存不足或 Java 版本不匹配时，自动下载 Mojang 官方对应版本的 Java 运行时。

### 下载中心

- **整合包**：内置整合包源与 Modrinth 整合包，可「安装」或「仅下载源文件到本地」（`.mrpack` 解包由内置 7-Zip 完成）。
- **模组 / 资源包 / 光影**：接入 Modrinth 搜索，支持筛选游戏版本与加载器，详情页标注「正式版 / 测试版 / 内测版」。
- 项目简介支持中文翻译，快速判断模组用途。

### 主页与管理

- 服务器状态卡片：在线人数、延迟、MOTD 与服务器版本（展示地址 `mc.ambercat.top`）。
- 版本切换、Java 环境摘要、最近日志速览，一键启动 / 取消启动。
- 右键版本卡片进入**版本管理**：查看模组、存档与版本占用，编辑版本设置。

### 设置

- **Java 环境**：自动扫描 `JAVA_HOME`、`PATH`、Program Files 等常见位置，也可手动指定。
- **JVM 内存**：手动设定或交由启动器按物理内存自动分配。
- **游戏进程优先级**：普通 / 高 / 低 / 最高 / 最低。
- **版本隔离**：开启后每个版本使用独立游戏目录（`versions/<版本>`），世界、服务器列表、截图、资源包互不互通；库与资源仍共享，不重复占用磁盘。
- **启动后自动加入橙猫生存服务器**：默认开启。
- **主题**：暗色 / 亮色 / 跟随系统。切换后启动器会自动重启以完整应用主题。

### 网络

- 可调下载线程数（1–16）。
- 原版文件下载源可在 Mojang 官方与 BMCLAPI 镜像间切换，源不可用时自动回退。
- 启动前自动补全缺失文件，适合迁移或手动缩水的游戏目录。

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809（build 17763）及以上、Windows 11 |
| 架构 | x64 |
| 运行时 | **无需预装**（安装包自包含 .NET 8 与 Windows App SDK） |
| Java | 由启动器自动检测 / 自动下载；也可手动指定 |

## 安装与使用

1. 运行安装包 `ACLauncher-Setup-0.99.3-beta.exe`。
2. 按需勾选「创建桌面快捷方式 / 添加到开始菜单 / 固定到任务栏」，完成安装。
3. 首次启动后：
   - 进入 **账户** 页添加账户（微软正版 / 离线 / 外置登录）；
   - 进入 **下载** 页安装游戏版本或整合包；
   - 回到 **主页** 点击「启动游戏」。若已开启自动加入，启动完成后会直接进入橙猫生存服务器。

> 游戏数据默认位于 `%AppData%\.minecraft`，与官方启动器共用。开启版本隔离后，
> 各版本的世界与设置分别存放在 `versions/<版本>/` 下。

## 常见问题

**Q：微软登录时授权页显示的是 Prism Launcher？**
这是正常现象。启动器使用公开注册的 Minecraft 客户端 ID（与 Prism Launcher 相同），
因此授权页显示该应用名称，不影响登录与游戏。

**Q：服务器状态一直显示「离线或无法连接」？**
请确认网络可正常访问外网。状态查询使用独立的查询端口，与游戏内连接地址不同。

**Q：切换主题后启动器自己关了又开？**
这是预期行为。WinUI 3 的控件模板在加载时即固化主题，只有重启才能让所有区域统一换色。

**Q：安装版本时提示「文件完整性校验未通过」？**
说明部分文件下载失败。重新执行安装即可，启动器只会补下缺失的文件。

**Q：这个版本为什么标注「内测版」？**
0.99.x 期间功能仍在调整，界面上会保留内测版标注。

## 从源码构建

1. 安装 **.NET 8 SDK** 与 **Visual Studio 2022**（勾选「.NET 桌面开发」与「Windows 应用 SDK / WinUI」工作负载）。
2. 在仓库根目录执行：

```bash
dotnet build ACLauncher.csproj -c Release -p:Platform=x64
```

3. 产物位于：

```
bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\ACLauncher.exe
```

4. 生成安装包（需 [Inno Setup 7](https://jrsoftware.org/isinfo.php)）：

```bash
ISCC.exe Installer\ACLauncher-Setup.iss
```

## 目录结构

```
ACLauncher/
├── ACLauncher.csproj           # 项目文件（.NET 8 + WinUI 3，非打包自包含）
├── App.xaml / App.xaml.cs      # 应用入口、主题应用与自动重启
├── MainWindow.xaml / .cs       # 主窗口（NavigationView 导航框架）
├── app.manifest                # 高 DPI / 长路径支持
├── Models/                     # 数据模型
│   ├── Account.cs              # 账户（微软 / 离线 / 外置）
│   ├── AppSettings.cs          # settings.json 模型
│   ├── Config.cs               # config.json 模型（锁定当前版本）
│   ├── VersionJson.cs          # 版本 JSON（新旧结构兼容）
│   ├── Library.cs              # 依赖库 / 规则 / 条件参数
│   └── ModrinthIndex.cs        # .mrpack 索引
├── Helpers/                    # JSON、Maven 坐标、系统信息、命令行拆分
├── Services/                   # 业务服务
│   ├── ConfigService.cs        # 读写 config.json、识别已装版本
│   ├── SettingsService.cs      # 读写 settings.json
│   ├── AuthService.cs          # 微软设备码登录、离线登录、账户管理
│   ├── YggdrasilService.cs     # 外置登录（authlib-injector 协议）
│   ├── SkinService.cs          # 角色皮肤头部渲染
│   ├── VersionParserService.cs # 版本 JSON 解析与规则匹配
│   ├── VersionDownloadService.cs # 原版版本清单与下载
│   ├── LoaderService.cs        # Fabric / Quilt / Forge / NeoForge 安装
│   ├── PackInstallService.cs   # 原版 / 加载器安装编排与整合包安装
│   ├── ResourceService.cs      # 缺失文件收集与完整性校验
│   ├── ModrinthService.cs      # Modrinth 搜索与版本查询
│   ├── GameLaunchService.cs    # 启动核心（ClassPath / 参数 / 进程）
│   ├── JavaService.cs          # Java 检测与版本读取
│   ├── JavaRuntimeService.cs   # Mojang 运行时自动下载
│   ├── ServerStatusService.cs  # 服务器状态查询（Server List Ping）
│   ├── HttpFactory.cs          # 统一 HttpClient（IPv4 优先建连）
│   └── ...
├── ViewModels/                 # 视图模型（MVVM）
├── Views/                      # 页面（主页 / 账户 / 选择版本 / 下载 / 版本管理 / 网络 / 设置 / 日志 / 关于）
├── Resources/                  # 7za、VC++ 运行库、默认皮肤头像、图标
├── licenses/                   # 第三方许可与声明
├── Installer/                  # Inno Setup 脚本与安装资源
├── tools/LaunchTest/           # 无界面测试 harness
└── docs/                       # 补充文档
```

## 技术栈

- **.NET 8 (LTS)** + **WinUI 3 / Windows App SDK 2.0.1**
- **MVVM**：`ViewModels` 与 `Views` 分离，命令使用自定义 `AsyncRelayCommand`
- **非打包（unpackaged）自包含部署**：`WindowsPackageType=None`、`SelfContained=true`
- 安装包由 **Inno Setup 7** 生成
- 网络层统一使用 `SocketsHttpHandler` + **IPv4 优先建连**，规避 IPv6 不可达导致的长超时

## 许可

本项目源代码采用 **GNU General Public License v3.0 或更高版本（GPL-3.0-or-later）** 授权，
全文见 [`LICENSE`](LICENSE)。你可以自由使用、修改和再分发，但分发修改后的版本时必须同样开源。

### 不在许可范围内的内容

- **图像素材**：`Resources/Skins/` 下的 Steve / Alex / Ari / Efe / Kai / Makena / Noor / Sunny / Zuri
  皮肤头部为 **Mojang Studios 的版权素材**，随包分发仅用于离线账户头像，不随本项目的许可证授予任何权利。
- **名称与标识**：「ACLauncher」「橙猫生存服务器」名称及 `Resources/ACLauncher.ico` 图标不作为商标授权；
  基于本项目代码的分支不得暗示自己由橙猫生存服务器官方发布。
- **第三方组件**：7-Zip、Java Launch Wrapper、Windows App SDK、.NET 等各自遵循其原有许可，
  详见 [`licenses/THIRD-PARTY-NOTICES.txt`](licenses/THIRD-PARTY-NOTICES.txt) 与 `licenses/` 下的许可全文。

### 致谢

- **Minecraft** 是 Mojang Studios 的商标，本项目与其无隶属关系。
- 感谢 **SadEPa** 制作整合包。

## 相关文档

- [更新日志](CHANGELOG.md)
- [在线更新说明](docs/ONLINE_UPDATE.md)
