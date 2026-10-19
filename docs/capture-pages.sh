 !/usr/bin/env bash
  依次打开各功能页并截图，用于人工核对界面（开发自检用，不参与构建）。
 
  用法：
    bash docs/capture-pages.sh                   默认：最大化逐页截图（12 个功能页：1-8 会话页，9-12 全局页）
    bash docs/capture-pages.sh --min             900×480 最小窗口逐页截图（B-01 验收）
    bash docs/capture-pages.sh --pages 2,8       只截指定页
 
  DPI 核对（100% / 150% / 200%）无法在脚本内切换（需要重新登录会话），
  请按 内部说明 的“干净机冒烟清单”手工把系统缩放改为 150%/200%
  后再运行本脚本，并把结果作为发版前的人工核对项（规划 22.4 第 5 点）。
set -e

MODE="full"
PAGES="1 2 3 4 5 6 7 8 9 10 11 12"
while [ $  -gt 0 ]; do
  case "$1" in
    --min) MODE="min"; shift ;;
    --pages) PAGES="$(echo "$2" | tr ',' ' ')"; shift 2 ;;
    *) echo "未知参数：$1"; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_DIR="$ROOT/src/Comm.App.Wpf/bin/Debug/net8.0-windows"
OUT_DIR="$ROOT/docs"
SUFFIX=""
[ "$MODE" = "min" ] && SUFFIX="-min900x480"

capture() {
  local page="$1"
  local name="$2"
  (cd "$APP_DIR" && cmd //c start "" CommTestTool.exe --page="$page")
  sleep 9

  if [ "$MODE" = "min" ]; then
      把主窗口改为 900×480（B-01：最小可用尺寸），再截图
    powershell.exe -NoProfile -Command "
      Add-Type -Namespace Win -Name Api -MemberDefinition '
        [DllImport(\"user32.dll\")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(IntPtr hWnd);
      ' -ErrorAction SilentlyContinue
      \$p = Get-Process CommTestTool -ErrorAction SilentlyContinue | Select-Object -First 1
      if (\$p) {
        [Win.Api]::MoveWindow(\$p.MainWindowHandle, 40, 40, 900, 480, \$true) | Out-Null
        [Win.Api]::SetForegroundWindow(\$p.MainWindowHandle) | Out-Null
      }
      Start-Sleep -Milliseconds 1200
      Add-Type -AssemblyName System.Windows.Forms,System.Drawing
      \$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
      \$bmp = New-Object System.Drawing.Bitmap \$b.Width, \$b.Height
      \$g = [System.Drawing.Graphics]::FromImage(\$bmp)
      \$g.CopyFromScreen(\$b.Location, [System.Drawing.Point]::Empty, \$b.Size)
      \$bmp.Save(\"\$env:TEMP\\shot-$name.png\")
    " > /dev/null
  else
    powershell.exe -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; \$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; \$bmp=New-Object System.Drawing.Bitmap \$b.Width,\$b.Height; \$g=[System.Drawing.Graphics]::FromImage(\$bmp); \$g.CopyFromScreen(\$b.Location,[System.Drawing.Point]::Empty,\$b.Size); \$bmp.Save(\"\$env:TEMP\\shot-$name.png\")" > /dev/null
  fi

  cp "$TEMP/shot-$name.png" "$OUT_DIR/screenshot-$name$SUFFIX.png" 2>/dev/null ||
    cp "/c/Users/$USERNAME/AppData/Local/Temp/shot-$name.png" "$OUT_DIR/screenshot-$name$SUFFIX.png"

  taskkill //IM CommTestTool.exe //F > /dev/null 2>&1 || true
  sleep 1
  rm -f "$APP_DIR/data/.running"
  echo "captured $name$SUFFIX"
}

declare -A NAMES=(
  [1]=connection [2]=device-scan [3]=unit-id-scan [4]=data-scan [5]=register-locator [6]=watch-table
  [7]=manual-send [8]=monitor [9]=network-tools [10]=lan-scan [11]=extension-center [12]=settings )

for page in $PAGES; do
  capture "$page" "${NAMES[$page]}"
done
