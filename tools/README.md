# 消除 Windows 告警的工具与说明

针对两个弹窗：

- **“打开文件 - 安全警告：无法验证发布者”** —— 附件管理器（Attachment Manager），由文件的
  MOTW（Zone.Identifier）触发；
- **“Windows 已保护你的电脑 / Microsoft Defender SmartScreen 阻止了无法识别的应用启动”** ——
  SmartScreen 应用信誉检查，同样由 MOTW 触发，再叠加“未签名 + 无下载信誉”。

> 结论：**这两个框都不是因为“你代码有问题”，而是因为文件带 Internet 区域标记且没有签名。**
> 编译产物本来不带标记，是“打包 → 传输 → 下载 → 解压”这条链路把它写回去的。

## 一、先确认当前文件到底有没有标记

```powershell
# 有输出 = 带标记，运行时会弹框
Get-Item .\CommTestTool.exe -Stream Zone.Identifier

# 看标记内容（ZoneId=3 表示 Internet 区域，1 表示本地 Intranet）
Get-Content .\CommTestTool.exe -Stream Zone.Identifier

# 看签名状态
Get-AuthenticodeSignature .\CommTestTool.exe | Format-List Status, StatusMessage, SignerCertificate
```

## 二、本机/内网：清掉标记（最快、最彻底）

```powershell
# 单个文件（等价于 属性 → 常规 → 解除锁定）
Unblock-File .\CommTestTool.exe

# 整个目录（递归）
.\tools\Unblock-Build.ps1 -Path .\src\Comm.App.Wpf\bin

# 或者干脆重新编译，新文件天然没有标记
C:\Users\Bass\.dotnet\dotnet.exe build CommTestTool.sln
```

> 如果提示“因为在此系统上禁止运行脚本”，用：
> `powershell -ExecutionPolicy Bypass -File .\tools\Unblock-Build.ps1 -Path .\artifacts`

**注意**：脚本只管本机。文件一旦再进 zip、网盘、微信、邮件，标记就会回来，
别人机器上你也没机会先跑 Unblock。

## 三、本机开发：自签名 + 信任（让“发行商”显示成你自己）

```powershell
# 1) 创建自签名代码签名证书，并装进 当前用户\受信任的根/发布者（不需要管理员）
.\tools\New-DevCodeSigningCert.ps1

# 2) 给产物签名
.\tools\Sign-App.ps1 -Path .\src\Comm.App.Wpf\bin\Debug\net8.0-windows\CommTestTool.exe

# 3) 复验：Status 应为 Valid，SignerCertificate.Subject 是你设的 CN
Get-AuthenticodeSignature .\src\Comm.App.Wpf\bin\Debug\net8.0-windows\CommTestTool.exe | Format-List
```

效果：警告框里“发行商”不再是“未知发布者”；本机信任链完整，杀软/SmartScreen 判定更友好。

**边界必须讲清楚**：

- 自签名证书**只在你安装了该根证书的机器上有效**。发给别人仍然是“未知发布者”，
  所以它解决不了“发给同事/客户”的问题；
- 自签名**不能**让“打开文件 - 安全警告”框消失——那个框是“区域”判断，不看签名。
  让它消失只能靠“去掉 MOTW”（本机）或“用安装包安装”（标记写在下载的安装包上，装出来的文件没有标记）。

## 四、对外分发：正规做法是代码签名 + 安装包

| 方案 | 成本 | SmartScreen 效果 |
|---|---|---|
| 自签名（本机/域内分发根证书） | 免费 | 仅受信任机器有效 |
| OV 代码签名证书（需 HSM/token） | 约 ¥1500–3000/年 | 需累积下载信誉，初期可能仍弹 |
| EV 代码签名证书（需 token） | 约 ¥2500–5000/年 | 立即获得信誉 |
| Azure Trusted Signing | $9.99/月，需组织 3 年可验证历史 | CI 内直接签，性价比最好 |

签名命令（有证书文件时；`/tr` 时间戳必须带，否则证书过期后签名失效）：

```powershell
signtool sign /fd sha256 /td sha256 /tr http://timestamp.digicert.com `
  /f mycert.pfx /p 密码 /d "通讯测试工具" CommTestTool.exe
signtool verify /pa /v CommTestTool.exe
```

本仓库没有装 Windows SDK，所以 `tools\Sign-App.ps1` 用 PowerShell 自带的
`Set-AuthenticodeSignature` 实现（参数 `-TimestampServer` 等价于 `/tr`）。
装了 SDK 之后可以把脚本切换成 signtool，功能一致。

配套动作：

1. **用安装包分发**（Inno Setup / WiX / MSIX），安装包本身要签名。
   安装出来的文件不带 MOTW，用户不会看到“打开文件 - 安全警告”。
2. **提交误报/信誉**：<https://www.microsoft.com/en-us/wdsi/filesubmission>
3. 保持同一发布者名、同一文件名、同一发布地址，信誉是累积的。

## 五、发布流程（推荐直接用它）

```powershell
# 发布到 artifacts\publish，清标记，可选签名，可选打 zip
.\tools\Publish-App.ps1 -Sign

# 自包含 + 签名 + 打包
.\tools\Publish-App.ps1 -SelfContained -Sign -Zip -TimestampServer http://timestamp.digicert.com
```

产物只从 `artifacts\publish` 出，不要把 `bin\Debug` 直接压缩发人：
那里有 pdb、有 obj 残留、有 `data/` 运行期文件，也最容易被搬来搬去染上标记。

## 六、自检清单

- [ ] 发布的 exe 是 `artifacts\publish` 出来的，不是 `bin\Debug` 直接拷的
- [ ] 发布目录里没有 `*.pdb`、`data/`、`logs/`
- [ ] `Get-AuthenticodeSignature` 显示 `Valid`（对外发布时）
- [ ] `Get-Item x.exe -Stream Zone.Identifier` 无输出
- [ ] 从 zip 解压到干净目录后，双击运行不弹框（把整条链路走一遍）
- [ ] 文件名不含 `setup`/`install`/`update`/`patch` 等关键字

---

## 七、本目录脚本一览（M2.5 更新）

| 脚本 | 用途 | 备注 |
|---|---|---|
| `Publish-App.ps1` | 发布产物：`dotnet publish` → 清 MOTW →（可选）签名 →（可选）打 zip | **默认 Release + 自包含 win-x64**；`-FrameworkDependent` 出小包；`-Sign` 会对本项目 11 个自有 exe/dll 全部签名；自动附带 `便携包说明.txt` 与 `解除锁定-便携包.ps1` |
| `Sign-App.ps1` | Authenticode 签名（SHA-256 + RFC3161 时间戳） | 不指定 `-Thumbprint` 时自动取当前用户“我的”里有效期最长的代码签名证书 |
| `New-DevCodeSigningCert.ps1` | 创建开发自签名证书并装入受信任存储 | 仅本机/域内有效；`-PfxPath` 可导出 PFX（**切勿提交进仓库**） |
| `New-AppIcon.ps1` | 生成 `src\Comm.App.Wpf\app.ico`（16/24/32/48/64/128/256） | 已有设计稿时直接替换 `app.ico`，不要再运行 |
| `Unblock-Build.ps1` | 清除 MOTW（等价于“属性 → 解除锁定”） | 只对本机有效；对外分发请用签名 |
| `Invoke-HilProbe.ps1` | 真实硬件回归：调 `tools\Comm.HilProbe` 遍历波特率/校验位 | 只发 `03` 只读请求；报告回填 `docs\hardware-compatibility-matrix-v1.md` |

### 发布与签名（M2.5 / R-20）

```powershell
# 仅发布（默认自包含）
.\Publish-App.ps1

# 发布 + 签名 + 便携包
.\Publish-App.ps1 -Sign -Zip -TimestampServer http://timestamp.digicert.com

# 真实硬件回归
.\Invoke-HilProbe.ps1 -Port COM5 -Unit 5 -Baud 9600,19200 -Parity N,E
```

> `param()` 默认值里 `$PSScriptRoot` 在部分调用方式下为空，因此 `-OutputPath` 类参数的默认值
> 在脚本正文中解析（脚本已处理，无需手工传参）。
