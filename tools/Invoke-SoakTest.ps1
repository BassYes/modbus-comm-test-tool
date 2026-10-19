<#
.SYNOPSIS
    稳定性（长跑）测试入口：按指定时长反复运行性能探针并采样内存，生成可回填的报告。

.DESCRIPTION
    规划 16.3.4 要求“持续收发 ≥ 24 h：内存无持续增长、句柄无泄漏、无未观察异常”。
    本脚本把这条要求变成可执行的流程：

      1. 运行 tools\Comm.StressProbe（默认 60 s @1000 帧/s + 20 万条显示缓冲）；
      2. 每轮结束后采样进程工作集内存与探针退出码；
      3. 循环到达到指定时长；期间任何一轮失败都会记录但不中断（便于收集完整曲线）；
      4. 输出报告 `docs\stability-report.md`（含每轮时间、内存、结论与阈值），供人工回填现场机数据。

.PARAMETER Hours
    总时长（小时），默认 24。

.PARAMETER RoundSeconds
    每轮探针的持续注入秒数，默认 60。

.PARAMETER OutputPath
    报告输出路径，默认 docs\stability-report.md。

.PARAMETER Quick
    快速自检：总时长 5 分钟、每轮 10 s（用于 CI/提交前冒烟）。

.EXAMPLE
    .\tools\Invoke-SoakTest.ps1 -Quick
.EXAMPLE
    .\tools\Invoke-SoakTest.ps1 -Hours 24
#>
[CmdletBinding()]
param(
    [double] $Hours = 24,
    [int] $RoundSeconds = 60,
    [string] $OutputPath,
    [switch] $Quick
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

if ($Quick) { $Hours = 5.0 / 60.0; $RoundSeconds = 10 }
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'docs\stability-report.md' }

$totalMinutes = [Math]::Max(1, $Hours * 60)
$deadline = (Get-Date).AddMinutes($totalMinutes)
$rounds = New-Object System.Collections.Generic.List[object]
$failed = 0
$round = 0

Write-Host "稳定性测试开始：总时长 $([Math]::Round($totalMinutes,1)) 分钟，每轮 $RoundSeconds s" -ForegroundColor Cyan
Write-Host "报告输出：$OutputPath" -ForegroundColor Cyan

while ((Get-Date) -lt $deadline) {
    $round++
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # 优先读探针输出的 JSON 摘要（避免解析中文控制台输出：PowerShell 5.1 捕获子进程
    # 输出时使用 OEM 代码页，中文会乱码导致正则匹配失败）。
    # 注意：不要写系统 TEMP——本机受限环境下子进程对 TEMP 无写权限；统一放在 artifacts\soak。
    $soakDir = Join-Path $repoRoot 'artifacts\soak'
    New-Item -ItemType Directory -Path $soakDir -Force | Out-Null
    $jsonPath = Join-Path $soakDir ("stressprobe-{0}.json" -f [Guid]::NewGuid().ToString('N'))
    $output = & $dotnet run --project (Join-Path $repoRoot 'tools\Comm.StressProbe') -- `
        --seconds $RoundSeconds --frames 200000 --json $jsonPath 2>&1
    $code = $LASTEXITCODE
    $sw.Stop()

    $memText = 'n/a'
    $conclusionText = '未判定'
    if (Test-Path -LiteralPath $jsonPath) {
        try {
            $summary = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $memText = [string]$summary.processMemoryMb
            $conclusionText = if ($summary.passed) { '全部阈值通过' } else { ("{0} 项阈值未通过" -f $summary.failures) }
        } catch {
            $conclusionText = 'JSON 解析失败'
        } finally {
            Remove-Item -LiteralPath $jsonPath -ErrorAction SilentlyContinue
        }
    } else {
        $conclusionText = '未生成 JSON 摘要（按退出码判定）'
    }

    if ($code -ne 0) { $failed++ }

    $rounds.Add([pscustomobject]@{
        Round     = $round
        StartedAt = (Get-Date).AddSeconds(-$sw.Elapsed.TotalSeconds).ToString('yyyy-MM-dd HH:mm:ss')
        Seconds   = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        MemoryMb  = $memText
        ExitCode  = $code
        Verdict   = $conclusionText
    })

    Write-Host ("  第 {0} 轮：{1} s，内存 {2} MB，退出码 {3}" -f $round, [Math]::Round($sw.Elapsed.TotalSeconds,1), $memText, $code)
}

$first = $rounds | Select-Object -First 1
$last = $rounds | Select-Object -Last 1
$memStart = if ($first.MemoryMb -ne 'n/a') { [double]$first.MemoryMb } else { $null }
$memEnd = if ($last.MemoryMb -ne 'n/a') { [double]$last.MemoryMb } else { $null }
$drift = if ($memStart -and $memEnd) { [Math]::Round($memEnd - $memStart, 1) } else { $null }

# 审计 F-09：内存漂移必须是"机器可判定"的失败条件，而不是只写进报告供人看。
# 阈值：末轮比首轮增长 > 20% 且绝对增量 > 32 MB 判失败（小基数下的噪声不足以触发）。
$memGrowthPct = if ($memStart -and $memEnd -and $memStart -gt 0) {
    [Math]::Round((($memEnd - $memStart) / $memStart) * 100, 1)
} else { $null }
$memVerdict = 'n/a'
if ($memGrowthPct -ne $null) {
    $memVerdict = if ($drift -gt 32 -and $memGrowthPct -gt 20) { 'FAIL（疑似增长）' } else { 'PASS' }
}
$memGateFailed = ($memVerdict -like 'FAIL*')

$lines = @()
$lines += '# 稳定性（长跑）测试报告'
$lines += ''
$lines += "| 项目 | 内容 |"
$lines += "|---|---|"
$lines += "| 生成时间 | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz') |"
$lines += "| 机器 | $env:COMPUTERNAME（$([System.Environment]::OSVersion.VersionString)） |"
$lines += "| 计划时长 | $([Math]::Round($totalMinutes,1)) 分钟（每轮 $RoundSeconds s） |"
$lines += "| 实际轮数 | $($rounds.Count) |"
$lines += "| 失败轮数 | $failed |"
$lines += "| 内存（首轮 → 末轮） | $(if ($memStart) { "$memStart MB → $memEnd MB（漂移 $drift MB / $memGrowthPct%）" } else { 'n/a' }) |"
$lines += "| 内存门禁 | $memVerdict |"
$lines += ''
$lines += '## 每轮明细'
$lines += ''
$lines += '| 轮次 | 开始时间 | 用时 s | 进程内存 MB | 退出码 | 结论 |'
$lines += '|---|---|---|---|---|---|'
foreach ($r in $rounds) {
    $lines += "| $($r.Round) | $($r.StartedAt) | $($r.Seconds) | $($r.MemoryMb) | $($r.ExitCode) | $($r.Verdict) |"
}
$lines += ''
$lines += '## 判定标准（规划 16.3.4 / 16.3.5）'
$lines += ''
$lines += '- 每轮退出码必须为 0（全部阈值通过）。'
$lines += '- 进程内存不得随轮次持续增长：末轮相对首轮增长 > 20% 且绝对增量 > 32 MB 判失败（脚本退出码非 0）。'
$lines += '- 无未观察异常、无崩溃报告（`logs\crash-*.log` 不新增）。'
$lines += ''
$lines += '> 说明：本脚本覆盖“长时间连续收发 + 落盘 + 显示缓冲”的软件路径。'
$lines += '> 现场 24 h 结论还需要在接真实硬件（USB-RS485 + 从站）的机器上运行，'
$lines += '> 并把 jitter/回显/断线重连等观察结果回填 `docs\hardware-compatibility-matrix-v1.md`。'

Set-Content -LiteralPath $OutputPath -Value ($lines -join "`r`n") -Encoding UTF8

Write-Host ''
Write-Host "稳定性测试结束：$($rounds.Count) 轮，失败 $failed 轮，内存门禁 $memVerdict" -ForegroundColor Green
Write-Host "报告：$OutputPath" -ForegroundColor Green
if ($failed -gt 0) { exit 1 }
if ($memGateFailed) { exit 2 }
exit 0
