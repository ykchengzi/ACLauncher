# ACLauncher 交接文档（交接给 zcode / glm5.3）

> 本文目标：让接手者**不依赖任何对话历史**，仅靠本仓库与本文即可继续开发、构建、实测、打包。  
> 最后更新：2026-10-27 ｜ 当前版本：**0.3.0**（已实测可真正启动 Java 版 MC 并已打包）

---

## 0. 一句话现状

ACLauncher 是一个 **C# / .NET 8 + WinUI 3（Windows App SDK，unpackaged 自包含）+ MVVM** 的《我的世界》Java 版启动器，全中文 UI。  
**0.3.0 已端到端实测通过**：真实 Modrinth 整合包（SadEPa 的整合包 / MC 26.2 + Fabric 0.19.5）能完成「下载 → 安装 → 自动下载 Java 25 → 启动 → 加载 Fabric 与 29 个 mod → 进入渲染/主菜单」。安装包 `Installer\ACLauncher-Setup-0.3.0.exe` 已生成。

- 项目根：`C:\Users\Lenovo\Doubao\chats\2026-09-13\new-chat-1\ACLauncher`
- 游戏目录（硬编码，勿改）：`%AppData%\.minecraft`（即 `C:\Users\<用户>\AppData\Roaming\.minecraft`）
- 启动器自身配置（config.json / settings.json / accounts.json）：启动器 exe 所在目录（`AppContext.BaseDirectory`）

---

## 1. 环境与工具链（本机已装，路径已核实）

| 工具           | 路径 / 版本                                                                                          |
| ------------ | ------------------------------------------------------------------------------------------------ |
| .NET SDK     | `C:\Program Files\dotnet\dotnet.exe`，9.0.318（可构建 net8.0-windows 目标）                              |
| MSBuild      | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe` |
| Inno Setup 7 | `C:\Program Files\Inno Setup 7\ISCC.exe`                                                         |
| 本机 Java      | 仅 `C:\Program Files\Java\jdk-21.0.12`（21.0.12）。**没有 Java 25**，26.2 依赖运行时由启动器首次启动时自动下载            |
| 操作系统         | Windows 10（10.0.19041），x64                                                                       |
| Shell        | PowerShell 7（pwsh）。**Bash 工具不可用**，所有命令走 PowerShell                                               |

关键 NuGet 包：`Microsoft.WindowsAppSDK 2.0.1`、`Microsoft.Windows.SDK.BuildTools 10.0.26100.4654`。

---

## 2. 三条核心命令（构建 / 实测 / 打包）

### 2.1 正式 Release 编译（x64）

编译前**必须先结束运行中的 ACLauncher**，否则会报 MSB3027 文件占用：

```powershell
Get-Process -Name ACLauncher,java,LaunchTest -ErrorAction SilentlyContinue | Stop-Process -Force
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
  "C:\Users\Lenovo\Doubao\chats\2026-09-13\new-chat-1\ACLauncher\ACLauncher.csproj" `
  -t:Rebuild -p:Configuration=Release -p:Platform=x64 -restore -v:m -nologo
```

输出目录：  
`bin\x64\Release\net8.0-windows10.0.19041.0\`（含 `ACLauncher.exe`、`7z\7za.exe`、`java_launch_wrapper\java_launch_wrapper.jar`、`licenses\...`）

### 2.2 打包安装包（Inno Setup）

先确认 `Installer\ACLauncher-Setup.iss` 里 `#define MyAppVersion "0.3.0"`，然后：

```powershell
& "C:\Program Files\Inno Setup 7\ISCC.exe" `
  "C:\Users\Lenovo\Doubao\chats\2026-09-13\new-chat-1\ACLauncher\Installer\ACLauncher-Setup.iss"
```

产物：`Installer\ACLauncher-Setup-<版本>.exe`（成功标志 `Successful compile`；lzma2/ultra64 + solid，约 5 分钟，体积约 88MB）。  
脚本里 `SourceDir` 是**绝对路径**，换机器需改。

### 2.3 非 GUI 端到端实测（重点，强烈优先用它验证）

本机的 GUI 自动化（豆包虚拟桌面 computer_use）当前不可用（PIP 初始化失败）。因此用内置控制台 harness **链接生产源码**做真实验证（不是复刻代码）。  
harness 已固化进仓库：`tools\LaunchTest\`（`LaunchTest.csproj` + `Program.cs`），用相对路径引用生产 `.cs`，无需 `-p:AclSrc`。

```powershell
# 构建 harness
& "C:\Program Files\dotnet\dotnet.exe" build `
  "C:\Users\Lenovo\Doubao\chats\2026-09-13\new-chat-1\ACLauncher\tools\LaunchTest\LaunchTest.csproj" -c Release -nologo

$exe = "C:\Users\Lenovo\Doubao\chats\2026-09-13\new-chat-1\ACLauncher\tools\LaunchTest\bin\Release\net8.0-windows\LaunchTest.exe"

# 启动已安装版本并观察 120 秒（离线账户 ACLTest）
Start-Process -FilePath $exe -ArgumentList 'launch','26.2-fabric-0.19.5','120' `
  -RedirectStandardOutput "$env:TEMP\out.log" -RedirectStandardError "$env:TEMP\err.log" -WindowStyle Hidden

# 实时日志在 harness 运行目录：tools\LaunchTest\bin\Release\net8.0-windows\launchtest.log（UTF-8）
```

harness 子命令：

- `install <整合包解压目录>`：仅安装整合包
- `launch <版本目录名> [观察秒数]`：启动并观察（默认 120s）
- `all <整合包解压目录> [观察秒数]`：安装后启动

**验收标准**：日志出现 `LWJGL version 3.4.1`、`Setting user: ACLTest`、Fabric/mods 加载、Render 线程持续输出；观察窗口内进程不退出即成功。  
**不得出现**：`Unrecognized option`、`Could not create the Java Virtual Machine`、`Only one quick play option`、`UnsatisfiedLinkError`、`Game crashed`、`-XstartOnFirstThread`（Windows 下应被 rules 过滤）。

测试结束后清理残留 java（harness 不自动杀游戏）：

```powershell
Get-CimInstance Win32_Process -Filter "Name='java.exe'" |
  Where-Object { $_.CommandLine -match 'KnotClient|net.minecraft|minecraft' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

> harness 只链接「无 WinUI 依赖」的源文件（清单见 csproj）。**不要**链接 VersionDownloadService、SevenZipService、AuthService、NetworkService、App / MainWindow / Bootstrap 等（含 `Microsoft.UI.Xaml` 或 `Microsoft.Win32` 启动逻辑，会导致控制台工程编译失败）。

---

## 3. 工程结构与文件职责

```
ACLauncher\
├─ ACLauncher.csproj        工程（net8.0-windows10.0.19041, UseWinUI, WindowsPackageType=None 自包含）
├─ App.xaml(.cs)            应用入口 / Bootstrap（unpackaged，DISABLE_XAML_GENERATED_MAIN）
├─ MainWindow.xaml(.cs)     主窗口 + 左侧导航
├─ Helpers\
│  ├─ CommandLineHelper.cs  命令行拆分（引号转义）
│  ├─ JsonHelper.cs         JSON 反序列化
│  ├─ MavenHelper.cs        Maven 坐标 → libraries 相对路径
│  └─ OsInfo.cs             OS 名/架构、Windows 版本(正则)、版本范围 versionRange 比较
├─ Models\
│  ├─ Account.cs            账户（离线 / 微软，含 Uuid/AccessToken/Xuid）
│  ├─ AppSettings.cs        设置（内存、线程数、下载源、Java 优先级等；EffectiveJavaPath）
│  ├─ Config.cs             config.json（当前版本等）
│  ├─ GameProcessPriority.cs 进程优先级映射（普通/高/低/最高/最低）
│  ├─ Library.cs            库/规则/参数项模型（LibraryRule、LibraryOs、OsVersionRange、ArgumentItem）
│  ├─ ModrinthIndex.cs      Modrinth 整合包索引（含 dependencies 连字符字段映射）
│  └─ VersionJson.cs        版本 JSON（继承、JavaVersion、Arguments 含 default-user-jvm、主 JAR 定位）
├─ Services\
│  ├─ AuthService.cs            微软设备码登录（RPS + Prism client_id，**禁止改回浏览器 RPA**）
│  ├─ ConfigService.cs          .minecraft 路径、版本扫描
│  ├─ DownloadService.cs        多线程下载（多候选 URL、.part+SHA1、停滞看门狗、HTTP/1.1）
│  ├─ GameLaunchService.cs      ★启动核心（解析→补资源→Java 选择→native→参数→起进程）
│  ├─ JavaRuntimeService.cs     ★按 Mojang manifest 自动下载 JRE（26.2 用，Java 25）
│  ├─ JavaService.cs            本机 Java 检测（JAVA_HOME/PATH/常见目录）
│  ├─ LogService.cs             控制台日志（event LineAdded）
│  ├─ NetworkService.cs         网络测速（GitHub 直连 / gh-proxy）
│  ├─ PackInstallService.cs     ★整合包安装（Modrinth / PCL2 系，识别→装原版→装加载器→文件→overrides）
│  ├─ SettingsService.cs        settings.json 读写
│  ├─ SevenZipService.cs        调用内置 7za.exe 解压（用 Microsoft.Win32，非 WinUI）
│  ├─ VersionDownloadService.cs 版本下载页下载（含 WinUI Visibility，harness 不链接）
│  └─ VersionParserService.cs   ★版本 JSON 解析 + inheritsFrom 递归合并 + 规则匹配
├─ ViewModels\  MainViewModel / AccountViewModel / NetworkViewModel / SettingsViewModel
│               VersionDownloadViewModel / SelectVersionViewModel / LogViewModel / ViewModelBase
├─ Views\       MainPage / AccountPage / NetworkPage / SettingsPage / VersionDownloadPage
│               SelectVersionPage / AboutPage / LogPage（.xaml + .xaml.cs）
├─ Installer\
│  ├─ ACLauncher-Setup.iss   Inno Setup 7 脚本
│  └─ pin-taskbar.ps1        任务栏固定（Shell COM verb，不重启 explorer）
├─ Resources\
│  ├─ 7z\7za.exe                          内置 7-Zip 内核
│  └─ java_launch_wrapper\java_launch_wrapper.jar   JLW（MIT）
├─ licenses\java_launch_wrapper-LICENSE.txt        MIT 版权声明（随分发保留）
├─ ThirdParty\
└─ tools\LaunchTest\  ★非 GUI 端到端验证 harness（见 2.3）
```

---

## 4. 关键启动流程（GameLaunchService.LaunchAsync）

1. `VersionParserService.ParseAsync(version)`：递归 `LoadMergedAsync` 处理 `inheritsFrom`（Fabric 继承原版），`MergeVersion` 合并库（子在前）、参数（父在前子在后）、主类/资源/下载信息/JavaVersion。
2. **`ResolveJavaAsync`（0.3.0 新增）**：读 `versionJson.JavaVersion`（component / majorVersion）。
   - 先用户指定 Java → 再本机检测；满足要求的最高版本胜出。
   - 都不满足且有 component → `JavaRuntimeService.EnsureRuntimeAsync` 自动下载官方 JRE 到 `.minecraft\runtime\<component>\<platform>\`，用其 `bin\java.exe`（带日志重定向、无窗口）。
   - 无 javaVersion 的旧版本：沿用用户指定 / 本机最高。
3. `EnsureResourcesAsync`（CheckResources=true）：补核心 JAR、依赖库（含 Fabric Maven 推导、多镜像）、assets index、assets objects；多线程下载。
4. `PrepareNativesDirectory` + `ExtractNatives`：建 `natives\{java,jna,lwjgl,netty}` 子目录；旧版 LWJGL（3.3 及以前，`natives` 字典）手动解压到 natives 根；新版 LWJGL 3.4 的 native jar **放入 classpath 自行解压**。
5. `BuildClassPath`：普通库 + **新版 native jar**（旧版 native 不进 cp）。
6. `BuildJvmArgs`：用户 `-Xmx/-Xms` 在前；展开 `arguments.jvm`（规则过滤）；26.2 额外展开 `arguments.default-user-jvm`（跳过其中的 -Xms/-Xmx，保留 GC/CompactObjectHeaders）；再追加用户额外 JVM 参数。
7. `BuildGameArgs`：展开 `arguments.game`（**features 规则一律不匹配**，否则 quick play 崩溃）；占位符替换；默认直连服务器（见第 6 节）。
8. 按需 JLW（仅 UTF-8 Beta + Java<19）：`java -cp ... -jar wrapper.jar <主类> <游戏参数>`。
9. 起进程（`RedirectStandardOutput/Error` + `BeginOutputReadLine` 实时进日志），设进程优先级，`Exited` 事件更新状态。

---

## 5. 0.3.0 修复的关键缺陷（都已实测验证，勿回退）

1. **版本继承未合并**：Fabric profile 只有自己的库/主类/参数，原版部分会丢。已实现 `LoadMergedAsync/MergeVersion`。
2. **Fabric 库无 downloads 字段**：只有 `name` + 顶层 `url`（maven.fabricmc.net）。旧逻辑全跳过。已改为 `lib.GetPath()` 从 Maven 坐标推导 + 多镜像 URL。
3. **Modrinth dependencies 连字符字段不映射**：`fabric-loader` 等字段 System.Text.Json 默认不映射，`FabricLoader` 恒 null → 错误走纯原版。已在 `Models\ModrinthIndex.cs` 加 `[JsonPropertyName("fabric-loader")/("quilt-loader")/("forge")/("neoforge")/("minecraft")]`。
4. **装 Fabric 前先装原版**：`InstallVanillaVersionAsync`（幂等）。
5. **下载卡死/句柄占用**：`.part` 写流未释放导致 SHA1/File.Move 报「文件被占用」。DownloadService 现用 SocketsHttpHandler（ConnectTimeout 15s、强制 HTTP/1.1、连接池 5min）、**60 秒无数据停滞看门狗**、单文件总超时 20min、候选 URL 两轮重试。
6. **可选资源容错**：整合包中 resourcepacks/shaderpacks/screenshots 下载失败仅警告不阻断；mods/核心失败才致命。
7. **Java 版本不匹配（26.2 致命）**：26.2 要求 Java 25（`javaVersion.majorVersion=25`, component=`java-runtime-epsilon`），Java 21 启动即 `Unrecognized option: --sun-misc-unsafe-memory-access=allow`。已实现自动下载 JRE（见第 7 节）。
8. **quick play 崩溃**：`arguments.game` 中带 `features` 的规则（`--quickPlaySingleplayer/Multiplayer/Realms`、`--demo`、`--width/--height`）被错误地无条件加入，导致 `Only one quick play option can be specified`。`LibraryRule` 新增 `Features` 字段，含 features 的规则默认不匹配。
9. **新版 native 处理**：LWJGL 3.4 的 native jar 需在 classpath 上自行解压，并需要 `natives\java|jna|lwjgl|netty` 子目录。
10. **新参数结构**：26.2 用 `arguments.default-user-jvm`（内存/GC/CompactObjectHeaders）与 `os.versionRange` 规则。模型与规则匹配已支持。

---

## 6. 网络与镜像规则（不要改错）

- **加速源白名单（UI 只保留这两个）**：GitHub 直连（`""`，不改 URL）与 `https://gh-proxy.org/`。
- 拼接方式：`mirror.TrimEnd('/') + "/" + 原始URL`。例：`https://gh-proxy.org/https://github.com/user/repo/releases/download/.../file.zip`。
- 内置整合包（UI 不显示链接、不显示「占位/GitHub Release/内置 zip」字样）：
  - 显示标题：**SadEPa的整合包 (26.2)**
  - URL：`https://github.com/ykchengzi/aclc/releases/download/0.0.0.0.0.0.0.1/26.2_By_SadEPa.zip`
- Minecraft 资源镜像：BMCLAPI `https://bmclapi2.bangbang93.com`
  - 库：`/maven/<path>`；assets：`/assets/<hh>/<hash>`；核心 JAR / piston-data 替换域名；JRE：`/v1/objects/<sha1>/<file>`、`/v1/packages/<sha>/manifest.json`。
- Fabric meta：`https://meta.fabricmc.net/v2/versions/loader/<mc>/<loaderVer>/profile/json`；Quilt：`https://meta.quiltmc.org/v3/...`；原版清单：`https://piston-meta.mojang.com/mc/game/version_manifest_v2.json`。
- **默认服务器直连**：主机 `mc.ambercat.top`、端口 `25565`（GameLaunchService 常量）。
  - 支持快速游戏的新版（`arguments.game` 含 `quickPlay`）：`--quickPlayMultiplayer mc.ambercat.top:25565`。
  - 旧版：`--server mc.ambercat.top --port 25565`。
  - 注意：26.2 已忽略 `--server`（日志 `Completely ignored arguments`），必须用 quickPlay。


## 8. 整合包安装（PackInstallService）

- 入口：`PackInstallService.Instance.TryInstallPackAsync(解压目录, 显示名)` → 返回版本目录名（无法识别为 Modrinth 时返回 null）。
- Modrinth 结构：根 `modrinth.index.json` + `overrides/`（也兼容 `client-overrides/`）。index 含 `dependencies`（minecraft / fabric-loader / quilt-loader / forge / neoforge）与 `files[]`（path/url/sha1/fileSize，env client 需保留）。
- 流程：装原版（幂等）→ 装加载器（Fabric/Quilt/Forge/NeoForge）→ 多线程下载 files 到对应目录（mods/resourcepacks/shaderpacks/…）→ 复制 overrides。
- 下载物为 zip，用内置 `Resources\7z\7za.exe` 解压（SevenZipService），解压后删原 zip。
- 也兼容 PCL2 / 各大启动器导出的整合包（识别 `.minecraft` 布局 / version json / mods 等）。
- 26.2 是**独立 MC 版本**（Minecraft 已用「年份.序号」命名），**不是** 1.20.1 的构建名；Fabric 0.19.5 是其加载器。

---

## 9. 微软登录（AuthService）

- 设备码流程（OAuth device code），使用 RPS + 官方（Prism 启动器同款）client_id；**不允许改成浏览器 RPA / 手动抓包**。
- 取得设备码后：红字提示「已复制设备码，3 秒后将打开网页，输入设备码登录完毕后返回启动器即可登录成功。」，3 秒后自动打开微软验证页。
- 设置页**不暴露**「Microsoft 账户」client_id 手动配置项（已按要求移除）。

---

## 10. 安装器（Inno Setup 7）

- AppId（升级用，勿改）：`{374BABD9-322E-4A77-ABBA-48A87101F4BF}`
- 安装到 `{autopf}\ACLauncher`（Program Files），`PrivilegesRequired=admin`，`[Dirs]` 授 Users 修改权限（游戏要在安装目录写配置）。
- 三个附加任务默认勾选：桌面快捷方式、开始菜单、固定任务栏。
- 任务栏固定：`CurStepChanged(ssPostInstall)` 解压 `pin-taskbar.ps1` 用 Shell COM verb 固定（**不重启 explorer**）；失败兜底复制快捷方式。
- `InitializeSetup` 检测 .NET 8 Desktop Runtime 与 Windows App Runtime 2.x，缺失则弹窗给官方下载链接。
- `[Files]` 排除 `config.json/settings.json/accounts.json/.minecraft\*`，避免覆盖用户数据。
- 卸载程序由 Inno 自动生成（`unins000.exe` 命名是 Inno 默认行为，多副本会递增 unins001…）。

---

## 11. 用户硬约束与偏好（务必遵守，改动前先对照）

- 全中文 UI；**拒绝暗色模式**。
- UI 文案**禁止出现**：「手动改配置文件」「占位」「GitHub Release」「内置 zip」、自定义 JVM/游戏参数、MicrosoftClientId、7z 路径、仓库地址、自定义下载源仓库。
- 网络设置里**不要**让用户配置 7zip 路径或手动配置仓库；加速源只有「GitHub 直连」「Gh-Proxy 加速(<https://gh-proxy.org)」。>
- 安装目录用 Program Files（**不要** AppData）；但 `.minecraft` 必须在 `%AppData%\.minecraft`（**不要**放 exe 同级）。
- 设置页不提供自定义 Java 启动参数；默认追加服务器地址 `mc.ambercat.top`。
- 设置/网络设置等页面的「未保存改动」黄色提示：**只有内容真的改变了才出现**，且始终置于该页面顶部。
- 版本号非 1.0 正式版前用 `0.x`（当前 0.3.0）。
- 版本下载页：一行一个版本；标题「SadEPa的整合包 (26.2)」，不显示下载链接。
- 关于页：副标题「橙猫生存服务器」；致谢 **SadEPa**；含 JLW（MIT, Copyright (c) 2022 00ll00）署名；不写游戏目录、不写功能说明、不提内置 zip；可写「支持安装 Modrinth / PCL 等整合包」。
- 用户沟通偏好：安静直接、命令用代码块、不喜欢反问确认、不要阴阳怪气。

---

## 12. 第三方组件与协议

- **Java Launch Wrapper（JLW）**：`https://github.com/00ll00/java_launch_wrapper`，MIT，Copyright (c) 2022 00ll00，包名 `oolloo.jlw`，Main-Class `oolloo.jlw.Wrapper`，manifest 需 `Add-Opens: java.base/jdk.internal.loader`。
  - 作用：修复 Windows 开启「Beta：使用 Unicode UTF-8」时 Java<19 用错误编码解析命令行（JDK-8272352，Java 19 修复）。
  - 启用策略：仅当注册表 ACP/OEMCP=65001 且 Java major<19 才注入；正常系统不注入（保持命令干净）。Java 25 下必然不启用。
  - 本机无 zig/gradle，jar 是用 javac `--release 8` 手动编译的 6 个 class（不含 native dll，运行时找不到 dll 会自动回退 CimCommandLineLoader，作者官方备用路径）。
- **7-Zip**：内置 `7za.exe` 用于解压（7z 源码为 LGPL/特殊许可，当前仅分发官方命令行程序；如自行编译需遵循其许可）。

---

## 13. 已知问题 / 可继续的方向

- 离线账户直连 `mc.ambercat.top`：若服务器开正版验证，离线号会被拒（属账户问题，提示用户用微软登录）。启动器侧链路已正常。
- 首次启动新版（26.2）需下载约 5000+ assets（数百 MB）+ 100MB JRE，属正常一次性耗时；EnsureResources 会阻塞到补齐才起进程。
- GUI 自动化（computer_use）在该环境不可用，验证一律走 `tools\LaunchTest` harness。
- 若未来 MC 更换 `arguments` 结构（如新增 quick play / jvm 段），优先在 `Models\VersionJson.cs`、`VersionParserService`、`GameLaunchService.BuildJvmArgs/BuildGameArgs` 三处对照官方 JSON 扩展。
- 若 gh-proxy.org 或 BMCLAPI 域名变化，需同步第 6 节白名单与 `BuildLibraryUrls/JavaRuntimeService` 里的镜像。

---

## 14. 给接手者的上手清单（建议照做）

1. 读本文 + `ACLauncher.csproj`；确认第 1 节工具链存在。
2. 跑 2.1 Release 编译（先杀进程），确认 0 错误。
3. 跑 2.3 构建 harness；若本机已有 `%AppData%\.minecraft\versions\26.2-fabric-0.19.5` 与 `runtime\java-runtime-epsilon`，直接 `launch` 验证；日志见 `launchtest.log`。
4. 需要重装整合包时：下载 zip → 7z 解压到某目录 → harness `install <目录>`（产物见 `%AppData%\.minecraft\versions`）。
5. 改完代码：harness 构建验证 → 正式 Release 编译 → ISCC 打包 → 用 `present_files` 交付 `Installer\ACLauncher-Setup-<版本>.exe`。
6. 任何「启动不了」先看 `launchtest.log` 与 `%AppData%\.minecraft\crash-reports\` 最新崩溃报告头部（Description / 异常栈通常直接定位）。
