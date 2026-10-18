#!/usr/bin/env bash
# 依次打开各功能页并截图，用于人工核对界面（开发自检用，不参与构建）。
# 用法：bash docs/capture-pages.sh
set -e
APP_DIR="$(cd "$(dirname "$0")/.." && pwd)/src/Comm.App.Wpf/bin/Debug/net8.0-windows"
OUT_DIR="$(cd "$(dirname "$0")" && pwd)"
TMP="C:\\Users\\$USERNAME\\AppData\\Local\\Temp"

capture() {
  local page="$1"
  local name="$2"
  (cd "$APP_DIR" && cmd //c start "" CommTestTool.exe --page="$page")
  sleep 9
  powershell.exe -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; \$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; \$bmp=New-Object System.Drawing.Bitmap \$b.Width,\$b.Height; \$g=[System.Drawing.Graphics]::FromImage(\$bmp); \$g.CopyFromScreen(\$b.Location,[System.Drawing.Point]::Empty,\$b.Size); \$bmp.Save('$TMP\\shot-$name.png')" > /dev/null
  cp "/tmp/shot-$name.png" "$OUT_DIR/screenshot-$name.png"
  taskkill //IM CommTestTool.exe //F > /dev/null 2>&1 || true
  sleep 1
  rm -f "$APP_DIR/data/.running"
  echo "captured $name"
}

capture 1 connection
capture 2 device-scan
capture 3 data-scan
capture 4 register-locator
capture 5 watch-table
capture 6 manual-send
capture 7 monitor
capture 8 network-tools
capture 9 lan-scan
capture 10 settings
