@echo off
REM DNS Core Server - Docker 运行脚本 (Windows)
REM 用于快速启动、停止和管理 DNS 服务器容器

setlocal enabledelayedexpansion

REM 设置容器名称和镜像
set CONTAINER_NAME=dns-core-server
set IMAGE_NAME=dns-core-server:latest
set DNS_PORT=53
set WEB_PORT=5000

REM 显示主菜单
:menu
cls
echo ========================================
echo DNS Core Server - Docker 管理
echo ========================================
echo.
echo 请选择操作:
echo.
echo   1. 启动容器 (start)
echo   2. 停止容器 (stop)
echo   3. 重启容器 (restart)
echo   4. 查看状态 (status)
echo   5. 查看日志 (logs)
echo   6. 进入容器 (exec)
echo   7. 删除容器 (remove)
echo   8. 使用 Docker Compose 启动
echo   9. 使用 Docker Compose 停止
echo   0. 退出
echo.
set /p choice="请输入选项 (0-9): "

if "%choice%"=="1" goto :start_container
if "%choice%"=="2" goto :stop_container
if "%choice%"=="3" goto :restart_container
if "%choice%"=="4" goto :show_status
if "%choice%"=="5" goto :show_logs
if "%choice%"=="6" goto :exec_container
if "%choice%"=="7" goto :remove_container
if "%choice%"=="8" goto :compose_up
if "%choice%"=="9" goto :compose_down
if "%choice%"=="0" goto :end
goto :menu

:start_container
echo.
echo [启动容器] %CONTAINER_NAME%
echo.

REM 检查容器是否已存在
docker ps -a --filter "name=%CONTAINER_NAME%" --format "{{.Names}}" | findstr /x "%CONTAINER_NAME%" >nul
if %errorlevel% equ 0 (
    echo 容器已存在，正在启动...
    docker start %CONTAINER_NAME%
) else (
    echo 创建并启动新容器...
    docker run -d ^
        --name %CONTAINER_NAME% ^
        -p %DNS_PORT%:53/udp ^
        -p %WEB_PORT%:5000 ^
        --restart unless-stopped ^
        %IMAGE_NAME%
)

if %errorlevel% equ 0 (
    echo.
    echo [成功] 容器已启动！
    echo.
    echo Web 管理界面: http://localhost:%WEB_PORT%
    echo Swagger API 文档: http://localhost:%WEB_PORT%/swagger
    echo DNS 服务端口: %DNS_PORT%/UDP
) else (
    echo.
    echo [错误] 容器启动失败！
)
echo.
pause
goto :menu

:stop_container
echo.
echo [停止容器] %CONTAINER_NAME%
echo.
docker stop %CONTAINER_NAME%
if %errorlevel% equ 0 (
    echo [成功] 容器已停止！
) else (
    echo [错误] 容器停止失败！
)
echo.
pause
goto :menu

:restart_container
echo.
echo [重启容器] %CONTAINER_NAME%
echo.
docker restart %CONTAINER_NAME%
if %errorlevel% equ 0 (
    echo [成功] 容器已重启！
) else (
    echo [错误] 容器重启失败！
)
echo.
pause
goto :menu

:show_status
echo.
echo [容器状态]
echo.
docker ps -a --filter "name=%CONTAINER_NAME%"
echo.
echo [容器详细信息]
docker inspect %CONTAINER_NAME% --format "{{.State.Status}}" 2>nul
if %errorlevel% neq 0 (
    echo 容器不存在
) else (
    docker inspect %CONTAINER_NAME% --format "启动时间: {{.State.StartedAt}}"
    docker inspect %CONTAINER_NAME% --format "运行状态: {{.State.Status}}"
)
echo.
pause
goto :menu

:show_logs
echo.
echo [容器日志] (按 Ctrl+C 退出)
echo.
docker logs -f --tail 50 %CONTAINER_NAME%
goto :menu

:exec_container
echo.
echo [进入容器] %CONTAINER_NAME%
echo.
docker exec -it %CONTAINER_NAME% /bin/bash
if %errorlevel% neq 0 (
    echo 尝试使用 sh...
    docker exec -it %CONTAINER_NAME% /bin/sh
)
goto :menu

:remove_container
echo.
echo [删除容器] %CONTAINER_NAME%
echo.
set /p confirm="确认删除容器? (y/n): "
if /i "%confirm%"=="y" (
    docker stop %CONTAINER_NAME% 2>nul
    docker rm %CONTAINER_NAME%
    if %errorlevel% equ 0 (
        echo [成功] 容器已删除！
    ) else (
        echo [错误] 容器删除失败！
    )
) else (
    echo 已取消删除操作
)
echo.
pause
goto :menu

:compose_up
echo.
echo [Docker Compose] 启动服务
echo.
docker-compose up -d
if %errorlevel% equ 0 (
    echo.
    echo [成功] 服务已启动！
    echo.
    echo Web 管理界面: http://localhost:%WEB_PORT%
    echo Swagger API 文档: http://localhost:%WEB_PORT%/swagger
) else (
    echo [错误] 服务启动失败！
)
echo.
pause
goto :menu

:compose_down
echo.
echo [Docker Compose] 停止服务
echo.
docker-compose down
if %errorlevel% equ 0 (
    echo [成功] 服务已停止！
) else (
    echo [错误] 服务停止失败！
)
echo.
pause
goto :menu

:end
echo.
echo 再见！
echo.
endlocal
