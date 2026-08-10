@echo off
REM 切换到 UTF-8 代码页：本文件以 UTF-8 保存，
REM 若按系统 ANSI 代码页(中文 Windows 为 GBK)解析，中文会乱码并破坏 if/( 结构。
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion

REM DNS Core Server - Docker 构建脚本 (Windows)
REM 用于构建 Docker 镜像

echo ========================================
echo DNS Core Server - Docker 镜像构建
echo ========================================
echo.

REM 设置默认值
set "IMAGE_NAME=dns-core-server"
set "IMAGE_TAG=latest"
set "REGISTRY="
set "PUSH=false"

REM 解析命令行参数
:parse_args
if "%~1"=="" goto :build
if /i "%~1"=="-t" (
    set "IMAGE_TAG=%~2"
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="--tag" (
    set "IMAGE_TAG=%~2"
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="-r" (
    set "REGISTRY=%~2/"
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="--registry" (
    set "REGISTRY=%~2/"
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="-p" (
    set "PUSH=true"
    shift
    goto :parse_args
)
if /i "%~1"=="--push" (
    set "PUSH=true"
    shift
    goto :parse_args
)
if /i "%~1"=="--help" goto :show_help
if /i "%~1"=="-h" goto :show_help
echo [警告] 未知参数: %~1
shift
goto :parse_args

:build
REM 组合完整镜像名称
if defined REGISTRY (
    set "FULL_IMAGE_NAME=%REGISTRY%%IMAGE_NAME%"
) else (
    set "FULL_IMAGE_NAME=%IMAGE_NAME%"
)

REM 显示构建信息
echo 镜像名称: !FULL_IMAGE_NAME!:%IMAGE_TAG%
echo 构建时间: %date% %time%
echo.

REM 探测容器引擎（docker 或 podman）
call "%~dp0scripts\container-engine.bat"
if errorlevel 1 exit /b 1

echo 容器引擎: %ENGINE_NAME%
echo.

REM 检查构建文件是否存在（Dockerfile 或 Containerfile）
set "CONTAINERFILE="
if exist "Dockerfile" set "CONTAINERFILE=Dockerfile"
if not defined CONTAINERFILE if exist "Containerfile" set "CONTAINERFILE=Containerfile"
if not defined CONTAINERFILE (
    echo [错误] 未找到 Dockerfile 或 Containerfile！
    echo 请确保在项目根目录下运行此脚本。
    echo 当前目录: %CD%
    exit /b 1
)

REM 生成 ISO 8601 UTC 时间戳，写入 OCI 标签 org.opencontainers.image.created。
REM 不用 %date%/%time%：其格式随系统区域设置变化，且是本地时间而非 UTC。
for /f %%i in ('powershell -NoProfile -Command "(Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')"') do set "BUILD_DATE=%%i"
if not defined BUILD_DATE set "BUILD_DATE=unknown"

REM 构建镜像
echo [步骤 1/3] 开始构建镜像（使用 %ENGINE_NAME%, !CONTAINERFILE!）...
echo.

%ENGINE% build %ENGINE_BUILD_ARGS% --tag "!FULL_IMAGE_NAME!:%IMAGE_TAG%" --tag "!FULL_IMAGE_NAME!:latest" --build-arg BUILD_DATE=!BUILD_DATE! --build-arg VERSION=%IMAGE_TAG% --file "!CONTAINERFILE!" .

if %errorlevel% neq 0 (
    echo.
    echo [错误] 镜像构建失败！
    echo 错误代码: %errorlevel%
    exit /b 1
)

echo.
echo [步骤 2/3] 镜像构建成功！
echo.

REM 显示镜像信息
echo [步骤 3/3] 镜像信息:
%ENGINE% images "!FULL_IMAGE_NAME!"
echo.

REM 推送镜像到仓库
if /i "!PUSH!"=="true" (
    if not defined REGISTRY (
        echo [警告] 未指定镜像仓库（-r/--registry），跳过推送步骤
        echo 提示: 使用 -r registry.example.com/username 指定仓库地址
        echo.
    ) else (
        echo [步骤 4/4] 正在推送镜像到仓库...
        echo.

        REM 推送指定标签
        echo 推送: !FULL_IMAGE_NAME!:!IMAGE_TAG!
        %ENGINE% push "!FULL_IMAGE_NAME!:!IMAGE_TAG!"

        if %errorlevel% neq 0 (
            echo.
            echo [错误] 镜像推送失败！
            echo 请确保:
            echo   1. 已登录到镜像仓库: %ENGINE% login
            echo   2. 有推送权限
            echo   3. 网络连接正常
            exit /b 1
        )

        REM 如果 TAG 不是 latest，也推送 latest 标签
        if not "!IMAGE_TAG!"=="latest" (
            echo 推送: !FULL_IMAGE_NAME!:latest
            %ENGINE% push "!FULL_IMAGE_NAME!:latest"
        )

        echo.
        echo [成功] 镜像已成功推送到仓库！
    )
)

echo.
echo ========================================
echo 构建完成！
echo ========================================
echo.
echo 镜像标签: !FULL_IMAGE_NAME!:%IMAGE_TAG%
echo.
echo 运行容器:
echo   %ENGINE% run -d -p 53:53/udp -p 53:53/tcp -p 5000:5000 --name dns-core !FULL_IMAGE_NAME!:%IMAGE_TAG%
echo.
echo 或使用 compose:
echo   %COMPOSE% up -d
echo.
echo 访问地址:
echo   Web 管理界面: http://localhost:5000
echo   Swagger API:  http://localhost:5000/swagger
echo.
goto :end

:show_help
echo 用法: docker-build.bat [选项]
echo.
echo 选项:
echo   -t, --tag TAG           指定镜像标签 (默认: latest)
echo   -r, --registry REGISTRY 指定镜像仓库前缀 (例如: docker.io/username)
echo   -p, --push              构建后自动推送到镜像仓库
echo   -h, --help              显示此帮助信息
echo.
echo 环境变量:
echo   CONTAINER_ENGINE        强制指定容器引擎 (podman 或 docker)
echo                           默认自动探测：优先 docker，其次 podman
echo.
echo 示例:
echo   # 仅构建镜像
echo   docker-build.bat
echo.
echo   # 构建并指定标签
echo   docker-build.bat -t v1.0.0
echo.
echo   # 构建并推送到 Docker Hub
echo   docker-build.bat -r docker.io/username -t v1.0.0 --push
echo.
echo   # 构建并推送到私有仓库
echo   docker-build.bat -r registry.example.com/myproject -t latest --push
echo.
echo 注意:
echo   - 推送前需要先登录: %ENGINE% login [registry]
echo   - 如果使用 --push 但未指定 -r，将跳过推送步骤
echo.
exit /b 0

:end
endlocal
