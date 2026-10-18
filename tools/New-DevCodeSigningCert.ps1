<#
.SYNOPSIS
    创建一个“开发用”的代码签名证书（自签名），并装进当前用户的受信任存储。

.DESCRIPTION
    自签名证书不能替代正规代码签名证书（别人机器上没有你的根证书，照样会拦），
    但它能解决本机开发时的两件事：
      1. 签名后 exe 的“发行商”不再是“未知发布者”，安全警告框里显示你的名字；
      2. 本机把它当作受信任发布者，双击运行更顺畅，也便于自动化测试签名流程。

    证书装进 CurrentUser 存储，不需要管理员权限。
    如果需要给域内其他机器用，把导出的 .cer 通过组策略分发到“受信任的根证书颁发机构”。

.PARAMETER Subject
    证书主题。默认 CN=CommTestTool (Dev)。

.PARAMETER Years
    有效期年数，默认 3。

.PARAMETER PfxPath
    可选。导出带私钥的 .pfx 到指定路径（用于 CI 或其他机器签名）。

.PARAMETER Password
    导出 .pfx 时使用的密码（明文；仅开发环境使用）。

.PARAMETER NoTrust
    只创建证书，不加入受信任的根/发布者存储。

.EXAMPLE
    .\New-DevCodeSigningCert.ps1

.EXAMPLE
    .\New-DevCodeSigningCert.ps1 -Subject "CN=CommTestTool Dev" -Years 5 -PfxPath ..\artifacts\dev-sign.pfx -Password 123456
#>
[CmdletBinding()]
param(
    [string] $Subject  = 'CN=CommTestTool (Dev)',
    [int]    $Years    = 3,
    [string] $PfxPath,
    [string] $Password,
    [switch] $NoTrust
)

$ErrorActionPreference = 'Stop'

Write-Host "创建代码签名证书：$Subject" -ForegroundColor Cyan
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears($Years) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

Write-Host ("  已生成，指纹: {0}" -f $cert.Thumbprint) -ForegroundColor Green
Write-Host ("  有效期至: {0:yyyy-MM-dd}" -f $cert.NotAfter)

if (-not $NoTrust) {
    # 受信任的根证书颁发机构：让本机信任“这个自签名证书本身”作为链的根
    $root = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
    $root.Open('ReadWrite')
    try { $root.Add($cert) } finally { $root.Close() }

    # 受信任的发布者：让 Authenticode 校验直接通过
    $pub = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPublisher', 'CurrentUser')
    $pub.Open('ReadWrite')
    try { $pub.Add($cert) } finally { $pub.Close() }

    Write-Host "  已加入 当前用户\受信任的根证书颁发机构 和 受信任的发布者" -ForegroundColor Green
}

if ($PfxPath) {
    $dir = Split-Path -Parent $PfxPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $sec = ConvertTo-SecureString -String ($(if ($Password) { $Password } else { 'P@ssw0rd' })) -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $sec | Out-Null
    Write-Host "  已导出 PFX: $PfxPath（务必妥善保管，泄露即可冒名签名）" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "下一步：" -ForegroundColor Cyan
Write-Host ("  .\Sign-App.ps1 -Path ..\src\Comm.App.Wpf\bin\Debug\net8.0-windows\CommTestTool.exe -Thumbprint {0}" -f $cert.Thumbprint)
