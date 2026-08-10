@echo off
REM 切换到 UTF-8 代码页：本文件以 UTF-8 保存，
REM 若按系统 ANSI 代码页(中文 Windows 为 GBK)解析，中文会乱码并破坏 if/( 结构。
chcp 65001 >nul 2>&1
REM 容器引擎探测（Windows）
REM
REM 由各 docker-*.bat 脚本 call 引入，设置以下变量：
REM   ENGINE         容器引擎命令（podman 或 docker）
REM   ENGINE_NAME    显示名（Podman / Docker）
REM   COMPOSE        compose 命令（可能含空格，如 "podman compose"）
REM   ENGINE_FOUND   1 表示探测成功，0 表示失败
REM
REM 优先级：环境变量 CONTAINER_ENGINE > docker > podman

set "ENGINE="
set "ENGINE_NAME="
set "COMPOSE="
set "ENGINE_FOUND=0"

REM 1) 显式指定
if defined CONTAINER_ENGINE (
    where "%CONTAINER_ENGINE%" >nul 2>&1
    if errorlevel 1 (
        echo [错误] CONTAINER_ENGINE 指定为 "%CONTAINER_ENGINE%"，但未找到该命令
        exit /b 1
    )
    set "ENGINE=%CONTAINER_ENGINE%"
    goto :engine_resolved
)

REM 2) 自动探测：docker 优先，其次 podman
where docker >nul 2>&1
if not errorlevel 1 (
    set "ENGINE=docker"
    goto :engine_resolved
)

where podman >nul 2>&1
if not errorlevel 1 (
    set "ENGINE=podman"
    goto :engine_resolved
)

echo [错误] 未找到容器引擎（docker 或 podman）
echo.
echo 请安装其中之一：
echo   Docker Desktop: https://docs.docker.com/desktop/install/windows-install/
echo   Podman Desktop: https://podman-desktop.io/downloads
echo.
echo 或通过环境变量指定：set CONTAINER_ENGINE=podman
exit /b 1

:engine_resolved
if /i "%ENGINE%"=="podman" (
    set "ENGINE_NAME=Podman"
) else if /i "%ENGINE%"=="docker" (
    set "ENGINE_NAME=Docker"
) else (
    set "ENGINE_NAME=%ENGINE%"
)

REM 探测 compose：
REM podman 优先内置子命令 podman compose（会转调外部 provider），其次 podman-compose
REM docker 优先 docker compose（v2 插件），其次 docker-compose（v1 独立二进制）
if /i "%ENGINE%"=="podman" (
    podman compose version >nul 2>&1
    if not errorlevel 1 (
        set "COMPOSE=podman compose"
    ) else (
        where podman-compose >nul 2>&1
        if not errorlevel 1 set "COMPOSE=podman-compose"
    )
) else (
    %ENGINE% compose version >nul 2>&1
    if not errorlevel 1 (
        set "COMPOSE=%ENGINE% compose"
    ) else (
        where docker-compose >nul 2>&1
        if not errorlevel 1 set "COMPOSE=docker-compose"
    )
)

REM podman 默认产出 OCI 格式镜像，而 HEALTHCHECK 属于 Docker 镜像格式扩展，
REM OCI 格式下会被直接丢弃并只给一条 warning。保留健康检查须显式指定 --format docker。
set "ENGINE_BUILD_ARGS="
if /i "%ENGINE%"=="podman" set "ENGINE_BUILD_ARGS=--format docker"

set "ENGINE_FOUND=1"
exit /b 0
