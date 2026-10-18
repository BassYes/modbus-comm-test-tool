<#
.SYNOPSIS
    清除文件或目录的 MOTW（Mark of the Web / Zone.Identifier）标记。

.DESCRIPTION
    “打开文件 - 安全警告（无法验证发布者）”和“Windows 已保护你的电脑（SmartScreen）”
    这两个弹窗的直接触发条件，就是文件带了 Zone.Identifier 标记（俗称“来自 Internet 区域”）。

    编译产物本身不会带标记；只有当 exe 经过“打包 → 网盘/微信/邮件 → 下载 → 解压”
    这条链路时，标记才会被写回文件。所以“我明明自己写的程序却弹窗”，
    绝大多数情况是：你运行的那份 exe 已经被搬过一次了。

    本脚本把指定目录下所有文件的标记清掉（等价于资源管理器里“属性 → 解除锁定”）。
    对没有标记的文件是无害的空操作。

.PARAMETER Path
    要处理的文件或目录（目录会递归）。

.PARAMETER Quiet
    只输出汇总，不逐个列出文件。

.EXAMPLE
    .\Unblock-Build.ps1 -Path ..\src\Comm.App.Wpf\bin\Debug

.EXAMPLE
    .\Unblock-Build.ps1 -Path ..\artifacts -Quiet

.NOTES
    对外分发时不要依赖本脚本：别人的机器上你没有机会先跑它。
    给别人用的版本请走“代码签名”，见 README.md。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromPipeline = $true)]
    [string[]] $Path,

    [switch] $Quiet
)

begin {
    $removed = [System.Collections.Generic.List[string]]::new()
    $scanned = 0
}

process {
    foreach ($p in $Path) {
        if (-not (Test-Path -LiteralPath $p)) {
            Write-Warning "路径不存在，已跳过：$p"
            continue
        }
        $item = Get-Item -LiteralPath $p
        $files = if ($item.PSIsContainer) {
            Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Force -ErrorAction SilentlyContinue
        } else {
            @($item)
        }

        foreach ($f in $files) {
            $scanned++
            $mark = Get-Item -LiteralPath $f.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue
            if ($mark) {
                Unblock-File -LiteralPath $f.FullName -ErrorAction SilentlyContinue
                $removed.Add($f.FullName)
            }
        }
    }
}

end {
    foreach ($r in $removed) {
        if (-not $Quiet) { Write-Host "  已解除锁定: $r" -ForegroundColor DarkGray }
    }
    Write-Host ""
    Write-Host ("扫描 {0} 个文件，解除锁定 {1} 个。" -f $scanned, $removed.Count) -ForegroundColor Green
    if ($removed.Count -gt 0) {
        Write-Host "提示：这些文件此前带 Internet 区域标记，运行时会触发“安全警告/SmartScreen”。" -ForegroundColor Yellow
        Write-Host "      如果它们是对外分发的产物，请改用签名（见 tools\README.md）。" -ForegroundColor Yellow
    }
}
