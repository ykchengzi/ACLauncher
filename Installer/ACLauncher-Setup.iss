; ACLauncher 安装脚本（Inno Setup 7）
; 构建：ISCC.exe ACLauncher-Setup.iss
; 功能：桌面快捷方式 / 开始菜单 / 任务栏固定（默认勾选）+ .NET 8 与 Windows App Runtime 检测

#define MyAppName "ACLauncher"
#define MyAppVersion "0.99.3-beta"
#define MyAppExeName "ACLauncher.exe"
#define MyAppPublisher "橙猫生存服务器"
#define SourceDir "E:\Wendang\ACLauncher\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"

[Setup]
AppId={{374BABD9-322E-4A77-ABBA-48A87101F4BF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
; 安装到 Program Files（标准程序安装位置，不再使用 AppData 隐藏目录）
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=E:\Wendang\ACLauncher\Installer
OutputBaseFilename=ACLauncher-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=ACLauncher.ico
SetupLogging=yes
CloseApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
; 无 flags = 默认勾选且每次安装默认勾选（不记忆用户上次取消）
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"
Name: "startmenu"; Description: "添加到开始菜单"; GroupDescription: "附加任务:"
Name: "taskbarpin"; Description: "固定到任务栏"; GroupDescription: "附加任务:"

[Dirs]
; 授予 Users 组修改权限：游戏运行需要在安装目录读写 .minecraft（日志 / 存档 / natives / 资源补全）
Name: "{app}"; Permissions: users-modify

[Files]
; 启动器全部文件（自包含部署：.NET 与 Windows App SDK 运行时已随程序分发，用户无需另外安装），排除用户数据
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "config.json,settings.json,accounts.json,.minecraft\*,film_microbiome\*,film_microbiome"
; 任务栏固定脚本（安装时解压到临时目录调用，不留在安装目录）
Source: "pin-taskbar.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenu
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
; 供任务栏固定使用的临时快捷方式（安装结束后由 Inno 自动清理；Inno 会自动追加 .lnk）
Name: "{tmp}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 自包含部署：.NET 运行时与 Windows App SDK 运行时随程序分发，安装前无需检查 / 安装任何运行时。

// 安装完成后：固定到任务栏（PowerShell Shell COM verb，不重启 explorer；失败自动兜底复制）
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('taskbarpin') then
    begin
      ExtractTemporaryFile('pin-taskbar.ps1');
      Exec('powershell.exe',
          '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\pin-taskbar.ps1') + '" "' + ExpandConstant('{tmp}\{#MyAppName}.lnk') + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
