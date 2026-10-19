; 通讯测试工具 安装包脚本（Inno Setup）
;
; 用途（规划 12.7、22.5.2 L2、M4）：
;   提供“签名安装包”这一分发渠道。安装出来的程序文件**不带 MOTW**（"来自网络"标记），
;   因此从网盘/微信/邮件获得安装包后，安装完成启动程序不会再出现"无法验证发布者"提示
;   （前提：安装包与其中程序均已签名，见 R-20）。
;
; 约定：只从 tools\Publish-App.ps1 的产物目录（artifacts\publish）取文件，
;       不从 bin\Debug 打包（避免中间文件与残留标记，见 内部文档）。
;
; 用法（需要本机安装 Inno Setup 6，且已把 ISCC.exe 放进 PATH 或使用 -InnoCompiler 指定）：
;   powershell -File tools\Build-Installer.ps1                 # 先用内置“无 Inno”回退打包
;   powershell -File tools\Build-Installer.ps1 -UseInno        # 有 Inno Setup 时生成正式安装包
;
; 安装形态（per-user，与便携版共存，互不影响）——见 内部文档：
;   应用目录：{app}\ = %LOCALAPPDATA%\Programs\通讯测试工具\（CommTestTool.exe + 依赖 + helper\ + plugins\）
;   用户数据：%LOCALAPPDATA%\通讯测试工具\data\     （设置、方案、网卡快照；由 install.flag 明确安装形态，不随可写性漂移）
;   日志/导出：%LOCALAPPDATA%\通讯测试工具\{logs,exports}\（卸载保留，不删除）
;   不写 Program Files、不请求管理员 → 安装全程不弹 UAC。
;   多人共享工位请改用便携 zip（不安装、各自解压运行）。

#define AppName "通讯测试工具"
#define AppExeName "CommTestTool.exe"
#define AppPublisher "通讯测试工具项目组"
#define AppUrl ""

; 卸载项键名（HKLM 旧版检测 + HKCU 新版标记共用）
#define AppUninstallKey "{8E4C1F42-5B7A-4D2E-9C3A-6F1D2B0A7C55}_is1"

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
; per-user 安装（方案 B）：装到 %LOCALAPPDATA%\Programs，不需要管理员，安装全程不弹 UAC。
PrivilegesRequired=lowest
; 不沿用旧安装目录：旧版是 admin 装在 Program Files，非管理员模式写不进去会报权限错误。
; 升级路径由 [Code] 的 LegacyAdminInstallExists 提示用户先卸载旧版（配置沿用，无需迁移）。
UsePreviousAppDir=no
; 不删除用户数据（卸载时保留设置与日志，便于复查；仅便携形态由 [Code] 清理）
UninstallDisplayIcon={app}\{#AppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
; 简体中文语言包随仓库提供（tools\ChineseSimplified.isl）：
; 部分 Inno 发行版的 Languages\ 目录不带中文，若直接用 compiler:Languages\ 会在换机器时编译失败。
; 这里优先用随仓库的副本，保证任何 Inno 6.x 上都能出同一种安装包。
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"

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
; 用户数据一律保留（方案 B / 决策 A）：data/logs/exports 在 %LOCALAPPDATA% 下，卸载程序不碰。
; 只删安装标记，避免卸载后残留 install.flag 把便携版误判成安装形态。
; 便携形态（存在 portable.flag）下程序目录里的运行期目录由 [Code] 的 CurUninstallStepChanged 清理。
Type: files; Name: "{app}\install.flag"

[Code]
// ---------------------------------------------------------------------------
// 方案 B（per-user 安装）：旧全局安装检测 + 安装标记 + 条件卸载清理
// 文档：内部文档
// ---------------------------------------------------------------------------

// 旧版是 admin 安装、注册在 HKLM；新版注册在 HKCU。同一 AppId → 需要提示先卸载旧版。
function LegacyAdminInstallExists(): Boolean;
var
  S: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppUninstallKey}', 'InstallLocation', S) or
    RegQueryStringValue(HKLM, 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{#AppUninstallKey}', 'InstallLocation', S);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if LegacyAdminInstallExists() then
  begin
    if MsgBox('检测到旧的“全局安装”（位于 Program Files，需要管理员）。' + #13#10 +
              '本安装包为“当前用户”安装，两者会并存，并各自出现在“应用和功能”中。' + #13#10 + #13#10 +
              '建议先卸载旧版本，再安装本版本。' + #13#10 +
              '配置会自动沿用（数据仍在 %LOCALAPPDATA%\通讯测试工具\data，无需迁移）。' + #13#10 + #13#10 +
              '是否继续安装？', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

// 安装完成后写安装标记：install.flag（主）+ HKCU 注册表（备）。
// 两者都用容错写入：注册表被策略禁用时只降级为“仅文件标记”，绝不让安装失败。
procedure CurStepChanged(CurStep: TSetupStep);
var
  Content: String;
begin
  if CurStep = ssPostInstall then
  begin
    Content :=
      'form=per-user' + #13#10 +
      'version={#MyAppVersion}' + #13#10 +
      'installedAt=' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + #13#10 +
      'dir=' + ExpandConstant('{app}') + #13#10;

    if not SaveStringToFile(ExpandConstant('{app}\install.flag'), Content, False) then
      Log('警告：写入 install.flag 失败，将依赖注册表标记');

    // HKCU 可能被策略/受限环境禁用：异常必须吞掉，否则整个安装会回滚。
    try
      RegWriteStringValue(HKCU, 'Software\通讯测试工具', 'InstallForm', 'per-user');
      RegWriteStringValue(HKCU, 'Software\通讯测试工具', 'InstallDir', ExpandConstant('{app}'));
    except
      Log('提示：HKCU 注册表标记写入失败（不影响安装，AppPaths 会改用 install.flag/unins000.exe 判定）');
    end;
  end;
end;

// 卸载：安装形态保留用户数据；仅便携形态（用户在程序目录放了 portable.flag）清理运行期目录。
// 注册表标记在这里一并删除，避免卸载后残留导致便携版被误判。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    try
      RegDeleteValue(HKCU, 'Software\通讯测试工具', 'InstallForm');
      RegDeleteValue(HKCU, 'Software\通讯测试工具', 'InstallDir');
      RegDeleteKeyIfEmpty(HKCU, 'Software\通讯测试工具');
    except
      Log('提示：清理 HKCU 注册表标记失败（忽略）');
    end;

    if FileExists(ExpandConstant('{app}\portable.flag')) then
    begin
      DelTree(ExpandConstant('{app}\data'), True, True, True);
      DelTree(ExpandConstant('{app}\logs'), True, True, True);
      DelTree(ExpandConstant('{app}\exports'), True, True, True);
    end;
  end;
end;
