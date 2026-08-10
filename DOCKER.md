# DNS Core Server - 容器部署指南

本文档介绍如何使用容器部署和运行 DNS Core Server。

**支持 Docker 与 Podman**：所有脚本会自动探测本机可用的容器引擎，
无需修改即可在两者上运行。探测优先级为 `docker` > `podman`，
可用环境变量 `CONTAINER_ENGINE` 强制指定。

## 📋 目录

- [容器引擎兼容性](#容器引擎兼容性)
- [快速开始](#快速开始)
- [构建镜像](#构建镜像)
- [运行容器](#运行容器)
- [使用 Compose](#使用-docker-compose)
- [配置说明](#配置说明)
- [常见问题](#常见问题)

## 容器引擎兼容性

### 自动探测

`docker-build.sh` / `docker-run.sh` / `docker-build.bat` / `docker-run.bat`
均通过 `scripts/container-engine.{sh,bat}` 探测引擎：

| 情况 | 使用的引擎 |
|---|---|
| 同时装有 docker 与 podman | docker |
| 仅装 podman | podman |
| 设置 `CONTAINER_ENGINE=podman` | podman（强制） |
| 两者都没有 | 报错并给出安装指引 |

Compose 命令同样自动探测，依次尝试：
`podman compose` → `podman-compose`（podman）、
`docker compose` → `docker-compose`（docker）。

```bash
# 强制使用 podman 构建
CONTAINER_ENGINE=podman ./docker-build.sh -t v1.0.0
```

```batch
REM Windows 下强制使用 podman
set CONTAINER_ENGINE=podman
docker-build.bat
```

### Podman 相关注意事项

**镜像格式**：Podman 默认输出 OCI 格式，而 `HEALTHCHECK` 属于 Docker
镜像格式的扩展，OCI 下会被静默丢弃（仅一条 warning）。构建脚本已自动
为 podman 追加 `--format docker`，以保留健康检查。手工构建时需自行加上：

```bash
podman build --format docker -t dns-core-server:latest .
```

**特权端口**：容器内以非 root 用户运行，DNS 需绑定 53 端口。镜像已对
`dotnet` 二进制设置 `cap_net_bind_service` 文件能力；compose 中还显式
声明了 `cap_add: NET_BIND_SERVICE`，因为 `no-new-privileges` 会阻止
文件能力提升生效。

若使用 **rootless** Podman，宿主侧无法绑定 53 端口，可选：

```bash
# 方式 1：放宽内核限制（一次性，需 root）
sudo sysctl -w net.ipv4.ip_unprivileged_port_start=53

# 方式 2：改用高端口
DNS_HOST_PORT=5353 podman compose up -d

# 方式 3：使用 rootful podman
sudo -E ./docker-run.sh
```

**bind mount 目录**：compose 将 `./data` 挂载进容器。Docker 会自动创建
缺失的挂载源目录，**Podman 不会**，目录不存在时直接报
`statfs .../data: no such file or directory`。仓库已包含 `data/.gitkeep`
以确保该目录存在；若被误删，手工创建即可：

```bash
mkdir -p data
```

**Containerfile**：Podman 惯用 `Containerfile` 命名，但也能直接读取
`Dockerfile`。本项目只保留 `Dockerfile`，脚本会自动识别两者中存在的那个。

## 🚀 快速开始

### 方法 1: 使用构建脚本（推荐）

**Windows:**
```batch
# 构建镜像
docker-build.bat

# 运行容器（交互式管理）
docker-run.bat
```

**Linux/Mac:**
```bash
# 添加执行权限
chmod +x docker-build.sh docker-run.sh

# 构建镜像
./docker-build.sh

# 运行容器（交互式管理）
./docker-run.sh
```

### 方法 2: 使用 Docker Compose（最简单）

```bash
# 构建并启动
docker-compose up -d

# 查看日志
docker-compose logs -f

# 停止服务
docker-compose down
```

> **Podman 用户**：下文示例统一使用 `docker` 命令。Podman 的 CLI 与 Docker
> 兼容，把 `docker` 换成 `podman`、`docker-compose` 换成 `podman compose` 即可。
> 手工 `podman build` 时请追加 `--format docker` 以保留 HEALTHCHECK。
> 使用项目自带脚本则无需关心差异，脚本会自动探测引擎。

## 🔨 构建镜像

### 基本构建

```bash
# Windows
docker-build.bat

# Linux/Mac
./docker-build.sh
```

### 自定义标签

```bash
# Windows
docker-build.bat -t v1.0.0

# Linux/Mac
./docker-build.sh -t v1.0.0
```

### 指定镜像仓库

```bash
# Windows
docker-build.bat -r myregistry.com/myproject -t v1.0.0

# Linux/Mac
./docker-build.sh -r myregistry.com/myproject -t v1.0.0
```

### 手动构建

```bash
docker build -t dns-core-server:latest .
```

## 🏃 运行容器

### 使用管理脚本（推荐）

**Windows:** 运行 `docker-run.bat`

**Linux/Mac:** 运行 `./docker-run.sh`

管理脚本提供以下功能：
- ✅ 启动/停止/重启容器
- ✅ 查看容器状态和日志
- ✅ 进入容器终端
- ✅ 管理 Docker Compose 服务

### 手动运行

```bash
# 基本运行
docker run -d \
  --name dns-core-server \
  -p 53:53/udp \
  -p 5000:5000 \
  dns-core-server:latest

# 带重启策略
docker run -d \
  --name dns-core-server \
  -p 53:53/udp \
  -p 5000:5000 \
  --restart unless-stopped \
  dns-core-server:latest

# 带自定义配置
docker run -d \
  --name dns-core-server \
  -p 53:53/udp \
  -p 5000:5000 \
  -v $(pwd)/appsettings.Production.json:/app/appsettings.json:ro \
  --restart unless-stopped \
  dns-core-server:latest
```

### 常用 Docker 命令

```bash
# 查看运行状态
docker ps

# 查看日志
docker logs dns-core-server
docker logs -f dns-core-server  # 实时查看

# 停止容器
docker stop dns-core-server

# 启动容器
docker start dns-core-server

# 重启容器
docker restart dns-core-server

# 删除容器
docker rm -f dns-core-server

# 进入容器
docker exec -it dns-core-server /bin/bash
```

## 🐳 使用 Docker Compose

### 基本操作

```bash
# 启动服务（后台运行）
docker-compose up -d

# 查看服务状态
docker-compose ps

# 查看日志
docker-compose logs
docker-compose logs -f  # 实时查看

# 停止服务
docker-compose stop

# 停止并删除容器
docker-compose down

# 重启服务
docker-compose restart

# 重新构建并启动
docker-compose up -d --build
```

### 自定义配置

编辑 `docker-compose.yml` 文件，可以配置以下内容：

**端口映射：**
```yaml
ports:
  - "53:53/udp"     # DNS 端口
  - "5000:5000"     # Web 管理界面
```

**环境变量：**
```yaml
environment:
  - ASPNETCORE_URLS=http://+:5000
  - TZ=Asia/Shanghai
```

**数据卷挂载：**
```yaml
volumes:
  - ./appsettings.Production.json:/app/appsettings.json:ro
  - ./logs:/app/logs
```

**资源限制：**
```yaml
deploy:
  resources:
    limits:
      cpus: '0.5'
      memory: 512M
```

## ⚙️ 配置说明

### 环境变量

| 变量名 | 说明 | 默认值 |
|--------|------|--------|
| `ASPNETCORE_URLS` | HTTP 监听地址 | `http://+:5000` |
| `ASPNETCORE_ENVIRONMENT` | 运行环境 | `Production` |
| `TZ` | 时区设置 | `Asia/Shanghai` |
| `LANG` | 语言编码 | `C.UTF-8` |

### 端口映射

| 主机端口 | 容器端口 | 协议 | 说明 |
|---------|---------|------|------|
| 53 | 53 | UDP | DNS 查询端口 |
| 53 | 53 | TCP | DNS 查询端口（可选） |
| 5000 | 5000 | TCP | Web 管理界面和 API |

### 访问服务

容器启动后，可通过以下地址访问：

- **Web 管理界面**: http://localhost:5000
- **Swagger API 文档**: http://localhost:5000/swagger
- **健康检查**: http://localhost:5000/health
- **DNS 服务**: UDP 端口 53

### 数据持久化

如需持久化配置和数据，可挂载以下目录：

```yaml
volumes:
  # 自定义配置文件
  - ./appsettings.Production.json:/app/appsettings.json:ro

  # 日志目录
  - ./logs:/app/logs

  # 数据目录（如需）
  - ./data:/app/data
```

## ❓ 常见问题

### 1. DNS 端口 53 被占用

**问题：** 端口 53 已被系统 DNS 服务占用

**解决方案：**
- **Windows:** 停止 DNS Client 服务或使用其他端口
- **Linux:** 停止 systemd-resolved 或配置端口映射

使用其他端口：
```bash
# 映射到 5053 端口
docker run -d -p 5053:53/udp -p 5000:5000 dns-core-server:latest
```

### 2. 权限不足

**问题：** 容器内运行用户权限不足

**解决方案：**

在 `docker-compose.yml` 中添加：
```yaml
user: root  # 仅在必要时使用
```

或使用特权模式：
```yaml
privileged: true  # 不推荐，安全风险
```

### 3. 无法访问 Web 界面

**检查步骤：**

1. 确认容器正在运行：
```bash
docker ps | grep dns-core
```

2. 查看容器日志：
```bash
docker logs dns-core-server
```

3. 检查端口映射：
```bash
docker port dns-core-server
```

4. 测试健康检查：
```bash
curl http://localhost:5000/health
```

### 4. 中文乱码

确保容器环境变量已设置 UTF-8 编码（默认已配置）：

```yaml
environment:
  - LANG=C.UTF-8
  - LC_ALL=C.UTF-8
  - DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
```

### 5. 构建失败

**检查：**

1. Docker 是否正确安装
```bash
docker --version
docker-compose --version
```

2. 是否在项目根目录
```bash
ls Dockerfile
```

3. 网络连接是否正常（需要下载 .NET SDK）

4. 磁盘空间是否充足
```bash
docker system df
```

清理旧镜像：
```bash
docker system prune -a
```

## 🔐 安全建议

1. **使用非 root 用户**：Dockerfile 已配置 `dnscore` 用户
2. **限制资源使用**：配置 CPU 和内存限制
3. **只读根文件系统**：可选配置 `read_only: true`
4. **定期更新镜像**：使用最新的基础镜像
5. **最小权限原则**：避免使用 `privileged` 模式

## 📚 更多资源

- [项目主文档](README.md)
- [项目结构说明](CLAUDE.md)
- [测试报告](docs/TEST_REPORT.md)
- [贡献指南](CONTRIBUTING.md)

## 📞 支持

如有问题，请：
1. 查看本文档的常见问题部分
2. 查看容器日志 `docker logs dns-core-server`
3. 提交 Issue 到项目仓库

---

**祝使用愉快！** 🎉
