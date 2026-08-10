#!/bin/bash
# 容器引擎探测（Linux/Mac/WSL/Git Bash）
#
# 由各 docker-*.sh 脚本 source 引入，导出以下变量：
#   ENGINE         容器引擎命令（podman 或 docker）
#   ENGINE_NAME    显示名（Podman / Docker）
#   COMPOSE        compose 命令（数组形式，可能是多词，如 "podman compose"）
#   ENGINE_ROOTLESS  podman 是否 rootless 运行（true/false，docker 恒为 false）
#
# 优先级：环境变量 CONTAINER_ENGINE > docker > podman
# 之所以 docker 优先：已装 docker 的机器上通常是有意使用 docker；
# 只有 podman 的机器上探测会自动落到 podman。

# 颜色（若调用方未定义则补上）
: "${RED:=\033[0;31m}"
: "${GREEN:=\033[0;32m}"
: "${YELLOW:=\033[1;33m}"
: "${BLUE:=\033[0;34m}"
: "${NC:=\033[0m}"

ENGINE=""
ENGINE_NAME=""
COMPOSE_ARGS=()
ENGINE_ROOTLESS="false"

detect_engine() {
    # 1) 显式指定
    if [ -n "${CONTAINER_ENGINE:-}" ]; then
        if ! command -v "$CONTAINER_ENGINE" &> /dev/null; then
            echo -e "${RED}[错误]${NC} CONTAINER_ENGINE 指定为 '$CONTAINER_ENGINE'，但未找到该命令"
            return 1
        fi
        ENGINE="$CONTAINER_ENGINE"
    # 2) 自动探测
    elif command -v docker &> /dev/null; then
        ENGINE="docker"
    elif command -v podman &> /dev/null; then
        ENGINE="podman"
    else
        echo -e "${RED}[错误]${NC} 未找到容器引擎（docker 或 podman）"
        echo ""
        echo "请安装其中之一："
        echo "  Docker: https://docs.docker.com/get-docker/"
        echo "  Podman: https://podman.io/getting-started/installation"
        echo ""
        echo "或通过环境变量指定：CONTAINER_ENGINE=podman $0"
        return 1
    fi

    case "$ENGINE" in
        podman) ENGINE_NAME="Podman" ;;
        docker) ENGINE_NAME="Docker" ;;
        *)      ENGINE_NAME="$ENGINE" ;;
    esac

    detect_compose
    detect_rootless
    return 0
}

# 探测可用的 compose 命令。
# podman：优先内置子命令 `podman compose`（它会转调外部 provider），
#         其次 podman-compose（独立的 Python 实现）。
# docker：优先 `docker compose`（v2 插件），其次 docker-compose（v1 独立二进制）。
detect_compose() {
    COMPOSE_ARGS=()

    if [ "$ENGINE" = "podman" ]; then
        if podman compose version &> /dev/null; then
            COMPOSE_ARGS=(podman compose)
        elif command -v podman-compose &> /dev/null; then
            COMPOSE_ARGS=(podman-compose)
        fi
    else
        if "$ENGINE" compose version &> /dev/null; then
            COMPOSE_ARGS=("$ENGINE" compose)
        elif command -v docker-compose &> /dev/null; then
            COMPOSE_ARGS=(docker-compose)
        fi
    fi
}

has_compose() {
    [ ${#COMPOSE_ARGS[@]} -gt 0 ]
}

# 执行 compose 命令
compose_cmd() {
    if ! has_compose; then
        echo -e "${RED}[错误]${NC} 未找到可用的 compose 命令"
        if [ "$ENGINE" = "podman" ]; then
            echo "请安装 podman-compose:  pip3 install podman-compose"
            echo "或安装 docker-compose 供 podman 作为外部 provider 调用"
        else
            echo "请安装 Docker Compose: https://docs.docker.com/compose/install/"
        fi
        return 1
    fi
    "${COMPOSE_ARGS[@]}" "$@"
}

compose_display() {
    if has_compose; then
        echo "${COMPOSE_ARGS[*]}"
    else
        echo "(未安装)"
    fi
}

# podman rootless 下无法绑定 <1024 的特权端口（DNS 需要 53）
detect_rootless() {
    if [ "$ENGINE" = "podman" ]; then
        local result
        result=$(podman info --format '{{.Host.Security.Rootless}}' 2>/dev/null || echo "")
        [ "$result" = "true" ] && ENGINE_ROOTLESS="true"
    fi
}

# rootless podman 绑定 53 端口会失败，提前给出可执行的处置建议
warn_if_rootless_privileged_port() {
    local port="${1:-53}"

    if [ "$ENGINE_ROOTLESS" != "true" ] || [ "$port" -ge 1024 ]; then
        return 0
    fi

    echo -e "${YELLOW}[注意]${NC} 检测到 Podman 以 rootless 模式运行，无法绑定特权端口 ${port}"
    echo ""
    echo "可选处置方式："
    echo "  1) 放宽内核限制（推荐，需 root 一次性设置）:"
    echo "     sudo sysctl -w net.ipv4.ip_unprivileged_port_start=53"
    echo "     持久化: echo 'net.ipv4.ip_unprivileged_port_start=53' | sudo tee -a /etc/sysctl.conf"
    echo ""
    echo "  2) 映射到高端口，再由宿主转发:"
    echo "     DNS_PORT=5353 $0"
    echo ""
    echo "  3) 使用 rootful podman:"
    echo "     sudo -E $0"
    echo ""
}

# 打印引擎信息
print_engine_info() {
    echo -e "${YELLOW}容器引擎:${NC} ${ENGINE_NAME} ($(${ENGINE} --version 2>/dev/null | head -1))"
    echo -e "${YELLOW}Compose:${NC} $(compose_display)"
    if [ "$ENGINE_ROOTLESS" = "true" ]; then
        echo -e "${YELLOW}运行模式:${NC} rootless"
    fi
}

# 引擎相关的额外 build 参数。
#
# podman 默认产出 OCI 格式镜像，而 HEALTHCHECK 是 Docker 镜像格式的扩展，
# OCI 格式下会被直接丢弃并只给一条 warning。要保留健康检查必须显式指定
# --format docker。
engine_build_args() {
    if [ "$ENGINE" = "podman" ]; then
        echo "--format docker"
    fi
}

# 构建文件名：podman 惯用 Containerfile，docker 用 Dockerfile。
# 两者都能读对方的文件名，这里择优返回已存在的那个。
resolve_containerfile() {
    if [ "$ENGINE" = "podman" ] && [ -f "Containerfile" ]; then
        echo "Containerfile"
    elif [ -f "Dockerfile" ]; then
        echo "Dockerfile"
    elif [ -f "Containerfile" ]; then
        echo "Containerfile"
    else
        echo ""
    fi
}
