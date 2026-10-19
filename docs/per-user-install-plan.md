# 安装包改 per-user：完整改动清单（方案 B）

> 状态：**待评审，尚未落地**。
> 目标：安装过程不再触发 UAC，同时**不丢配置、不破坏升级、不误删数据**。
> 关联：[安全警告矩阵](security-warning-matrix.md)、[代码签名计划](code-signing-plan.md)。

---

## 0. 三条设计决策（先定这个，后面都是推论）

| # | 决策 | 理由 |
|---|---|---|
| **D1** | **安装标记优先于“程序目录可写性”**，新增 `install.flag` + HKCU 注册表标记 | 现在 `AppPaths` 是“可写即便携”，per-user 装到 `%LOCALAPPDATA%\Programs` 后目录可写 → 会被误判为便携 → 数据搬家。加标记后**数据目录与今天完全一致**，老用户零迁移 |
| **D2** | **形态决定目录**，可写性只作兜底 | 便携→程序目录；安装→用户目录；都不可写→temp。语义清楚，可测试 |
| **D3** | **卸载不删用户数据**（安装形态）；只有便携形态才清理 `{app}\data|logs|exports` | per-user 下 `{app}\data` 可能真有数据，而 `[UninstallDelete]` 会无条件删 |

**关键收益**：D1 让“旧 admin 安装 → 新 per-user 安装”的用户**配置零迁移**（两者都读 `%LOCALAPPDATA%\通讯测试工具\data`）。

> 注意：本方案**不解决**“无法验证发布者 / SmartScreen”，那是签名问题，见 [代码签名计划](code-signing-plan.md)。

---

## 1. `tools/installer.iss`（6 处）

### 1.1 提权模式（第 49–54 行）

```ini
; 改前
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline

; 改后
PrivilegesRequired=lowest
; PrivilegesRequiredOverridesAllowed 删除：lowest 已无“降级”可覆盖
```

`{autopf}` 会自动映射为 `{userpf}` = `%LOCALAPPDATA%\Programs`（Inno 官方 auto 常量表），
所以第 37 行 `DefaultDirName={autopf}\{#AppName}` **不需要改**。
`{group}`、`{autodesktop}` 同样自动变成当前用户范围。

### 1.2 禁止沿用旧目录（新增，[Setup] 段）

```ini
; Inno 默认 UsePreviousAppDir=yes：会沿用旧安装（Program Files）作为默认目录，
; 非管理员模式写不进去 → 安装报权限错误。这里显式关掉。
UsePreviousAppDir=no
```

### 1.3 新增安装标记（[Registry] 段，整段新增）

```ini
[Registry]
; AppPaths 靠它判断“我是安装形态”，不依赖可写性启发式。
; uninsdeletevalue 保证卸载后标记消失，下次可正常当便携用。
Root: HKCU; Subkey: "Software\通讯测试工具"; ValueType: string; ValueName: "InstallForm"; ValueData: "per-user"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\通讯测试工具"; ValueType: string; ValueName: "InstallDir";  ValueData: "{app}";          Flags: uninsdeletevalue
```

对应的 GUID 常量（放在 `#define` 区，供 `[Code]` 用）：

```pascal
#define AppUninstallKey "{8E4C1F42-5B7A-4D2E-9C3A-6F1D2B0A7C55}_is1"
```

### 1.4 卸载清理改为条件删除（第 83–87 行）

```ini
; 改前
[UninstallDelete]
Type: filesandordirs; Name: "{app}\data"
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\exports"

; 改后
[UninstallDelete]
Type: files; Name: "{app}\install.flag"
```

真正的条件清理挪到 `[Code]`（见 1.6）。

### 1.5 安装时写文件标记（[Code] 新增）

```pascal
procedure CurStepChanged(CurStep: TSetupStep);
var
  FlagPath, Content: String;
begin
  if CurStep = ssPostInstall then
  begin
    FlagPath := ExpandConstant('{app}\install.flag');
    Content :=
      'form=per-user' + #13#10 +
      'version={#MyAppVersion}' + #13#10 +
      'installedAt=' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + #13#10 +
      'dir=' + ExpandConstant('{app}') + #13#10;
    SaveStringToFile(FlagPath, Content, False);
  end;
end;
```

### 1.6 升级兼容与卸载逻辑（[Code] 新增/替换）

```pascal
// ---- 旧“全局安装”检测：旧版装在 Program Files、注册在 HKLM ----
function LegacyAdminInstallExists(): Boolean;
var S: String;
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
              '建议：先卸载旧版本，再安装本版本。' + #13#10 +
              '配置会自动沿用（数据仍在 %LOCALAPPDATA%\通讯测试工具\data，无需迁移）。' + #13#10 + #13#10 +
              '是否继续安装？', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

// ---- 卸载：只有便携形态才清理程序目录里的运行期数据 ----
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if FileExists(ExpandConstant('{app}\portable.flag')) then
    begin
      DelTree(ExpandConstant('{app}\data'),    True, True, True);
      DelTree(ExpandConstant('{app}\logs'),    True, True, True);
      DelTree(ExpandConstant('{app}\exports'), True, True, True);
    end;
  end;
end;
```

> 原来的 `InitializeSetup`（`tasklist` 那段空检查）可以删除：`CloseApplications=yes` 已经处理进程占用。

### 1.7 头部注释（第 15–19 行）

把“安装到 Program Files 后程序目录不可写 → 回退”的描述改成：

```
; 安装形态（当前用户）：应用目录 {app} = %LOCALAPPDATA%\Programs\通讯测试工具；
; 用户数据：%LOCALAPPDATA%\通讯测试工具\data（由 install.flag 明确安装形态，不随目录可写性漂移）；
; 日志/导出：%LOCALAPPDATA%\通讯测试工具\{logs,exports}；卸载保留，不删除。
```

---

## 2. `src/Comm.Diagnostics/AppPaths.cs`（5 处）

### 2.1 新增形态判定（第 15 行附近）

```csharp
public const string InstallFlagName = "install.flag";

/// <summary>安装标记文件（由安装包写入）。</summary>
public static bool HasInstallFlag => SafeExists(Path.Combine(BaseDirectory, InstallFlagName));

/// <summary>注册表安装标记（双保险：文件被误删也能识别）。</summary>
private static bool HasInstallRegistryMark()
{
    if (!OperatingSystem.IsWindows()) return false;
    try
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\通讯测试工具");
        var dir = key?.GetValue("InstallDir") as string;
        return !string.IsNullOrEmpty(dir) &&
               string.Equals(Path.GetFullPath(dir), Path.GetFullPath(BaseDirectory).TrimEnd('\\'),
                             StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception) { return false; }
}

/// <summary>是否安装形态（文件标记或注册表标记，任一命中）。</summary>
public static bool IsInstalledForm => HasInstallFlag || HasInstallRegistryMark();
```

> 注册表标记比对 `InstallDir` 而不是“存在即真”，避免同一台机器上的便携副本被误判。

### 2.2 重写 `Resolve()`（替换第 50–67 行）

```csharp
private static DataDirectoryInfo Resolve()
{
    // ① 显式便携声明（portable.flag）最高优先：高级用户可强制便携
    if (HasPortableFlag && TryEnsureWritable(PortableDataDirectory))
        return new DataDirectoryInfo(PortableDataDirectory, DataForm.Portable, null);

    // ② 安装形态：固定在用户目录，与历史版本路径一致（零迁移）
    if (IsInstalledForm && TryEnsureWritable(UserDataDirectory))
        return new DataDirectoryInfo(UserDataDirectory, DataForm.Installed, null);

    // ③ 便携兜底
    if (TryEnsureWritable(PortableDataDirectory))
        return new DataDirectoryInfo(PortableDataDirectory, DataForm.Portable, null,
            FallbackReason: HasPortableFlag ? "便携标记存在但程序目录不可写" : null);

    // ④ 用户目录兜底
    if (TryEnsureWritable(UserDataDirectory))
        return new DataDirectoryInfo(UserDataDirectory, DataForm.Installed,
            FallbackReason: $"程序目录不可写（{PortableDataDirectory}），数据已回退到用户目录；" +
                            "如需便携使用，请把程序解压到可写目录（可放置 portable.flag 明确声明）。");

    // ⑤ 临时目录
    return new DataDirectoryInfo(Path.Combine(Path.GetTempPath(), ProductFolderName, "data"),
        DataForm.Temporary, FallbackReason: "程序目录与用户目录均不可写，数据已回退到系统临时目录；设置可能不会长期保留。");
}
```

### 2.3 日志 / 导出目录按形态（替换第 32–34 行）

```csharp
private static string UserRoot => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);

/// <summary>安装形态下 logs/exports 也放用户目录，卸载不会误删。</summary>
public static string LogsDirectory =>
    Info.Form == DataForm.Installed ? Path.Combine(UserRoot, "logs") : Path.Combine(BaseDirectory, "logs");

public static string ExportsDirectory =>
    Info.Form == DataForm.Installed ? Path.Combine(UserRoot, "exports") : Path.Combine(BaseDirectory, "exports");
```

### 2.4 `DataDirectoryInfo` 增加形态字段（第 128 行）

```csharp
public enum DataForm { Portable, Installed, Temporary }

public sealed record DataDirectoryInfo(string Directory, DataForm Form, string? FallbackReason)
{
    public bool IsPortable => Form == DataForm.Portable;
}
```

> 这会破坏现有 `new DataDirectoryInfo(dir, IsPortable: true, ...)` 的调用点；
> 全仓只有 `Resolve()` 一处构造，改完即可（`Info.IsPortable` 的读取方不用动）。

### 2.5 展示文本与环境自检（第 47–48、146–147、194 行）

```csharp
public static string ToDisplayText() =>
    $"{DataDirectory}（{Info.Form switch {
        DataForm.Portable => "便携：程序同级 data",
        DataForm.Installed => "安装：用户目录 data",
        _ => "临时目录 data" }}）";
```

`EnvironmentReport.Build` 增加两行：

```csharp
sb.AppendLine($"安装形态：{(AppPaths.IsInstalledForm ? "安装版（per-user）" : "便携版")}；install.flag：{(AppPaths.HasInstallFlag ? "存在" : "不存在")}");
sb.AppendLine($"日志目录：{AppPaths.LogsDirectory}；导出目录：{AppPaths.ExportsDirectory}");
```

---

## 3. `src/Comm.Diagnostics/LogGate.cs`

**无需改动**。`ResolveDirectory` 的候选顺序是 `配置 > AppPaths.LogsDirectory > 用户 logs > temp`，
而 `AppPaths.LogsDirectory` 已经按形态返回正确路径（第 169 行）。

> 唯一要做的：把 165–166 行注释更新为“安装形态下 logs 直接在用户目录”。

---

## 4. `src/Comm.App.Wpf/ViewModels/SettingsViewModel.cs`（1 处）

`DirectoryPolicyText`（第 191–206 行）文案改为三形态说明：

```csharp
var lines = new List<string>
{
    $"当前形态：{(AppPaths.IsInstalledForm ? "安装版（per-user）" : "便携版")}",
    $"实际数据目录：{info.Directory}",
    $"便携目录：{AppPaths.PortableDataDirectory}（portable.flag：{(AppPaths.HasPortableFlag ? "存在" : "不存在")}）",
    $"安装标记：install.flag {(AppPaths.HasInstallFlag ? "存在" : "不存在")}",
    $"用户目录：{AppPaths.UserDataDirectory}",
    "策略：portable.flag → 便携 data/；安装标记 → 用户目录 data；其余按可写性回退。"
};
```

`MigrateToPortable` / `MigrateToUserData` 两个命令保持不变（迁移函数仍然有用）；
建议在安装形态下给“创建便携标记”按钮加一句提示：需要在程序目录可写时才生效。

---

## 5. 文档与注释同步（3 处）

| 文件 | 改动 |
|---|---|
| `tools/installer.iss` 头部 | 见 1.7 |
| `docs/build-environment.md` | 若其中写了“安装到 Program Files / 程序目录不可写”，同步为 per-user 描述 |
| `docs/用户手册.md` | 安装位置从 `C:\Program Files\通讯测试工具` 改为 `%LOCALAPPDATA%\Programs\通讯测试工具` |

---

## 6. 验收清单

| # | 场景 | 期望 |
|---|---|---|
| 1 | 全新机器、标准用户、双击安装包 | **无 UAC**；装到 `%LOCALAPPDATA%\Programs\通讯测试工具`；`install.flag` 存在；`data\` **不在**程序目录 |
| 2 | 同上，启动后看设置页 | 数据目录 = `%LOCALAPPDATA%\通讯测试工具\data`；形态显示“安装版” |
| 3 | 旧 admin 版用户升级 | 安装时弹出“检测到旧全局安装”提示；卸载旧版 → 装新版 → **配置仍在** |
| 4 | 便携 zip 解压运行 | 数据进 `{app}\data`，行为与今天完全一致 |
| 5 | 便携目录里放 `portable.flag` 后再装 per-user | 仍按便携（① 优先于 ②） |
| 6 | 删除 `install.flag` 后启动 | 仍识别为安装形态（注册表兜底） |
| 7 | 卸载（安装形态） | 程序目录清空，`%LOCALAPPDATA%\通讯测试工具\data` **保留** |
| 8 | 卸载（便携形态，含 `portable.flag`） | `{app}\data|logs|exports` 被清理 |
| 9 | 多用户：A 装完，B 登录 | B 看不到 A 的安装（符合 per-user 语义）；需要时 B 自行安装 |
| 10 | 功能冒烟 | 串口、扫描、手工发送、网卡配置（UAC 助手按需提权）、导出、日志、自动更新替换 |

---

## 7. 风险与回滚

| 风险 | 缓解 |
|---|---|
| 用户手删 `install.flag` → 被误判便携 | 注册表标记兜底（2.1）；设置页显示形态便于排查 |
| 便携副本放在安装目录旁 → 被注册表标记影响 | 注册表比对 `InstallDir` 与 `BaseDirectory`，不一致则不算安装形态 |
| 旧版与新版的“应用和功能”双条目 | 安装前提示卸载旧版（1.6）；文档说明 |
| 回滚到旧安装包 | 旧版 admin 装到 Program Files（不可写）→ 自动回退用户目录，**新旧版数据目录相同**，配置可读 |
| 共享工位多用户 | 每个账号各装一份（约 233 MB/人）；如不可接受，保留“全局安装”作为第二产物（见决策点 C） |

---

## 8. 工作量与实施顺序

| 步骤 | 内容 | 预估 |
|---|---|---|
| 1 | `AppPaths` 形态判定 + 目录解析 + 单测（便携/安装/回退三态） | 2–3 h |
| 2 | `SettingsViewModel` 文案 + `EnvironmentReport` 增补 | 1 h |
| 3 | `installer.iss` 六处改造（含迁移提示、条件卸载） | 1.5–2 h |
| 4 | 出包 + 按第 6 节验收（重点 1/3/4/7） | 2–3 h |
| 5 | 文档同步 + CHANGELOG | 0.5 h |
| 合计 | | **约 1 人日** |

建议顺序：**先 1（代码）→ 4 的便携/安装三态验证 → 再 3（脚本）**。
代码先落地时，旧安装包仍能工作（没有 `install.flag` 时行为不变）。

---

## 9. 需要你确认的决策点

| 编号 | 问题 | 建议 |
|---|---|---|
| **A** | 卸载时是否保留 `data/logs/exports`？ | **保留**（安装形态），仅便携形态清理 |
| **B** | 是否引入 HKCU 注册表标记做双保险？ | **是**（成本 10 行，避免误判） |
| **C** | 是否保留“全局安装（Program Files）”作为可选产物？ | 若有多人共享工位诉求 → 保留；否则**只出 per-user 一种**，减少分支 |

---

## 10. 明确不在本方案范围内

- SmartScreen / “无法验证发布者” → 需要签名，见 [代码签名计划](code-signing-plan.md)；
- `Comm.NetConfigHelper` 的按需 UAC → 设计如此，不变；
- 自动更新 → 改用 per-user 后**更简单**（用户态替换文件即可），但更新逻辑本轮不动。

---

## 11. 实施记录（2026-10-19）

### 11.1 已落地

| 文件 | 实际改动 |
|---|---|
| `tools\installer.iss` | `PrivilegesRequired=lowest`；删除 `PrivilegesRequiredOverridesAllowed`；新增 `UsePreviousAppDir=no`；`[UninstallDelete]` 只删 `install.flag`；`[Code]` 新增 `LegacyAdminInstallExists` / `InitializeSetup` 提示 / `CurStepChanged` 写标记 / `CurUninstallStepChanged` 条件清理 |
| `src\Comm.Diagnostics\AppPaths.cs` | 新增 `InstallFlagName`、`HasInstallFlag`、`IsInstalledForm`（文件→注册表→unins000.exe 三重判定）、`UserRootDirectory`；`Resolve()` 改为四段优先级；`LogsDirectory`/`ExportsDirectory` 按形态；`DataForm` 枚举 + `DataDirectoryInfo(…, DataForm, …)`；`ToDisplayText` 与环境自检增补 |
| `src\Comm.App.Wpf\ViewModels\SettingsViewModel.cs` | 目录策略文案改为“形态 + 三种目录 + 策略” |
| `src\Comm.Diagnostics\LogGate.cs` | 仅注释 |
| `docs\build-environment.md` | 4 节数据目录策略、4.3 安装包说明、4.4 `/CURRENTUSER` 说明、验证方法 |

### 11.2 验证结果（本机沙箱内）

| 项 | 方法 | 结果 |
|---|---|---|
| 编译 | `Comm.Diagnostics` / `Comm.App.Wpf` / solution | 0 错误 |
| 安装包编译 | `ISCC.exe /DMyAppVersion=0.4.1 installer.iss` | 成功，72.1 MB → `artifacts\installer\通讯测试工具-0.4.1-安装包.exe` |
| per-user 生效 | 安装日志 | `Administrative install mode: No`、`Install mode root key: HKEY_CURRENT_USER` ✔ |
| 便携判定 | 探针（无标记） | Form=Portable，数据在程序目录 ✔ |
| 安装标记判定 | 探针 + `install.flag` | `IsInstalledForm=True` ✔（后续写用户目录被沙箱拒绝，按设计回退并给出原因 ✔） |
| 显式便携优先 | `install.flag` + `portable.flag` | Form=Portable ✔ |
| unins000 兜底 | 探针 + 仅 `unins000.exe` | `IsInstalledForm=True` ✔ |
| 注册表兜底 | 写 HKCU | **沙箱禁止写 HKCU**，未能验证（需真机） |
| 端到端安装 | `/VERYSILENT /DIR=…` | **沙箱禁止写 HKCU**，Inno 在“Saving uninstall information”处必然失败并回滚（日志已取证），无法在本会话完成 |

### 11.3 与原清单的偏差（实测驱动）

1. **注册表标记从 `[Registry]` 段改到 `[Code]` 的 try/except 写入**。原因：`[Registry]` 写失败会让 Inno **整包回滚**（沙箱实测，退出码 4/5）。标记只是保险，不能成为安装的硬依赖。
2. **卸载清理注册表同样 try/except**，避免受限环境下卸载失败。
3. **新增第三重判定 `unins000.exe`**：无需任何权限、比注册表更可靠，作为 `install.flag` 被误删时的首选兜底。

### 11.4 仍需在真机确认

- 第 6 节第 1、3、7 项（全新 per-user 安装 / 旧 admin 版升级提示 / 卸载保留数据）；
- 注册表标记写入与 `unins000.exe` 之外的兜底组合；
- 升级到新版后设置页显示“安装版（per-user）”、数据目录为 `%LOCALAPPDATA%\通讯测试工具\data`。
