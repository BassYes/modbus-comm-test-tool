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
    默认 win-x64。自包含（-SelfContained）时体积大但目标机免装运行时。

.PARAMETER SelfContained
    自包含发布。

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
    .\Publish-App.ps1 -SelfContained -Sign -Zip -TimestampServer http://timestamp.digicert.com
#>
[CmdletBinding()]
param(
    [string] $Configuration   = 'Release',
    [string] $Runtime         = 'win-x64',
    [switch] $SelfContained,
    [string] $OutputPath      = (Join-Path $PSScriptRoot '..\artifacts\publish'),
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

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Recurse -Force }
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

Write-Host "发布到: $OutputPath" -ForegroundColor Cyan
& $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained:$($SelfContained.IsPresent) `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# 清 MOTW
Get-ChildItem -LiteralPath $OutputPath -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue

if ($Sign) {
    Write-Host "开始签名……" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Sign-App.ps1') -Path (Join-Path $OutputPath '*.exe') -TimestampServer $TimestampServer
    if ($LASTEXITCODE -ne 0) { throw "签名失败" }
}

if ($Zip) {
    $ver = (Get-Item (Join-Path $OutputPath 'CommTestTool.dll')).VersionInfo.FileVersion
    if (-not $ver) { $ver = '0.0.0' }
    $zipPath = Join-Path (Split-Path -Parent $OutputPath) ("CommTestTool-{0}-{1}.zip" -f $ver, $Runtime)
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $OutputPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "已打包: $zipPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "完成。产物：" -ForegroundColor Green
Get-ChildItem -LiteralPath $OutputPath -File | Select-Object Name, @{n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } |
    Format-Table -AutoSize
