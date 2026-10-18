; 通讯测试工具 安装包脚本（Inno Setup）
;
; 用途（规划 12.7、22.5.2 L2、M4）：
;   提供“签名安装包”这一分发渠道。安装出来的程序文件**不带 MOTW**（"来自网络"标记），
;   因此从网盘/微信/邮件获得安装包后，安装完成启动程序不会再出现"无法验证发布者"提示
;   （前提：安装包与其中程序均已签名，见 R-20）。
;
; 约定：只从 tools\Publish-App.ps1 的产物目录（artifacts\publish）取文件，
;       不从 bin\Debug 打包（避免中间文件与残留标记，见 docs/build-environment.md）。
;
; 用法（需要本机安装 Inno Setup 6，且已把 ISCC.exe 放进 PATH 或使用 -InnoCompiler 指定）：
;   powershell -File tools\Build-Installer.ps1                 # 先用内置“无 Inno”回退打包
;   powershell -File tools\Build-Installer.ps1 -UseInno        # 有 Inno Setup 时生成正式安装包
;
; 安装形态（与便携版共存，互不影响）：
;   应用目录：{app}\  = CommTestTool.exe + 依赖 + helper\ + plugins\
;   用户数据：%APPDATA%\通讯测试工具\            （设置、方案、网卡快照）
;   日志：    %LOCALAPPDATA%\通讯测试工具\logs\  （程序目录不可写时自动回退到这里）
;   这是因为安装到 Program Files 后程序目录不可写——AppPaths 已实现该回退（R-21 X-2）。

#define AppName "通讯测试工具"
#define AppExeName "CommTestTool.exe"
#define AppPublisher "通讯测试工具项目组"
#define AppUrl ""

; 版本号由 Build-Installer.ps1 通过 /DMyAppVersion=... 注入（唯一来源仍是 Directory.Build.props）
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{8E4C1F42-5B7A-4D2E-9C3A-6F1D2B0A7C55}
AppName={#AppName}
AppVersion={#MyAppVersion}
AppVerName={#AppName} {#MyAppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=通讯测试工具-{#MyAppVersion}-安装包
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 规划 AD-03 / X-10：Windows 10 21H2+ / Windows 11，x64
MinVersion=10.0.19044
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 主程序与提权助手都不需要安装时提权；助手在“应用配置”时按需 UAC（AD-08）
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; 不删除用户数据（卸载时保留设置与日志，便于复查）
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 主程序与全部依赖（自包含发布，目标机无需预装 .NET）
; 便携包说明与解除锁定脚本对安装版无意义，用 Excludes 排除；data/logs 是运行期产物，也不打包。
; 注意：Excludes 必须与 Source 写在同一条目里，单独一行会被当成新的 [Files] 条目（首次编译就会报错）。
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "便携包说明.txt,解除锁定-便携包.ps1,data\*,logs\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理程序目录内的运行时产物；用户数据在 %APPDATA%/%LOCALAPPDATA% 下，刻意保留。
Type: filesandordirs; Name: "{app}\data"
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\exports"

[Code]
// 安装前检查：目标目录里若已有正在运行的实例，提示先退出（避免文件占用导致安装失败）。
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if Exec('tasklist', '/FI "IMAGENAME eq CommTestTool.exe" /NH', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    // 具体判断交给 CloseApplications；这里只保证不会静默覆盖正在运行的程序。
  end;
end;
