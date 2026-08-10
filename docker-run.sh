#!/bin/bash
# DNS Core Server - 容器运行脚本 (Linux/Mac)
# 用于快速启动、停止和管理 DNS 服务器容器
# 自动适配 Docker 或 Podman
#
# 环境变量:
#   CONTAINER_ENGINE  强制指定容器引擎 (podman 或 docker)
#   DNS_PORT          DNS 端口映射 (默认 53；rootless podman 建议改用 5353)
#   WEB_PORT          Web 管理端口 (默认 5000)

set -e

# 颜色输出
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# 引入容器引擎探测
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/container-engine.sh
source "${SCRIPT_DIR}/scripts/container-engine.sh"

detect_engine || exit 1

# 配置（端口可通过环境变量覆盖）
CONTAINER_NAME="dns-core-server"
IMAGE_NAME="dns-core-server:latest"
DNS_PORT="${DNS_PORT:-53}"
WEB_PORT="${WEB_PORT:-5000}"

# 显示菜单
show_menu() {
    clear
    echo -e "${BLUE}========================================"
    echo "DNS Core Server - 容器管理 (${ENGINE_NAME})"
    echo -e "========================================${NC}"
    echo ""
    echo "请选择操作:"
    echo ""
    echo "  1. 启动容器 (start)"
    echo "  2. 停止容器 (stop)"
    echo "  3. 重启容器 (restart)"
    echo "  4. 查看状态 (status)"
    echo "  5. 查看日志 (logs)"
    echo "  6. 进入容器 (exec)"
    echo "  7. 删除容器 (remove)"
    echo "  8. 使用 Compose 启动"
    echo "  9. 使用 Compose 停止"
    echo "  0. 退出"
    echo ""
    read -p "请输入选项 (0-9): " choice
    echo ""
}

# 启动容器
start_container() {
    echo -e "${GREEN}[启动容器]${NC} $CONTAINER_NAME"
    echo ""

    warn_if_rootless_privileged_port "$DNS_PORT"

    if "$ENGINE" ps -a --filter "name=$CONTAINER_NAME" --format "{{.Names}}" | grep -q "^${CONTAINER_NAME}$"; then
        echo "容器已存在，正在启动..."
        "$ENGINE" start "$CONTAINER_NAME"
    else
        echo "创建并启动新容器..."
        # DNS 需要同时监听 UDP 与 TCP：超过 512 字节的应答会置 TC 位，
        # 客户端随后改用 TCP 重试，只映射 UDP 会导致这类查询失败
        "$ENGINE" run -d \
            --name "$CONTAINER_NAME" \
            -p "${DNS_PORT}:53/udp" \
            -p "${DNS_PORT}:53/tcp" \
            -p "${WEB_PORT}:5000" \
            --restart unless-stopped \
            "$IMAGE_NAME"
    fi

    if [ $? -eq 0 ]; then
        echo ""
        echo -e "${GREEN}[成功]${NC} 容器已启动！"
        echo ""
        echo -e "${YELLOW}Web 管理界面:${NC} http://localhost:${WEB_PORT}"
        echo -e "${YELLOW}Swagger API 文档:${NC} http://localhost:${WEB_PORT}/swagger"
        echo -e "${YELLOW}DNS 服务端口:${NC} ${DNS_PORT}/UDP"
    else
        echo ""
        echo -e "${RED}[错误]${NC} 容器启动失败！"
    fi
}

# 停止容器
stop_container() {
    echo -e "${YELLOW}[停止容器]${NC} $CONTAINER_NAME"
    echo ""

    "$ENGINE" stop "$CONTAINER_NAME"

    if [ $? -eq 0 ]; then
        echo -e "${GREEN}[成功]${NC} 容器已停止！"
    else
        echo -e "${RED}[错误]${NC} 容器停止失败！"
    fi
}

# 重启容器
restart_container() {
    echo -e "${YELLOW}[重启容器]${NC} $CONTAINER_NAME"
    echo ""

    "$ENGINE" restart "$CONTAINER_NAME"

    if [ $? -eq 0 ]; then
        echo -e "${GREEN}[成功]${NC} 容器已重启！"
    else
        echo -e "${RED}[错误]${NC} 容器重启失败！"
    fi
}

# 查看状态
show_status() {
    echo -e "${BLUE}[容器状态]${NC}"
    echo ""
    "$ENGINE" ps -a --filter "name=$CONTAINER_NAME"
    echo ""

    echo -e "${BLUE}[容器详细信息]${NC}"
    if "$ENGINE" inspect "$CONTAINER_NAME" &>/dev/null; then
        echo -e "${YELLOW}启动时间:${NC} $("$ENGINE" inspect "$CONTAINER_NAME" --format '{{.State.StartedAt}}')"
        echo -e "${YELLOW}运行状态:${NC} $("$ENGINE" inspect "$CONTAINER_NAME" --format '{{.State.Status}}')"
    else
        echo "容器不存在"
    fi
}

# 查看日志
show_logs() {
    echo -e "${BLUE}[容器日志]${NC} (按 Ctrl+C 退出)"
    echo ""
    "$ENGINE" logs -f --tail 50 "$CONTAINER_NAME"
}

# 进入容器
exec_container() {
    echo -e "${BLUE}[进入容器]${NC} $CONTAINER_NAME"
    echo ""

    if ! "$ENGINE" exec -it "$CONTAINER_NAME" /bin/bash; then
        echo "尝试使用 sh..."
        "$ENGINE" exec -it "$CONTAINER_NAME" /bin/sh
    fi
}

# 删除容器
remove_container() {
    echo -e "${RED}[删除容器]${NC} $CONTAINER_NAME"
    echo ""

    read -p "确认删除容器? (y/n): " confirm

    if [[ "$confirm" =~ ^[Yy]$ ]]; then
        "$ENGINE" stop "$CONTAINER_NAME" 2>/dev/null || true
        "$ENGINE" rm "$CONTAINER_NAME"

        if [ $? -eq 0 ]; then
            echo -e "${GREEN}[成功]${NC} 容器已删除！"
        else
            echo -e "${RED}[错误]${NC} 容器删除失败！"
        fi
    else
        echo "已取消删除操作"
    fi
}

# 使用 Compose 启动
compose_up() {
    echo -e "${GREEN}[Compose]${NC} 启动服务 ($(compose_display))"
    echo ""

    compose_cmd up -d

    if [ $? -eq 0 ]; then
        echo ""
        echo -e "${GREEN}[成功]${NC} 服务已启动！"
        echo ""
        echo -e "${YELLOW}Web 管理界面:${NC} http://localhost:${WEB_PORT}"
        echo -e "${YELLOW}Swagger API 文档:${NC} http://localhost:${WEB_PORT}/swagger"
    else
        echo -e "${RED}[错误]${NC} 服务启动失败！"
    fi
}

# 使用 Compose 停止
compose_down() {
    echo -e "${YELLOW}[Compose]${NC} 停止服务 ($(compose_display))"
    echo ""

    compose_cmd down

    if [ $? -eq 0 ]; then
        echo -e "${GREEN}[成功]${NC} 服务已停止！"
    else
        echo -e "${RED}[错误]${NC} 服务停止失败！"
    fi
}

# 主循环
main() {
    while true; do
        show_menu

        case $choice in
            1)
                start_container
                ;;
            2)
                stop_container
                ;;
            3)
                restart_container
                ;;
            4)
                show_status
                ;;
            5)
                show_logs
                ;;
            6)
                exec_container
                ;;
            7)
                remove_container
                ;;
            8)
                compose_up
                ;;
            9)
                compose_down
                ;;
            0)
                echo -e "${BLUE}再见！${NC}"
                exit 0
                ;;
            *)
                echo -e "${RED}无效选项，请重试${NC}"
                ;;
        esac

        echo ""
        read -p "按 Enter 键继续..." dummy
    done
}

# 运行主程序
main
