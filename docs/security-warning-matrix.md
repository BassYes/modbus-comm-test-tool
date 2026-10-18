# 安全警告复现与验证矩阵（R-20，规划 22.5.4）

> 状态：**模板已交付，实测待执行**（需要干净 Win10 22H2 / Win11 虚拟机 + 可用的签名证书）。
> 规划 22.5.1 已确认根因（未签名）并关闭 Q-3，因此本矩阵**不再用于定位原因**，而作为“签名后各分发渠道表现”的回归手段。

---

## 1. 判定口径

| 记录项 | 取值 |
|---|---|
| **打开文件 – 安全警告** | 未出现 / 出现（显示“**未知发布者**”）/ 出现（显示“**已验证的发布者：xxx**”） |
| **SmartScreen** | 未出现 / 出现“Windows 已保护你的电脑”（有“仍要运行”）/ 出现且无“仍要运行” |
| **UAC（仅提权程序，M3a）** | 不适用 / 黄框“未知发布者” / 蓝框“已验证的发布者” |
| MOTW | 运行前用 `Get-Item <file> -Stream Zone.Identifier` 确认：有 / 无 |
| 签名状态 | `Get-AuthenticodeSignature <file> | Select Status`：Valid / NotSigned / UnknownError / HashMismatch |

> 关键前提：**“打开文件 – 安全警告”由 MOTW 触发**，与签名无关的“区域”判断不会因签名而消失；签名改变的是发布者显示与 SmartScreen 信誉。因此：
> - **安装包渠道**（安装出的文件不带 MOTW）→ 目标是“从获得文件到启动**不出现**任何提示”；
> - **便携包渠道** → 按说明解除锁定后不出现；未解除锁定时最多出现“已验证的发布者”。

---

## 2. 测试环境

| 项 | 环境 A | 环境 B |
|---|---|---|
| 操作系统 | Windows 10 22H2 x64（干净，未装 .NET） | Windows 11（干净，未装 .NET） |
| 是否已加入域 | | |
| 是否装有第三方杀软 | 仅 Defender | 仅 Defender |
| 系统缩放 | 100% | 100% |
| 被测产物 | `CommTestTool-<版本>-win-x64.zip`（签名便携包）+ 签名安装包（M4） | 同左 |

---

## 3. 渠道矩阵（每个单元格填判定结果 + 截图文件名）

| # | 渠道 / 步骤 | 标准用户（环境 A） | 管理员（环境 A） | 标准用户（环境 B） | 管理员（环境 B） |
|---|---|---|---|---|---|
| 1 | 浏览器下载 zip → 资源管理器解压 → 运行 exe | | | | |
| 2 | 浏览器下载 zip → **在 zip 预览窗口内直接运行** exe | | | | |
| 3 | U 盘拷贝（作为基准对照，无 MOTW） | | | | |
| 4 | UNC 共享（用 IP 访问，如 `\\10.0.0.5\share`） | | | | |
| 5 | UNC 共享（用主机名访问） | | | | |
| 6 | 签名安装包安装后运行 | | | | |
| 7 | 便携包按说明执行 `解除锁定-便携包.ps1` 后运行 | | | | |
| 8 | 右键 zip → 属性 → 勾选“解除锁定”后解压运行 | | | | |

**截图命名建议**：`docs/security-matrix/<渠道号>-<envA|envB>-<user|admin>.png`

---

## 4. 待执行命令

```powershell
# 0) 发布并签名（需已有可用证书）
.\tools\Publish-App.ps1 -Sign -Zip -TimestampServer http://timestamp.digicert.com

# 1) 对每个被测文件确认签名与 MOTW
$f = '.\artifacts\publish\CommTestTool.exe'
Get-AuthenticodeSignature -LiteralPath $f | Format-List Status, StatusMessage, SignerCertificate
Get-Item -LiteralPath $f -Stream Zone.Identifier -ErrorAction SilentlyContinue

# 2) 若无 SDK 的 signtool，可用 PowerShell 等价命令
signtool verify /pa /v $f
```

---

## 5. 预期结论（供核对，不是实测结果）

| 渠道 | 已签名后的预期 |
|---|---|
| 1（浏览器下载解压） | 便携 exe 仍可能弹“打开文件 – 安全警告”，但发布者显示为**已验证**；安装包安装后不弹 |
| 2（zip 预览内运行） | 最易触发提示；应显示“已验证的发布者” |
| 3（U 盘） | 无 MOTW → 不弹 |
| 4/5（UNC） | 属“本地 Intranet/Internet”区域，可能弹；应显示“已验证的发布者” |
| 6（安装包后运行） | **不弹**（安装出的文件无 MOTW） |
| 7/8（解除锁定后） | **不弹** |

> 若实测结果与预期不符（例如已签名但显示“未知发布者”），优先检查：签名是否用 `signtool verify /pa` 通过、是否带时间戳、是否被再次压缩/改写（改写会破坏签名）。

---

## 6. 结论

- 是否有渠道在签名后仍出现“**未知发布者**”：**有 / 无**；如是，列出渠道与截图：
- SmartScreen 表现：
- 需要在手册中说明的限制（原文）：
- 需要修订规划 22.5 的项：
