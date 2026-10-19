# GitHub Actions 发布流程草稿（评估，未启用）

> 说明：此文件是草稿，尚未放到 `.github/workflows/`。确认更新策略后再启用。

```yaml
name: Release (draft)

on:
  push:
    tags:
      - 'v*'
  workflow_dispatch:

jobs:
  build-windows:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Build and publish
        shell: pwsh
        run: |
          .\tools\Publish-App.ps1
          # TODO: create zip artifact with stable name

      - name: Generate update manifest
        shell: pwsh
        run: |
          # TODO: compute version/build/size/sha256 and generate manifest.json

      - name: Sign update manifest
        shell: pwsh
        env:
          UPDATE_SIGNING_KEY: ${{ secrets.UPDATE_SIGNING_KEY_BASE64 }}
        run: |
          # TODO: decode secret to a temporary file outside repo, then:
          # .\tools\Sign-UpdateManifest.ps1 -Manifest manifest.json -Key <temp-key> -Out manifest.signed.json

      - name: Verify update manifest
        shell: pwsh
        run: |
          # TODO: .\tools\Verify-UpdateManifest.ps1 -Manifest manifest.signed.json -PublicKeyBase64 <embedded-public-key>

      - name: Upload release assets
        uses: softprops/action-gh-release@v2
        with:
          files: |
            artifacts/publish/**
            manifest.signed.json
```

## 必要 Secrets

- `UPDATE_SIGNING_KEY_BASE64`：Ed25519 私钥 seed 的 Base64，仅存放于 GitHub Secrets。
- 可选：代码签名证书（如果未来购买）。

## 安全要求

1. 私钥不得出现在仓库、发布包、日志中；
2. manifest 签名必须在 CI 中完成，私钥通过 Secret 注入；
3. 发布后必须验证 manifest；
4. 所有下载 URL 必须固定域名。
```

---

## SignPath.io OSS 签名（GitHub 开源发布）

如果启用 SignPath.io，Windows 二进制签名流程建议如下：

1. 构建未签名产物；
2. `actions/upload-artifact` 上传未签名 artifact；
3. `signpath/github-action-submit-signing-request@v3` 提交签名请求；
4. 等待 SignPath 审批与签名；
5. 下载签名后的 artifact；
6. 再生成/签名更新 manifest；
7. 上传 GitHub Release。

注意：
- 必须使用 GitHub-hosted runner；
- 每次发布需要 SignPath 人工审批；
- 证书 publisher 为 SignPath Foundation；
- README 中需包含 SignPath 声明。
