<#
.SYNOPSIS
    构建“安装包”分发渠道：优先用 Inno Setup 生成正式签名安装包；无 Inno 时回退为自解压安装包（zip + 安装脚本）。

.DESCRIPTION
    规划 22.5.2 分层处置 R-20：
      L1 数字签名（Publish-App.ps1 -Sign）
      L2 安装包 —— 本脚本
      L3 便携包（Publish-App.ps1 -Zip，含解除锁定脚本）
      L4 受控电脑（组策略导入内部证书）

    为什么安装包更彻底：安装到 Program Files 的文件**不带 MOTW**（"来自网络"标记），
    因此安装后每次启动都不会再触发"打开文件 - 安全警告"。

    本脚本做四件事：
      1. 确认发布产物存在（没有就先调用 Publish-App.ps1）；
      2. 有 Inno Setup 时：用 tools\installer.iss 生成正式安装包（并从程序集读版本号）；
      3. 无 Inno Setup 时：生成"便携安装包"（zip，内含 Install-Portable.ps1，解压到目标目录并建快捷方式），
         并在输出里明确标注这是回退方案（不写注册表、卸载即删除目录）；
      4. 可选签名（-Sign）所有产物，并打印校验结果。

.PARAMETER Version
    版本号；省略时从 artifacts\publish\CommTestTool.exe 的文件版本读取（唯一来源仍是 Directory.Build.props）。

.PARAMETER Sign
    构建后对安装包与便携包签名（需要代码签名证书；见 tools\Sign-App.ps1）。

.PARAMETER TimestampServer
    RFC3161 时间戳服务，例如 http://timestamp.digicert.com。

.PARAMETER UseInno
    明确要求使用 Inno Setup；找不到 ISCC.exe 时报错退出（而不是静默回退）。

.PARAMETER InnoCompiler
    ISCC.exe 的完整路径（不在 PATH 时使用）。

.EXAMPLE
    .\tools\Build-Installer.ps1 -Sign -TimestampServer http://timestamp.digicert.com
.EXAMPLE
    .\tools\Build-Installer.ps1 -UseInno -InnoCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $Sign,
    [string] $TimestampServer,
    [switch] $UseInno,
    [string] $InnoCompiler
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$installerDir = Join-Path $repoRoot 'artifacts\installer'

if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'CommTestTool.exe'))) {
    Write-Host "发布产物不存在，先执行 Publish-App.ps1 ……" -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Publish-App.ps1')
    if ($LASTEXITCODE -ne 0) { throw "发布失败，无法继续构建安装包" }
}

New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

# 版本号：优先参数，其次从程序集读（X-9：版本号单一来源）
if (-not $Version) {
    $Version = (Get-Item (Join-Path $publishDir 'CommTestTool.exe')).VersionInfo.FileVersion
    if (-not $Version) { $Version = '0.0.0' }
    $Version = ($Version -replace '\s', '')
}
Write-Host "安装包版本：$Version" -ForegroundColor Cyan

# ---------------------------------------------------------------- Inno Setup
if (-not $InnoCompiler) {
    # 注意：管道只命中一个路径时结果是 String，$candidates[0] 会取到首字符 'C'（PS 的字符串索引语义），
    # 因此必须用 @() 强制成数组，否则"只装了一处 Inno Setup"反而检测不到。
    $candidates = @(@(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $PSScriptRoot '_build\inno\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ })
    if ($candidates.Count -gt 0) { $InnoCompiler = $candidates[0] }
}

if ($InnoCompiler -and (Test-Path -LiteralPath $InnoCompiler)) {
    Write-Host "使用 Inno Setup：$InnoCompiler" -ForegroundColor Cyan
    $iss = Join-Path $PSScriptRoot 'installer.iss'
    & $InnoCompiler "/DMyAppVersion=$Version" $iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）" }

    $setup = Get-ChildItem -LiteralPath $installerDir -Filter '*安装包*.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $setup) { throw "未找到 Inno Setup 生成的安装包" }

    Write-Host "已生成安装包：$($setup.FullName)" -ForegroundColor Green
    $artifact = $setup.FullName
    $kind = 'Inno Setup 安装包'
}
else {
    if ($UseInno) {
        throw "找不到 ISCC.exe。请安装 Inno Setup 6，或用 -InnoCompiler 指定路径；也可以去掉 -UseInno 使用回退方案。"
    }

    # ---------------------------------------------------------------- 回退：便携安装包
    Write-Host "未检测到 Inno Setup —— 生成回退方案：便携安装包（zip + 安装脚本）" -ForegroundColor Yellow

    $staging = Join-Path $installerDir 'portable-staging'
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    # 复制发布产物（排除运行时数据/日志）
    $exclude = @('data', 'logs', 'exports')
    Get-ChildItem -LiteralPath $publishDir | Where-Object { $exclude -notcontains $_.Name } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse -Force
    }

    $installScript = @'
<#
    便携安装脚本（通讯测试工具）
    ============================
    用途：把本目录的程序安装到指定位置并创建快捷方式（不写注册表、不改系统设置）。
    参数：
      -Target   目标目录，默认 %LOCALAPPDATA%\Programs\通讯测试工具
      -Desktop  同时创建桌面快捷方式
    卸载：删除目标目录即可（用户数据在 %APPDATA% / %LOCALAPPDATA% 下，会保留）。
#>
[CmdletBinding()]
param(
    [string] $Target = (Join-Path $env:LOCALAPPDATA 'Programs\通讯测试工具'),
    [switch] $Desktop
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot

Write-Host "安装到：$Target" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $Target -Force | Out-Null

# 复制程序文件（保留子目录 helper\ 与 plugins\）
Get-ChildItem -LiteralPath $source | Where-Object { $_.Name -ne 'Install-Portable.ps1' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $Target -Recurse -Force
}

$exe = Join-Path $Target 'CommTestTool.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "安装失败：未找到 $exe" }

# 开始菜单快捷方式
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) '通讯测试工具'
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $startMenu '通讯测试工具.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $Target
$link.Description = '通讯测试工具（Modbus RTU / TCP 现场调试）'
$link.Save()

if ($Desktop) {
    $desktopLink = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) '通讯测试工具.lnk'))
    $desktopLink.TargetPath = $exe
    $desktopLink.WorkingDirectory = $Target
    $desktopLink.Save()
}

Write-Host ''
Write-Host '安装完成。' -ForegroundColor Green
Write-Host "  程序：$exe"
Write-Host "  开始菜单：$startMenu"
Write-Host '  卸载：删除上面这个程序目录即可（用户设置与日志会保留）。'
Write-Host '  提示：本包的 helper\Comm.NetConfigHelper.exe 是网卡配置提权助手，请勿删除。'
'@

    Set-Content -LiteralPath (Join-Path $staging 'Install-Portable.ps1') -Value $installScript -Encoding UTF8

    $zipName = "通讯测试工具-$Version-便携安装包.zip"
    $zipPath = Join-Path $installerDir $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Remove-Item -LiteralPath $staging -Recurse -Force

    Write-Host "已生成便携安装包：$zipPath" -ForegroundColor Green
    $artifact = $zipPath
    $kind = '便携安装包（回退方案，无 Inno Setup）'
}

# ---------------------------------------------------------------- 受限环境启动器
# 有些机器的 %TEMP% 对提权进程不可写，安装包会在解包阶段报「错误 5：拒绝访问」。
# 把启动器放到与安装包同一目录：双击它 → 先提权 → 把 TEMP 指到可写目录 → 再启动安装包。
$launcher = Join-Path $PSScriptRoot '安装-受限环境.cmd'
if (Test-Path -LiteralPath $launcher) {
    $launcherTarget = Join-Path $installerDir '安装-受限环境（双击我）.cmd'
    Copy-Item -LiteralPath $launcher -Destination $launcherTarget -Force
    Write-Host "已随包附上受限环境启动器：$launcherTarget" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- 可选签名
if ($Sign) {
    Write-Host "签名安装包产物……" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Sign-App.ps1') -Path $artifact -TimestampServer $TimestampServer
    if ($LASTEXITCODE -ne 0) { throw "签名失败" }
}

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
Write-Host "  类型：$kind"
Write-Host "  路径：$artifact"
Write-Host ''
Write-Host '分发建议（规划 22.5）：'
Write-Host '  · 网络渠道（网盘/微信/QQ/邮件）：优先给安装包；便携包需按“便携包说明.txt”解除锁定。'
Write-Host '  · U 盘渠道：安装包与便携包都可用；安装包安装后不再带“来自网络”标记。'
Write-Host '  · 正式对外发布前必须先定案代码签名证书（规划 Q-1），否则 UAC 仍显示“未知发布者”。'
