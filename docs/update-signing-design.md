# 通讯测试工具：更新签名与发布流程设计（评估稿）

> 状态：评估 / 设计稿，尚未实现。  
> 参考：DengShell 的“Ed25519 更新清单签名 + GitHub Release 分发 + 客户端校验 + 原子替换”模式。  
> 目标平台：Windows x64（当前 WPF 自包含发布）。  
> 结论先行：**不购买 Authenticode 证书也能做安全更新**，但只能保证“更新包完整性/来源”，**不能消除 SmartScreen / 未知发布者警告**。

---

## 1. 背景

当前项目发布方式：

- `tools/Publish-App.ps1` 生成自包含 Release；
- 可选 Inno Setup 安装包 / 便携 zip；
- 无软件内自动更新；
- 无正式代码签名证书。

DengShell 的做法：

- Windows 二进制不使用 Authenticode；
- 使用独立的 **Ed25519 更新清单签名**；
- 更新清单包含版本、构建号、大小、SHA-256、有效期、平台、下载地址；
- 客户端校验清单签名、有效期、大小、哈希、构建号；
- 下载后在原程序目录原子替换 EXE；
- 失败时尝试恢复旧版本。

本设计将该模式适配到本项目，但保持更保守的安全边界。

---

## 2. 目标

1. 支持 Windows x64 软件内更新检查；
2. 更新包来源可信（固定 GitHub Release 域名）；
3. 更新包完整性可验证（Ed25519 + SHA-256 + size + build number + expiry）；
4. 替换过程可回滚；
5. 不引入私钥到源码/发布包；
6. 与现有 `Publish-App.ps1`、`AppPaths`、设置系统兼容。

---

## 3. 非目标

1. 不解决 SmartScreen / “未知发布者”；
2. 不替代 Authenticode；
3. 不做 macOS/Linux 更新（当前仅 Windows）；
4. 不做增量更新（先做整包替换）；
5. 不做后台静默更新（先做“检查 -> 提示 -> 用户确认 -> 下载 -> 替换 -> 重启”）；
6. 不在客户端执行任意下载地址或安装命令。

---

## 4. 与 DengShell 的取舍

| 项目 | DengShell | 本项目建议 |
|---|---|---|
| Windows 代码签名 | 无 | 无（先评估） |
| 更新签名算法 | Ed25519 | Ed25519 |
| 更新包 | 安装包/绿色包 | 自包含目录 zip 或安装包 |
| 替换方式 | 独立助手等待旧进程退出 | 独立 helper / 重启后替换 |
| macOS | ad-hoc，未公证 | 暂不支持 |
| Android | debug 签名 | 不适用 |
| 信任模型 | 内置公钥 | 内置公钥 |
| 安全边界 | 固定域名、防回退、有效期 | 同样，且更严格地拒绝任意命令 |

---

## 5. 总体架构

```text
App.Wpf (UI)
  -> UpdateService
       -> ManifestFetcher (HTTPS fixed host)
       -> ManifestVerifier (Ed25519 / expiry / version / build / size / sha256)
       -> UpdateDownloader (fixed URL, progress, cancel, max size, timeout)
       -> PackageVerifier (zip hash / PE check)
       -> UpdateApplier (helper process / atomic replace / backup / rollback)
       -> UpdateStateStore (settings + local state)
```

建议新增组件：

- `Comm.Updates` 或 `Comm.App.Wpf/Services/Update/*`
- `Comm.UpdateSigner`（发布端签名工具，控制台项目）
- `docs/update-manifest.example.json`

---

## 6. 更新清单 Schema（v1）

字段建议：

```json
{
  "schema": 1,
  "product": "CommTestTool",
  "platform": "windows-x64",
  "version": "0.5.0",
  "build": 12345,
  "issuedAt": "2026-10-20T00:00:00Z",
  "expiresAt": "2027-01-20T00:00:00Z",
  "fileName": "CommTestTool-0.5.0-win-x64.zip",
  "downloadUrl": "https://github.com/<owner>/<repo>/releases/download/v0.5.0/CommTestTool-0.5.0-win-x64.zip",
  "size": 103809024,
  "sha256": "....",
  "executableSha256": "....",
  "notes": "更新说明",
  "signingKeyId": "commtool-update-2026",
  "signature": "base64(ed25519 signature)"
}
```

### 校验规则

1. `schema` 必须支持；
2. `product` 必须等于当前产品；
3. `platform` 必须等于当前平台；
4. `version` 必须高于当前版本（或 build 更高）；
5. `build` 必须高于当前 build；
6. `issuedAt <= now <= expiresAt`；
7. `expiresAt - issuedAt <= 180 天`；
8. `downloadUrl` host 必须在白名单；
9. `size` 必须 <= 最大允许值；
10. 下载后 SHA-256 必须匹配；
11. zip 内主 exe SHA-256 必须匹配（可选但推荐）；
12. Ed25519 签名必须使用内置公钥验证；
13. 任一失败：拒绝更新，记录诊断信息，不执行替换。

---

## 7. 安全模型

### 威胁

- 中间人篡改更新包；
- 旧版本回滚攻击；
- 恶意下载地址；
- 本地文件被替换；
- 更新包被杀软锁定；
- 用户在更新过程中关闭程序。

### 对策

1. **固定域名**：只允许 `github.com/<owner>/<repo>/releases/...` 或自定义固定 HTTPS 域名；
2. **不执行任意命令**：更新器只做文件替换；
3. **防回退**：build/version 必须更高；
4. **有效期**：清单过期即停止提供更新；
5. **大小限制**：防止下载巨大文件；
6. **哈希校验**：SHA-256；
7. **签名验证**：Ed25519；
8. **原子替换**：下载到临时目录，校验通过后替换；
9. **备份**：替换前备份旧 exe；
10. **回滚**：失败时恢复旧 exe；
11. **目录可写性检查**：不可写时提示手动安装；
12. **不覆盖用户数据**：只替换程序文件，保留 `data/`。

---

## 8. 密钥管理

### 发布端

- 使用 `dotnet` 或 Go/Node 工具生成 Ed25519 密钥对；
- 私钥保存在发布者机器安全位置；
- 权限：目录 `0700`，文件 `0600`；
- 不提交到 Git；
- 不放在项目/发布/网站目录；
- 离线备份；
- 丢失私钥 = 失去为已部署客户端签发后续更新的能力。

### 客户端

- 公钥编译进程序；
- 支持 `signingKeyId`，便于未来轮换；
- 轮换策略：新版本可内置新公钥，旧公钥保留一段时间。

### 建议新增文件

- `docs/update-manifest.example.json`
- `tools/Comm.UpdateSigner/`（评估后实现）
- `src/Comm.App.Wpf/Services/Update/*`（实现阶段）

---

## 9. Windows 替换策略

### 方案 A：独立 helper（推荐）

1. 主程序下载并校验更新包；
2. 启动 `Comm.UpdateHelper.exe`；
3. 主程序退出；
4. helper 等待主进程退出；
5. 备份旧 exe；
6. 解压/替换；
7. 启动新 exe；
8. 清理临时文件。

### 方案 B：重启后替换

1. 主程序把更新包保存到 `%TEMP%/CommTestTool-update`；
2. 主程序退出；
3. 启动器/批处理等待进程退出后替换；
4. 启动新 exe。

### 建议

- 优先方案 A，更可靠；
- helper 必须是已签名或至少与主程序同目录；
- 更新目录必须可写，否则提示手动安装。

---

## 10. UI / UX

1. 设置页增加“检查更新”；
2. 启动时可选自动检查（默认关闭或仅提示）；
3. 显示：
   - 当前版本 / 最新版本；
   - 更新说明；
   - 文件大小；
   - 校验状态；
4. 用户确认后下载，显示进度；
5. 下载完成后提示重启；
6. 失败时显示原因与手动下载链接；
7. 不自动重启，避免丢失现场状态。

---

## 11. 发布流程（未来）

```text
1. 更新版本号（Directory.Build.props）
2. dotnet publish
3. 生成 zip / installer
4. 计算 SHA-256 / size
5. 生成 manifest
6. 使用离线私钥签名 manifest
7. 校验 manifest
8. 上传到 GitHub Release
9. 在客户端测试更新
10. 记录发布校验结果
```

建议在 `tools/Publish-App.ps1` 后新增：

- `tools/Sign-UpdateManifest.ps1`
- `tools/Verify-UpdateManifest.ps1`

---

## 12. 测试计划

### 单元测试

- manifest schema 解析；
- 版本/build 比较；
- 有效期；
- URL 白名单；
- SHA-256；
- Ed25519 验签；
- 防回退。

### 集成测试

- 下载取消；
- 哈希不匹配；
- 签名不匹配；
- 过期清单；
- 旧版本回滚；
- 目录不可写；
- 进程退出后替换。

### 手动测试

- 从 v0.4.1 更新到 v0.4.2；
- 更新中断后重试；
- 杀软锁定文件；
- 用户取消。

---

## 13. 风险

1. SmartScreen 仍可能警告；
2. 自动更新不等于自动信任；
3. GitHub Release 可用性依赖网络；
4. 用户可能运行旧版本长期不更新；
5. 私钥管理不当会导致更新链路失效；
6. Windows 目录权限问题。

---

## 14. 里程碑

### M0：评估（当前）

- [x] 调研 DengShell；
- [x] 设计文档；
- [ ] 确认是否实施。

### M1：原型

- [ ] Ed25519 密钥生成；
- [ ] manifest 签名/验证工具；
- [ ] 示例 manifest；
- [ ] 单元测试。

### M2：客户端更新器

- [ ] UpdateService；
- [ ] 下载器；
- [ ] 校验器；
- [ ] helper 替换；
- [ ] UI。

### M3：发布集成

- [ ] Publish-App.ps1 集成；
- [ ] GitHub Actions；
- [ ] 端到端测试。

---

## 15. 当前建议

由于用户选择“先评估”，建议：

1. 先保留本文档；
2. 不修改主程序；
3. 下一步只实现 `Comm.UpdateSigner` 原型与 manifest 示例；
4. 在确认更新策略后，再进入客户端实现。

---

## 16. 参考

- DengShell `build/SIGNED-UPDATES.md`
- DengShell `build/ONLINE-UPDATE-DESIGN.md`
- DengShell `scripts/package-release.py`
- 本项目 `tools/Publish-App.ps1`
- 本项目 `docs/code-signing-plan.md`

---

## 17. 已实现的准备工具（原型）

- `tools/Comm.UpdateSigner`：Ed25519 密钥生成、manifest 签名、manifest 验证。
- `tools/Sign-UpdateManifest.ps1` / `tools/Verify-UpdateManifest.ps1`：PowerShell 包装。
- `src/Comm.App.Wpf/Services/Update/*`：更新服务契约与占位实现。
- `docs/samples/update-manifest.example.json`：更新清单示例。
- `docs/github-actions-release-draft.md`：GitHub Actions 发布流程草稿。

### 原型验证命令

```powershell
# 生成密钥（仅评估，密钥不要提交）
powershell -ExecutionPolicy Bypass -File tools/Sign-UpdateManifest.ps1 ...
```

实际命令：

```powershell
dotnet run --project tools/Comm.UpdateSigner -- keygen --out artifacts/updtest
dotnet run --project tools/Comm.UpdateSigner -- sign --manifest docs/samples/update-manifest.example.json --key artifacts/updtest/update-signing-ed25519.seed --out artifacts/updtest/signed.json
dotnet run --project tools/Comm.UpdateSigner -- verify --manifest artifacts/updtest/signed.json --pubkey artifacts/updtest/update-signing-ed25519.pub
```
