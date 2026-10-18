<#
.SYNOPSIS
    生成应用程序图标 src\Comm.App.Wpf\app.ico（多尺寸，PNG 压缩条目）。

.DESCRIPTION
    规划 22.6.2 X-3 要求补全应用图标与版本资源：有图标与完整版本信息的可执行文件
    在杀软/SmartScreen 的判断里更“像正规软件”，也便于用户在任务栏/文件列表里识别。

    图标设计（可随时替换成自己的 .ico）：
      深蓝圆角底 + 青色通信波形 + 两个端点，呼应“Modbus 现场通信测试”。

    输出：src\Comm.App.Wpf\app.ico（16/24/32/48/64/128/256），
    随后 Comm.App.Wpf.csproj 的 <ApplicationIcon> 会自动使用它。

.PARAMETER OutputPath
    输出 .ico 路径，默认 ..\src\Comm.App.Wpf\app.ico。

.EXAMPLE
    .\New-AppIcon.ps1

.NOTES
    已有设计稿时请直接替换 app.ico，不要再运行本脚本（脚本会覆盖）。
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\Comm.App.Wpf\app.ico')
)

$ErrorActionPreference = 'Stop'
if (-not $OutputPath) {
    $root = if ($PSScriptRoot) { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path } else { (Get-Location).Path }
    $OutputPath = Join-Path $root 'src\Comm.App.Wpfpp.ico'
}
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.Clear([System.Drawing.Color]::Transparent)

        $s = [double]$size
        $pad = [Math]::Max(0.0, $s * 0.02)
        $radius = $s * 0.20

        # 圆角背景
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad)
        $d = $radius * 2
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect,
            [System.Drawing.Color]::FromArgb(255, 28, 46, 66),
            [System.Drawing.Color]::FromArgb(255, 43, 58, 74),
            [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
        $g.FillPath($bg, $path)
        $bg.Dispose()

        # 通信波形（两条相位相反的方波/正弦）
        $penWidth = [Math]::Max(1.0, $s * 0.075)
        $wave = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 90, 214, 232)), $penWidth
        $wave.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $wave.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $wave.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

        $left = $s * 0.18
        $right = $s * 0.82
        $midY = $s * 0.50
        $amp = $s * 0.20

        $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
        $steps = 48
        for ($i = 0; $i -le $steps; $i++) {
            $t = $i / $steps
            $x = $left + ($right - $left) * $t
            $y = $midY - $amp * [Math]::Sin($t * 2 * [Math]::PI)
            $pts.Add((New-Object System.Drawing.PointF $x, $y))
        }
        $g.DrawLines($wave, $pts.ToArray())
        $wave.Dispose()

        # 两端端点（表示“主站—从站”）
        $dotR = [Math]::Max(1.0, $s * 0.075)
        $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 209, 102))
        $g.FillEllipse($dot, ($left - $dotR), ($midY - $dotR), ($dotR * 2), ($dotR * 2))
        $g.FillEllipse($dot, ($right - $dotR), ($midY - $dotR), ($dotR * 2), ($dotR * 2))
        $dot.Dispose()

        $path.Dispose()
    }
    finally {
        $g.Dispose()
    }
    return $bmp
}

# 1) 生成各尺寸 PNG 字节
$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $size; Bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

# 2) 组装 ICO 容器（PNG 压缩条目；Windows Vista+ 支持）
$dir = Split-Path -Parent $OutputPath
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$fs = [System.IO.File]::Create($OutputPath)
$bw = New-Object System.IO.BinaryWriter $fs
try {
    $bw.Write([UInt16]0)                 # reserved
    $bw.Write([UInt16]1)                 # type = icon
    $bw.Write([UInt16]$pngs.Count)       # count

    $offset = 6 + 16 * $pngs.Count
    foreach ($entry in $pngs) {
        $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
        $bw.Write([Byte]$dim)            # width
        $bw.Write([Byte]$dim)            # height
        $bw.Write([Byte]0)               # color count
        $bw.Write([Byte]0)               # reserved
        $bw.Write([UInt16]1)             # planes
        $bw.Write([UInt16]32)            # bit count
        $bw.Write([UInt32]$entry.Bytes.Length)
        $bw.Write([UInt32]$offset)
        $offset += $entry.Bytes.Length
    }

    foreach ($entry in $pngs) { $bw.Write($entry.Bytes) }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

$info = Get-Item -LiteralPath $OutputPath
Write-Host ("已生成图标：{0}（{1} 字节，尺寸 {2}）" -f $info.FullName, $info.Length, ($sizes -join '/')) -ForegroundColor Green
