<#
.SYNOPSIS
    给 exe/dll/ps1 打 Authenticode 签名（默认用本机的开发自签名证书）。

.DESCRIPTION
    “发行商：未知发布者”只是症状。签名解决的是“发行人可验证”这件事：
      - 本机：配合 New-DevCodeSigningCert.ps1 的受信任安装，校验结果为 Valid；
      - 别人机器：自签名仍然会被判为未知发布者，必须用正规 CA 签发的证书。

    正规签名的流程、费用和选型见 tools\README.md。
    用 signtool 时要带时间戳（/tr），否则证书过期后签名会失效；
    本脚本用 -TimestampServer 参数实现同样效果。

.PARAMETER Path
    要签名的文件，可多个，支持通配。

.PARAMETER Thumbprint
    证书指纹。省略时自动取当前用户“我的”存储中有效期最长的代码签名证书。

.PARAMETER TimestampServer
    RFC3161 时间戳服务。默认不启用（开发自签名够用）；对外发布请指定，
    例如 http://timestamp.digicert.com

.EXAMPLE
    .\Sign-App.ps1 -Path ..\src\Comm.App.Wpf\bin\Debug\net8.0-windows\CommTestTool.exe

.EXAMPLE
    .\Sign-App.ps1 -Path ..\artifacts\publish\*.exe -TimestampServer http://timestamp.digicert.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]] $Path,

    [string] $Thumbprint,

    [string] $TimestampServer
)

$ErrorActionPreference = 'Stop'

# 选择证书
if ($Thumbprint) {
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "找不到指纹为 $Thumbprint 的证书（Cert:\CurrentUser\My）" }
} else {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        throw "当前用户存储里没有代码签名证书。请先运行 .\New-DevCodeSigningCert.ps1"
    }
    Write-Host "自动选用证书: $($cert.Subject)  到期 $($cert.NotAfter.ToString('yyyy-MM-dd'))" -ForegroundColor DarkGray
}

$files = foreach ($p in $Path) { Get-ChildItem -Path $p -File -ErrorAction SilentlyContinue }
if (-not $files -or @($files).Count -eq 0) { throw "没有匹配到要签名的文件: $($Path -join ', ')" }

$ok = 0; $bad = 0
foreach ($f in @($files)) {
    $params = @{
        FilePath      = $f.FullName
        Certificate   = $cert
        HashAlgorithm = 'SHA256'
    }
    if ($TimestampServer) { $params.TimestampServer = $TimestampServer }

    $r = Set-AuthenticodeSignature @params
    $check = Get-AuthenticodeSignature -LiteralPath $f.FullName
    if ($check.Status -eq 'Valid') {
        $ok++
        Write-Host ("  ✔ {0}  [{1}] 发行者: {2}" -f $f.Name, $check.Status, $check.SignerCertificate.Subject) -ForegroundColor Green
    } else {
        $bad++
        Write-Host ("  ✘ {0}  [{1}] {2}" -f $f.Name, $check.Status, $check.StatusMessage) -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host ("签名完成：成功 {0}，未通过 {1}。" -f $ok, $bad) -ForegroundColor Cyan
if ($bad -gt 0) {
    Write-Host "自签名证书在本机未受信任时会出现 UnknownError / UntrustedRoot，" -ForegroundColor Yellow
    Write-Host "先运行 New-DevCodeSigningCert.ps1（不要加 -NoTrust）即可。" -ForegroundColor Yellow
}
