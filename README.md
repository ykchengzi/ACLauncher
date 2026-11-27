# ACLauncher — 《我的世界》Java 版启动器

基于 **C# + WinUI 3 (Windows App SDK) + MVVM** 的现代化 Minecraft: Java Edition 启动器，
指向与 `ACLauncher.exe` 同目录的本地 `.minecraft` 文件夹，解析版本 JSON 并启动游戏。

## 目录结构

```
ACLauncher/
├── ACLauncher.csproj          # 项目文件（.NET 8 + WinUI 3，非打包模式）
├── app.manifest               # 高 DPI / 长路径支持
├── App.xaml / App.xaml.cs     # 应用入口（含主题应用）
├── MainWindow.xaml / .cs      # 主窗口（NavigationView 四页导航）
├── Models/
│   ├── Config.cs              # config.json 模型
│   ├── VersionJson.cs         # 版本 JSON 模型（新旧结构兼容）
│   ├── Library.cs             # 依赖库 / 规则 / 条件参数模型
│   ├── Account.cs             # 账户模型（Microsoft / 离线）
│   └── AppSettings.cs         # 内部设置模型
├── Helpers/
│   ├── JsonHelper.cs          # JSON 序列化工具
│   ├── MavenHelper.cs         # Maven 坐标 → libraries 路径
│   ├── OsInfo.cs              # 系统名 / 架构（rules 匹配）
│   └── CommandLineHelper.cs   # 命令行拆分
├── Services/
│   ├── ConfigService.cs       # 读写 config.json
│   ├── SettingsService.cs     # 读写 settings.json
│   ├── VersionParserService.cs# 版本 JSON 解析 + 规则匹配
│   ├── JavaService.cs         # Java 自动检测 / 版本读取
│   ├── AuthService.cs         # Microsoft 设备码登录 + 离线登录 + 账户管理
│   ├── GameLaunchService.cs   # 游戏启动核心（ClassPath / 参数 / 进程）
│   └── LogService.cs          # 日志管理
├── ViewModels/
│   ├── ViewModelBase.cs       # INotifyPropertyChanged + 命令
│   ├── MainViewModel.cs       # 主页（版本 / 目录 / 启动）
│   ├── AccountViewModel.cs    # 账户页
│   ├── SettingsViewModel.cs   # 设置页
│   └── LogViewModel.cs        # 日志页
└── Views/
    ├── MainPage.xaml / .cs
    ├── AccountPage.xaml / .cs
    ├── SettingsPage.xaml / .cs
    └── LogPage.xaml / .cs
```

## 运行前提

```
某个目录/
├── ACLauncher.exe
├── config.json            # {"game_version": "1.20.4"}
└── .minecraft/
    ├── versions/1.20.4/1.20.4.json
    ├── versions/1.20.4/1.20.4.jar
    ├── libraries/
    └── assets/
```

- 游戏文件需已完整存在，启动器**不负责下载**任何文件。
- 游戏版本由 `config.json` 锁定，UI 不可更改。

## 构建

1. 安装 **.NET 8 SDK**（或更高版本）与 **Visual Studio 2022**（勾选"Windows 应用 SDK / WinUI 开发"工作负载）。
2. 打开 `ACLauncher.csproj` 或直接用命令行：

```
dotnet build ACLauncher.csproj -c Release
```

3. 产物位于 `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\ACLauncher.exe`。
   将 `ACLauncher.exe`、`config.json` 与 `.minecraft` 放到同一目录即可运行。

> 项目使用**非打包（unpackaged）**模式，运行需要 Windows App SDK 运行时
> （安装 Visual Studio 时会自带）。若需免运行时分发，可在 csproj 中把
> `<WindowsAppSDKSelfContained>` 改为 `true` 后重新发布。

## 使用前必读

### 1. Microsoft 正版登录

`Services/AuthService.cs` 顶部的 `ClientId` 常量默认是全 0 占位符。
使用正版登录前，请先在 [Azure 门户](https://portal.azure.com) 注册应用：

1. "Microsoft Entra ID" → "应用注册" → "新注册"；
2. 受支持的账户类型：**个人 Microsoft 账户**；
3. 平台配置 → 添加 **"移动和桌面应用程序"**，重定向 URI 填 `http://localhost`；
4. 将"应用程序(客户端) ID"填入 `ClientId` 常量。

未配置 Client ID 时，点击"添加 Microsoft 账户"会给出中文提示，不影响离线模式。

### 2. Java 环境

- 设置页"自动检测 Java"会扫描 `JAVA_HOME`、`PATH`、`Program Files\Java`、
  Eclipse Adoptium、Microsoft、Zulu 等常见位置；
- 也可"手动浏览"选择 `javaw.exe` / `java.exe`；
- **手动配置优先级高于自动检测**。

### 3. 版本兼容性

- 新版（约 1.13+）：解析 `arguments.jvm` / `arguments.game` 模板，按 `rules` 过滤；
- 旧版（1.12.2 及以前）：解析 `minecraftArguments` 字符串；
- 库路径优先取 `downloads.artifact.path`，缺失时按 Maven 坐标推导；
- native 库（`"natives": "windows"`）自动解压 `.dll` 到
  `.minecraft/versions/<版本>/natives/`。

## 功能一览

| 页面   | 内容 |
|--------|------|
| 主页   | 当前账户、锁定版本号、.minecraft 路径、"启动游戏"大按钮与状态文字 |
| 账户   | 添加 Microsoft / 离线账户、切换、删除，记住上次登录账户 |
| 设置   | Java 自动检测 + 手动浏览、最小/最大内存、自定义 JVM/游戏参数、亮/暗/跟随系统主题 |
| 日志   | 实时滚动显示游戏 stdout/stderr 与启动步骤，支持清空 |
