#!/bin/bash
# DNS Core Server - 容器镜像构建脚本 (Linux/Mac)
# 自动适配 Docker 或 Podman

set -e  # 遇到错误立即退出

# 颜色输出
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# 引入容器引擎探测
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/container-engine.sh
source "${SCRIPT_DIR}/scripts/container-engine.sh"

# 默认配置
IMAGE_NAME="dns-core-server"
IMAGE_TAG="latest"
REGISTRY=""
PUSH=false

# 显示帮助信息
show_help() {
    cat << EOF
用法: $0 [选项]

选项:
  -t, --tag TAG           指定镜像标签 (默认: latest)
  -r, --registry REGISTRY 指定镜像仓库前缀 (例如: docker.io/username)
  -p, --push              构建后自动推送到镜像仓库
  -h, --help              显示此帮助信息

环境变量:
  CONTAINER_ENGINE        强制指定容器引擎 (podman 或 docker)
                          默认自动探测：优先 docker，其次 podman

示例:
  # 仅构建镜像
  $0

  # 构建并指定标签
  $0 -t v1.0.0

  # 构建并推送到 Docker Hub
  $0 -r docker.io/username -t v1.0.0 --push

  # 构建并推送到私有仓库
  $0 -r registry.example.com/myproject -t latest --push

示例（指定引擎）:
  # 强制使用 podman 构建
  CONTAINER_ENGINE=podman $0 -t v1.0.0

注意:
  - 推送前需要先登录: <engine> login [registry]
  - 如果使用 --push 但未指定 -r，将跳过推送步骤

EOF
    exit 0
}

# 解析命令行参数
while [[ $# -gt 0 ]]; do
    case $1 in
        -t|--tag)
            IMAGE_TAG="$2"
            shift 2
            ;;
        -r|--registry)
            REGISTRY="$2/"
            shift 2
            ;;
        -p|--push)
            PUSH=true
            shift
            ;;
        -h|--help)
            show_help
            ;;
        *)
            echo -e "${RED}[错误]${NC} 未知参数: $1"
            echo "使用 --help 查看帮助信息"
            exit 1
            ;;
    esac
done

# 探测容器引擎（失败时给出安装指引并退出）
detect_engine || exit 1

# 打印构建信息
echo -e "${BLUE}========================================"
echo "DNS Core Server - 容器镜像构建"
echo -e "========================================${NC}"
echo ""
print_engine_info
echo -e "${YELLOW}镜像名称:${NC} ${REGISTRY}${IMAGE_NAME}:${IMAGE_TAG}"
echo -e "${YELLOW}构建时间:${NC} $(date '+%Y-%m-%d %H:%M:%S')"
echo ""

# 检查构建文件是否存在（Dockerfile 或 Containerfile）
CONTAINERFILE="$(resolve_containerfile)"
if [ -z "$CONTAINERFILE" ]; then
    echo -e "${RED}[错误]${NC} 未找到 Dockerfile 或 Containerfile！"
    echo "请确保在项目根目录下运行此脚本。"
    exit 1
fi

# 构建镜像
echo -e "${GREEN}[步骤 1/3]${NC} 开始构建镜像（使用 ${ENGINE_NAME}, ${CONTAINERFILE}）..."
echo ""

# shellcheck disable=SC2046  # 需要按空格拆分为独立参数
"$ENGINE" build \
    $(engine_build_args) \
    --tag "${REGISTRY}${IMAGE_NAME}:${IMAGE_TAG}" \
    --tag "${REGISTRY}${IMAGE_NAME}:latest" \
    --build-arg BUILD_DATE="$(date -u +'%Y-%m-%dT%H:%M:%SZ')" \
    --build-arg VERSION="${IMAGE_TAG}" \
    --file "$CONTAINERFILE" \
    .

echo ""
echo -e "${GREEN}[步骤 2/3]${NC} 镜像构建成功！"
echo ""

# 显示镜像信息
echo -e "${GREEN}[步骤 3/3]${NC} 镜像信息:"
"$ENGINE" images "${REGISTRY}${IMAGE_NAME}"
echo ""

# 推送镜像到仓库
if [ "$PUSH" = true ]; then
    if [ -z "$REGISTRY" ]; then
        echo -e "${YELLOW}[警告]${NC} 未指定镜像仓库（-r/--registry），跳过推送步骤"
        echo "提示: 使用 -r registry.example.com/username 指定仓库地址"
        echo ""
    else
        echo -e "${GREEN}[步骤 4/4]${NC} 正在推送镜像到仓库..."
        echo ""

        # 推送指定标签
        echo -e "${YELLOW}推送:${NC} ${REGISTRY}${IMAGE_NAME}:${IMAGE_TAG}"
        if ! "$ENGINE" push "${REGISTRY}${IMAGE_NAME}:${IMAGE_TAG}"; then
            echo ""
            echo -e "${RED}[错误]${NC} 镜像推送失败！"
            echo "请确保:"
            echo "  1. 已登录到镜像仓库: ${ENGINE} login ${REGISTRY%/}"
            echo "  2. 有推送权限"
            echo "  3. 网络连接正常"
            exit 1
        fi

        # 如果 TAG 不是 latest，也推送 latest 标签
        if [ "$IMAGE_TAG" != "latest" ]; then
            echo -e "${YELLOW}推送:${NC} ${REGISTRY}${IMAGE_NAME}:latest"
            "$ENGINE" push "${REGISTRY}${IMAGE_NAME}:latest"
        fi

        echo ""
        echo -e "${GREEN}[成功]${NC} 镜像已成功推送到仓库！"
        echo ""
    fi
fi

echo ""
echo -e "${BLUE}========================================"
echo "构建完成！"
echo -e "========================================${NC}"
echo ""
echo -e "${YELLOW}镜像标签:${NC} ${REGISTRY}${IMAGE_NAME}:${IMAGE_TAG}"
echo ""
echo -e "${YELLOW}运行容器:${NC}"
echo "  ${ENGINE} run -d -p 53:53/udp -p 53:53/tcp -p 5000:5000 --name dns-core ${REGISTRY}${IMAGE_NAME}:${IMAGE_TAG}"
echo ""
if has_compose; then
    echo -e "${YELLOW}或使用 compose:${NC}"
    echo "  $(compose_display) up -d"
    echo ""
fi
warn_if_rootless_privileged_port 53
