<#
.SYNOPSIS
    M2.5 第 6 项：真实硬件回归（USB-RS485 + 已知从站）的一键探针。

.DESCRIPTION
    在接好 USB-RS485 适配器与已知从站之后运行本脚本：
      1. 罗列当前串口；
      2. 按给定组合遍历波特率/校验位，发送只读请求并记录回显、拆帧与字节间隔；
      3. 输出报告文件，内容可直接回填 docs\hardware-compatibility-matrix-v1.md。

    探针程序是 tools\Comm.HilProbe（只读、只发 03 读保持寄存器，不做任何写操作）。

.PARAMETER Port
    串口号（如 COM5）。省略时只罗列串口并退出。

.PARAMETER Unit
    已知从站地址，默认 1。

.PARAMETER Address
    起始寄存器地址，默认 0。

.PARAMETER Quantity
    读取数量，默认 1。

.PARAMETER Baud
    候选波特率，默认 9600,19200（建议先用已知参数，再扩大到未知参数）。

.PARAMETER Parity
    候选校验位，默认 N,E（可选 N/E/O）。

.PARAMETER Stop
    候选停止位，默认 1（可选 1/2）。

.PARAMETER OutputPath
    报告输出路径，默认 ..\artifacts\hil-probe-<时间>.txt。

.EXAMPLE
    .\Invoke-HilProbe.ps1                       # 只看有哪些串口

.EXAMPLE
    .\Invoke-HilProbe.ps1 -Port COM5 -Unit 5 -Baud 9600,19200,38400 -Parity N,E,O

.NOTES
    开始前请确认：从站已上电、A/B 接线正确、总线上没有其他主站正在轮询。
#>
[CmdletBinding()]
param(
    [string] $Port,
    [int]    $Unit = 1,
    [int]    $Address = 0,
    [int]    $Quantity = 1,
    [string] $Baud = '9600,19200',
    [string] $Parity = 'N,E',
    [string] $Stop = '1',
    [int]    $Timeout = 1000,
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project  = Join-Path $repoRoot 'tools\Comm.HilProbe\Comm.HilProbe.csproj'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

if (-not $Port) {
    Write-Host "未指定 -Port，仅罗列当前串口：" -ForegroundColor Cyan
    & $dotnet run --project $project
    Write-Host ""
    Write-Host "接好适配器后重新运行，例如：" -ForegroundColor Yellow
    Write-Host "  .\tools\Invoke-HilProbe.ps1 -Port COM5 -Unit 5"
    return
}

if (-not $OutputPath) {
    $artifacts = Join-Path $repoRoot 'artifacts'
    if (-not (Test-Path -LiteralPath $artifacts)) { New-Item -ItemType Directory -Path $artifacts -Force | Out-Null }
    $OutputPath = Join-Path $artifacts ("hil-probe-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

Write-Host "开始真实硬件回归探针：$Port（Unit $Unit，只发 03 只读请求）" -ForegroundColor Cyan
& $dotnet run --project $project -- `
    --port $Port --unit $Unit --address $Address --quantity $Quantity `
    --baud $Baud --parity $Parity --stop $Stop --timeout $Timeout --out $OutputPath

if ($LASTEXITCODE -ne 0) { throw "探针执行失败（退出码 $LASTEXITCODE）" }

Write-Host ""
Write-Host "请把报告中的“结论：可用/不可用”、回显情况与最大字节间隔回填到：" -ForegroundColor Green
Write-Host "  docs\hardware-compatibility-matrix-v1.md"
