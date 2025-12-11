@echo off
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

REM 检查 Docker 是否安装
where docker >nul 2>nul
if %errorlevel% neq 0 (
    echo [错误] 未找到 Docker 命令！
    echo 请先安装 Docker Desktop: https://www.docker.com/products/docker-desktop
    exit /b 1
)

REM 检查 Dockerfile 是否存在
if not exist "Dockerfile" (
    echo [错误] 未找到 Dockerfile 文件！
    echo 请确保在项目根目录下运行此脚本。
    echo 当前目录: %CD%
    exit /b 1
)

REM 构建 Docker 镜像
echo [步骤 1/3] 开始构建 Docker 镜像...
echo.

docker build --tag "!FULL_IMAGE_NAME!:%IMAGE_TAG%" --tag "!FULL_IMAGE_NAME!:latest" --file Dockerfile .

if %errorlevel% neq 0 (
    echo.
    echo [错误] Docker 镜像构建失败！
    echo 错误代码: %errorlevel%
    exit /b 1
)

echo.
echo [步骤 2/3] 镜像构建成功！
echo.

REM 显示镜像信息
echo [步骤 3/3] 镜像信息:
docker images "!FULL_IMAGE_NAME!"
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
        docker push "!FULL_IMAGE_NAME!:!IMAGE_TAG!"

        if %errorlevel% neq 0 (
            echo.
            echo [错误] 镜像推送失败！
            echo 请确保:
            echo   1. 已登录到镜像仓库: docker login
            echo   2. 有推送权限
            echo   3. 网络连接正常
            exit /b 1
        )

        REM 如果 TAG 不是 latest，也推送 latest 标签
        if not "!IMAGE_TAG!"=="latest" (
            echo 推送: !FULL_IMAGE_NAME!:latest
            docker push "!FULL_IMAGE_NAME!:latest"
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
echo   docker run -d -p 53:53/udp -p 5000:5000 --name dns-core !FULL_IMAGE_NAME!:%IMAGE_TAG%
echo.
echo 或使用 docker-compose:
echo   docker-compose up -d
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
echo   - 推送前需要先登录: docker login [registry]
echo   - 如果使用 --push 但未指定 -r，将跳过推送步骤
echo.
exit /b 0

:end
endlocal
