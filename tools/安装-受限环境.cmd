@echo off
chcp 65001 >nul
setlocal EnableExtensions
title 通讯测试工具 安装（受限环境）
rem ============================================================================
rem  受限环境安装启动器
rem  用途：有些电脑的 %TEMP% 对提权进程不可写，安装包启动时会报
rem        「安装程序无法创建目录 ...\Temp\is-XXXX.tmp。错误 5：拒绝访问。」
rem  做法：先提权，再把 TEMP/TMP 指向本机可写目录，最后启动同目录下的安装包。
rem        安装包继承这个可写的 TEMP，解包阶段就不会被拒。
rem  用法：把本文件与「通讯测试工具-x.y.z-安装包.exe」放在同一目录，双击本文件。
rem ============================================================================

rem ---- 1. 找到同目录下的安装包 ------------------------------------------------
set "SETUP="
for %%F in ("%~dp0*安装包*.exe") do set "SETUP=%%~fF"
if not defined SETUP (
    echo [错误] 没有在本目录找到安装包（文件名应包含「安装包」并以 .exe 结尾）。
    echo         当前目录：%~dp0
    echo.
    pause
    exit /b 1
)

rem ---- 2. 自提权（安装包要求管理员；先提权就不会在设置 TEMP 之后再次弹 UAC）----
rem 用"是否为高完整性级别"判断，避免依赖 net session（部分系统上该服务被禁用会误判成未提权）。
whoami /groups | find "S-1-16-12288" >nul 2>&1
if errorlevel 1 (
    echo 正在请求管理员权限……
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

rem ---- 3. 选一个确实可写的临时目录 --------------------------------------------
set "TMPROOT=%LOCALAPPDATA%\CommTestTool\install-temp"
if not exist "%TMPROOT%" mkdir "%TMPROOT%" 2>nul
if not exist "%TMPROOT%" set "TMPROOT=%~dp0install-temp"
if not exist "%TMPROOT%" mkdir "%TMPROOT%" 2>nul

mkdir "%TMPROOT%\wtest" 2>nul
if not exist "%TMPROOT%\wtest" (
    echo [错误] 找不到可写的临时目录，安装无法继续。
    echo         已尝试：%LOCALAPPDATA%\CommTestTool\install-temp
    echo         请手工指定一个可写目录后重试，例如在命令行执行：
    echo             set TEMP=D:\Temp
    echo             set TMP=D:\Temp
    echo             "%SETUP%"
    echo.
    echo   另一个选择：改用便携包（解压即用，不需要临时目录解包）。
    pause
    exit /b 1
)
rd "%TMPROOT%\wtest" 2>nul

set "TEMP=%TMPROOT%"
set "TMP=%TMPROOT%"
echo 安装包：%SETUP%
echo 临时目录：%TEMP%
echo.

rem ---- 4. 启动安装（已提权，安装包不会再弹 UAC）------------------------------
start /wait "" "%SETUP%"
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo 安装完成。
) else (
    echo 安装程序结束，退出码 %RC%。
    echo 若仍是「错误 5」，请把上面的临时目录路径和退出码一起反馈。
)
echo.
pause
exit /b %RC%
