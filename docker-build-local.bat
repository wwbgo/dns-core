@echo off
REM 切换到 UTF-8 代码页：本文件以 UTF-8 保存，
REM 若按系统 ANSI 代码页(中文 Windows 为 GBK)解析，中文会乱码并破坏 if/( 结构。
chcp 65001 >nul 2>&1
REM DNS Core Server - Docker Build (快捷方式)
REM 此脚本调用 docker-build.bat

REM 依次构建并推送 latest 与 1.1.0 两个标签。
REM 每步都检查退出码：call 失败不会自动中止后续语句，
REM 若不检查，第一步失败时第二步仍会执行，错误会重复出现且掩盖真正的失败点。
call "%~dp0docker-build.bat" -t latest -r docker.flexem.com/flexem -p %*
if errorlevel 1 (
    echo.
    echo [错误] latest 标签构建/推送失败，已中止后续步骤。
    pause
    exit /b 1
)

call "%~dp0docker-build.bat" -t 1.1.0 -r docker.flexem.com/flexem -p %*
if errorlevel 1 (
    echo.
    echo [错误] 1.1.0 标签构建/推送失败。
    pause
    exit /b 1
)

echo.
echo [完成] latest 与 1.1.0 两个标签均已构建并推送。
pause
