<#
.SYNOPSIS
    发布发布包：dotnet publish 到干净目录 → 解除 MOTW → 可选签名 → 可选打 zip。

.DESCRIPTION
    直接把人 bin\Debug 打包发出去，是最容易踩坑的做法（一堆中间文件 + 可能残留标记）。
    本脚本走“发布目录”这条正路：

      1. 用本机 .NET 8 SDK（C:\Users\Bass\.dotnet\dotnet.exe）publish 到 artifacts\publish；
      2. 清掉产物上的 MOTW（防止产物被搬过之后又带上标记）；
      3. 可选地逐个 exe/dll 签名；
      4. 可选地打成 zip（注意：zip 经网盘/微信传输后，解压出来的 exe 会重新带上标记）。

.PARAMETER Configuration
    默认 Release。

.PARAMETER Runtime
    默认 win-x64。默认自包含（体积大但目标机免装运行时）。

.PARAMETER SelfContained
    自包含发布。**默认已开启**（不写也是自包含）；保留该开关以兼容旧用法。

.PARAMETER FrameworkDependent
    改为框架依赖发布（体积小，但目标机必须预装 .NET 8 Desktop Runtime）。

.PARAMETER OutputPath
    输出目录，默认 ..\artifacts\publish。

.PARAMETER Sign
    发布后立即签名（需要已有代码签名证书）。

.PARAMETER TimestampServer
    签名时使用的 RFC3161 时间戳服务。

.PARAMETER Zip
    额外生成 artifacts\CommTestTool-<version>-<runtime>.zip。

.EXAMPLE
    .\Publish-App.ps1 -Sign

.EXAMPLE
    # 默认即自包含；加 -Sign -Zip 产出签名便携包
    .\Publish-App.ps1 -Sign -Zip -TimestampServer http://timestamp.digicert.com

.EXAMPLE
    # 框架依赖小包（需 .NET 8 Desktop Runtime）
    .\Publish-App.ps1 -FrameworkDependent -Sign
#>
[CmdletBinding()]
param(
    [string] $Configuration   = 'Release',
    [string] $Runtime         = 'win-x64',
    # X-1 / D-12：默认就是自包含 win-x64 单目录（目标机无需预装 .NET 8 Desktop Runtime）。
    # 保留 -SelfContained 以兼容旧用法；要出“框架依赖小包”请加 -FrameworkDependent。
    [switch] $SelfContained,
    [switch] $FrameworkDependent,
    # 默认在脚本下方解析（param 默认值里 $PSScriptRoot 在部分调用方式下为空）
    [string] $OutputPath,
    [switch] $Sign,
    [string] $TimestampServer,
    [switch] $Zip
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project  = Join-Path $repoRoot 'src\Comm.App.Wpf\Comm.App.Wpf.csproj'

# 本机默认 dotnet 只有 3.0.101，必须用用户目录下的 8.x SDK
$dotnet = 'C:\Users\Bass\.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

$selfContained = -not $FrameworkDependent
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'artifacts\publish' }
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Recurse -Force }
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

Write-Host "发布到: $OutputPath" -ForegroundColor Cyan
& $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained:$selfContained `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# 清 MOTW
Get-ChildItem -LiteralPath $OutputPath -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue

if ($Sign) {
    # R-20 L1：自有 exe 与 dll 都要签名（只签 exe 会留下未签名的自有程序集）。
    Write-Host "开始签名（exe + 自有 dll）……" -ForegroundColor Cyan
    $ownAssemblies = @('CommTestTool.exe', 'Comm.Core.dll', 'Comm.Protocols.Modbus.dll', 'Comm.Transport.dll',
                       'Comm.Session.dll', 'Comm.Scanning.dll', 'Comm.Capture.dll', 'Comm.Network.dll',
                       'Comm.Diagnostics.dll', 'Comm.Settings.dll', 'Comm.Plugins.Abstractions.dll')
    $targets = @()
    foreach ($name in $ownAssemblies) {
        $path = Join-Path $OutputPath $name
        if (Test-Path -LiteralPath $path) { $targets += $path }
    }
    if ($targets.Count -eq 0) { throw "发布目录里没有找到任何可签名文件：$OutputPath" }

    & (Join-Path $PSScriptRoot 'Sign-App.ps1') -Path $targets -TimestampServer $TimestampServer
    if ($LASTEXITCODE -ne 0) { throw "签名失败" }
}

# R-20 L3：便携包随包提供“解除锁定”说明与脚本
$unblockSource = Join-Path $PSScriptRoot 'Unblock-Build.ps1'
if (Test-Path -LiteralPath $unblockSource) {
    Copy-Item -LiteralPath $unblockSource -Destination (Join-Path $OutputPath '解除锁定-便携包.ps1') -Force
}
$readmeText = @"
便携包使用说明
==============
1. 本目录内的 CommTestTool.exe 已做数字签名；若从网盘/微信/邮件等渠道获得本包，
   Windows 可能仍提示"无法验证发布者"或"Windows 已保护你的电脑"——这是文件带
   "来自网络"标记（MOTW）导致的，提示里应显示"已验证的发布者"。
2. 最省事的做法：右键 zip -> 属性 -> 勾选"解除锁定"后再解压；
   或解压后运行本目录的 解除锁定-便携包.ps1（等价于逐个文件"解除锁定"）。
3. 需要彻底不弹提示，请使用签名安装包（安装出来的文件不带网络标记）。
4. 本程序不需要管理员权限；数据与日志默认写在程序同级 data/ 目录；
   若放在只读目录（如 Program Files），会自动回退到 %LOCALAPPDATA%\通讯测试工具\data。
"@
Set-Content -LiteralPath (Join-Path $OutputPath '便携包说明.txt') -Value $readmeText -Encoding UTF8

if ($Zip) {
    # X-9：版本号来自程序集（唯一来源 Directory.Build.props 的 Version）
    $ver = (Get-Item (Join-Path $OutputPath 'CommTestTool.dll')).VersionInfo.FileVersion
    if (-not $ver) { $ver = '0.0.0' }
    $ver = $ver -replace '\s', ''
    $zipPath = Join-Path (Split-Path -Parent $OutputPath) ("CommTestTool-{0}-{1}.zip" -f $ver, $Runtime)
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $OutputPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "已打包: $zipPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "完成。产物：" -ForegroundColor Green
Get-ChildItem -LiteralPath $OutputPath -File | Select-Object Name, @{n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } |
    Format-Table -AutoSize
