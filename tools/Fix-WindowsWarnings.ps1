<#
.SYNOPSIS
    诊断并消除 Windows 的两类启动告警（无法验证发布者 / SmartScreen 已保护你的电脑）。

.DESCRIPTION
    告警只有三个来源，本脚本分别处理：

      1) 区域标记（MOTW / Zone.Identifier）—— “打开文件 - 安全警告：无法验证发布者”的直接原因
      2) 没有签名 —— “发行商：未知发布者”，也让 SmartScreen 无从放行
      3) SmartScreen“检查应用和文件”—— 未签名程序被拦的最后一道闸

    默认只扫描不修改。要动手请显式加开关。

.PARAMETER Root
    要处理的目录，默认是脚本所在目录的上一级（工程根）。

.PARAMETER Unblock
    递归清除所有文件（含 zip、快捷方式）的区域标记。

.PARAMETER Sign
    创建/复用开发用自签名证书，并给所有 exe/dll 签名（证书会装进当前用户受信任存储）。

.PARAMETER Thumbprint
    指定已有证书指纹；省略则自动选择有效期最长的代码签名证书。

.PARAMETER DisableAppReputation
    关闭 SmartScreen 的“检查应用和文件”（写 HKCU，原值备份到 artifacts\smartscreen-backup.json）。
    这是降低防护的开关，仅建议在你自己开发机上用。

.PARAMETER Restore
    用备份恢复“检查应用和文件”的原值。

.EXAMPLE
    .\Fix-WindowsWarnings.ps1
    只诊断，告诉你到底带不带标记、有没有签名、开关是什么状态。

.EXAMPLE
    .\Fix-WindowsWarnings.ps1 -Unblock -Sign
    去掉标记 + 签好名（本机“发行商”显示成你自己）。

.EXAMPLE
    .\Fix-WindowsWarnings.ps1 -DisableAppReputation
    关掉“检查应用和文件”（可随时 -Restore 恢复）。
#>
[CmdletBinding()]
param(
    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch] $Unblock,
    [switch] $Sign,
    [string] $Thumbprint,
    [switch] $DisableAppReputation,
    [switch] $Restore
)

$ErrorActionPreference = 'Stop'
$backupFile = Join-Path $Root 'artifacts\smartscreen-backup.json'
$appHostKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\AppHost'

function Get-Mark([string] $p) {
    [bool](Get-Item -LiteralPath $p -Stream Zone.Identifier -ErrorAction SilentlyContinue)
}

# ---------- 1. 扫描 ----------
Write-Host "`n=== 1. 扫描 $Root ===" -ForegroundColor Cyan
$files = Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue
# 只有“可执行/可触发”的类型才需要看区域标记，整树逐文件查会非常慢
$risky = @($files | Where-Object { $_.Extension -in '.exe', '.dll', '.msi', '.ps1', '.bat', '.cmd', '.scr', '.com', '.zip', '.lnk', '.js', '.vbs' })
$marked = @($risky | Where-Object { Get-Mark $_.FullName })
$exes   = @($files | Where-Object { $_.Extension -in '.exe', '.dll' -and $_.FullName -match '\\(bin|publish|publish-fd|artifacts)\\' })
$unsigned = @($exes | Where-Object { (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid' })

Write-Host ("  文件总数      : {0}" -f $files.Count)
Write-Host ("  可执行类文件  : {0}" -f $risky.Count)
Write-Host ("  带区域标记    : {0}" -f $marked.Count) -ForegroundColor $(if ($marked.Count) { 'Yellow' } else { 'Green' })
Write-Host ("  产物 exe/dll  : {0}" -f $exes.Count)
Write-Host ("  未通过签名    : {0}" -f $unsigned.Count)

if ($marked.Count -gt 0) {
    Write-Host "  带标记的文件（前 15 个）：" -ForegroundColor Yellow
    $marked | Select-Object -First 15 | ForEach-Object { Write-Host ("    " + $_.FullName.Replace($Root + '\', '')) }
}
# 需要重点看可执行文件
$markedExe = @($marked | Where-Object { $_.Extension -in '.exe', '.dll', '.msi' })
if ($markedExe.Count -gt 0) {
    Write-Host "  >>> 这些可执行文件运行时一定会弹框：" -ForegroundColor Red
    $markedExe | ForEach-Object { Write-Host ("      " + $_.FullName) -ForegroundColor Red }
}

# ---------- 2. 系统开关状态 ----------
Write-Host "`n=== 2. 系统开关 ===" -ForegroundColor Cyan
$reg = Get-ItemProperty -Path $appHostKey -ErrorAction SilentlyContinue
$eval = if ($null -ne $reg.EnableWebContentEvaluation) { $reg.EnableWebContentEvaluation } else { 1 }
Write-Host ("  SmartScreen 检查应用和文件 : {0}" -f $(if ($eval -eq 0) { '已关闭' } else { '开启（默认）' }))
Write-Host ("  阻止用户覆盖              : {0}" -f $(if ($reg.PreventOverride -eq 1) { '是（点了也没用）' } else { '否' }))
$sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
Write-Host ("  智能应用控制(SAC)         : {0}" -f $(switch ($sac) { 0 { '关闭' } 1 { '开启（未签名程序一律阻止）' } 2 { '评估模式' } default { "未知($sac)" } }))
Write-Host ("  安装包提权要求(installer.iss) : {0}" -f $(if (Test-Path (Join-Path $Root 'tools\installer.iss')) { ((Select-String -Path (Join-Path $Root 'tools\installer.iss') -Pattern 'PrivilegesRequired=' -ErrorAction SilentlyContinue | Select-Object -First 1).Line.Trim()) } else { '无' }))

# ---------- 3. 去标记 ----------
if ($Unblock) {
    Write-Host "`n=== 3. 清除区域标记 ===" -ForegroundColor Cyan
    $n = 0
    foreach ($f in $marked) { Unblock-File -LiteralPath $f.FullName -ErrorAction SilentlyContinue; $n++ }
    Write-Host ("  已解除锁定 {0} 个文件。" -f $n) -ForegroundColor Green
}

# ---------- 4. 签名 ----------
if ($Sign) {
    Write-Host "`n=== 4. 签名 ===" -ForegroundColor Cyan
    if ($Thumbprint) {
        $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
    } else {
        $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
                Sort-Object NotAfter -Descending | Select-Object -First 1
        if (-not $cert) {
            Write-Host "  未找到代码签名证书，创建一张开发用自签名证书……" -ForegroundColor Yellow
            $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=CommTestTool (Dev)' `
                -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
                -KeyUsage DigitalSignature -CertStoreLocation 'Cert:\CurrentUser\My' `
                -NotAfter (Get-Date).AddYears(3) `
                -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
            foreach ($storeName in 'Root', 'TrustedPublisher') {
                $st = [System.Security.Cryptography.X509Certificates.X509Store]::new($storeName, 'CurrentUser')
                $st.Open('ReadWrite'); try { $st.Add($cert) } finally { $st.Close() }
            }
            Write-Host ("  已创建并信任证书: {0}" -f $cert.Thumbprint) -ForegroundColor Green
        }
    }
    Write-Host ("  使用证书: {0}" -f $cert.Subject)
    $targets = @($exes | Where-Object {
        $_.FullName -match '\\(bin|publish|artifacts)\\' -and $_.Extension -eq '.exe'
    })
    $ok = 0
    foreach ($t in $targets) {
        Set-AuthenticodeSignature -FilePath $t.FullName -Certificate $cert -HashAlgorithm SHA256 `
            -TimestampServer 'http://timestamp.digicert.com' -ErrorAction SilentlyContinue | Out-Null
        if ((Get-AuthenticodeSignature -LiteralPath $t.FullName).Status -eq 'Valid') { $ok++ }
    }
    Write-Host ("  签名完成 {0}/{1}（其余多为依赖库或时间戳服务不可达）" -f $ok, $targets.Count) -ForegroundColor Green
}

# ---------- 5. 关闭/恢复“检查应用和文件” ----------
if ($DisableAppReputation) {
    Write-Host "`n=== 5. 关闭 SmartScreen 检查应用和文件 ===" -ForegroundColor Cyan
    if (-not (Test-Path (Split-Path -Parent $backupFile))) { New-Item -ItemType Directory -Path (Split-Path -Parent $backupFile) -Force | Out-Null }
    $old = Get-ItemProperty -Path $appHostKey -ErrorAction SilentlyContinue
    if ($old) {
        @{ EnableWebContentEvaluation = $old.EnableWebContentEvaluation; PreventOverride = $old.PreventOverride } |
            ConvertTo-Json | Set-Content -Path $backupFile -Encoding UTF8
        Write-Host ("  原值已备份到 {0}" -f $backupFile) -ForegroundColor DarkGray
    }
    if (-not (Test-Path $appHostKey)) { New-Item -Path $appHostKey -Force | Out-Null }
    New-ItemProperty -Path $appHostKey -Name EnableWebContentEvaluation -Value 0 -PropertyType DWord -Force | Out-Null
    Write-Host "  已关闭。新版 Windows 安全中心界面：应用和浏览器控制 → 基于声誉的保护设置 → 检查应用和文件 → 关" -ForegroundColor Yellow
    Write-Host "  注意：这会降低整机防护，仅建议开发机使用；随时可用 -Restore 恢复。" -ForegroundColor Yellow
}

if ($Restore) {
    Write-Host "`n=== 5. 恢复 SmartScreen 设置 ===" -ForegroundColor Cyan
    if (Test-Path $backupFile) {
        $b = Get-Content $backupFile -Raw | ConvertFrom-Json
        if ($null -ne $b.EnableWebContentEvaluation) {
            New-ItemProperty -Path $appHostKey -Name EnableWebContentEvaluation -Value $b.EnableWebContentEvaluation -PropertyType DWord -Force | Out-Null
        } else {
            Remove-ItemProperty -Path $appHostKey -Name EnableWebContentEvaluation -ErrorAction SilentlyContinue
        }
        Write-Host "  已从备份恢复。" -ForegroundColor Green
    } else {
        New-ItemProperty -Path $appHostKey -Name EnableWebContentEvaluation -Value 1 -PropertyType DWord -Force | Out-Null
        Write-Host "  无备份，已重置为开启(1)。" -ForegroundColor Yellow
    }
}

Write-Host "`n完成。若告警仍在，请把上面的完整输出发我。" -ForegroundColor Cyan
