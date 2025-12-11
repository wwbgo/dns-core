# Docker 部署指南

## 概述

DNS Core Server 提供完整的 Docker 容器化支持，可以轻松部署到任何支持 Docker 的环境。

## 前置要求

- Docker Engine 20.10+
- Docker Compose 2.0+
- 2GB 可用磁盘空间
- 至少 512MB 可用内存

## 快速开始

### 方法 1: 使用 Docker Compose（推荐）

**1. 启动服务**

```bash
# Windows
docker-start.bat

# Linux/Mac
chmod +x docker-start.sh
./docker-start.sh
```

**2. 访问服务**

- Web 管理界面: http://localhost:5000
- Swagger API: http://localhost:5000/swagger
- DNS 服务: UDP 53 / TCP 53

**3. 查看日志**

```bash
docker-compose logs -f
```

**4. 停止服务**

```bash
docker-compose down
```

### 方法 2: 手动构建和运行

**1. 构建镜像**

```bash
# Windows
docker-build.bat

# Linux/Mac
chmod +x docker-build.sh
./docker-build.sh
```

或手动构建：

```bash
docker build -t dns-core-server:latest .
```

**2. 运行容器**

```bash
docker run -d \
  --name dns-core-server \
  -p 53:53/udp \
  -p 5000:5000 \
  dns-core-server:latest
```

**3. 查看状态**

```bash
docker ps
docker logs dns-core-server
```

## 配置说明

### 环境变量

在 `docker-compose.yaml` 中配置环境变量：

```yaml
environment:
  - ASPNETCORE_ENVIRONMENT=Production
  - ASPNETCORE_URLS=http://+:5000
  - TZ=Asia/Shanghai
```

### 自定义配置文件

**1. 创建配置文件**

复制示例配置：
```bash
cp appsettings.docker.json appsettings.custom.json
```

**2. 编辑配置**

```json
{
  "DnsServer": {
    "Port": 53,
    "UpstreamDnsServers": [
      "8.8.8.8",
      "1.1.1.1"
    ],
    "CustomRecords": [
      {
        "Domain": "*.dev.local",
        "Type": "A",
        "Value": "192.168.1.100",
        "TTL": 3600
      }
    ]
  }
}
```

**3. 挂载配置文件**

修改 `docker-compose.yaml`：

```yaml
volumes:
  - ./appsettings.custom.json:/app/appsettings.json:ro
```

### 端口映射

默认端口映射：

| 服务 | 容器端口 | 宿主机端口 | 协议 |
|------|----------|------------|------|
| DNS | 53 | 53 | UDP |
| DNS | 53 | 53 | TCP |
| Web/API | 5000 | 5000 | TCP |

**修改端口**：

```yaml
ports:
  - "1053:53/udp"  # 使用高端口避免权限问题（UDP）
  - "1053:53/tcp"  # 使用高端口避免权限问题（TCP）
  - "8080:5000"    # Web 界面使用 8080 端口
```

### 数据持久化

**1. 日志持久化**

```yaml
volumes:
  - ./logs:/app/logs
```

**2. 配置持久化**

```yaml
volumes:
  - ./config:/app/config
  - ./config/appsettings.json:/app/appsettings.json:ro
```

## 高级配置

### 资源限制

在 `docker-compose.yaml` 中设置：

```yaml
deploy:
  resources:
    limits:
      cpus: '2.0'
      memory: 1G
    reservations:
      cpus: '0.5'
      memory: 256M
```

### 网络配置

**1. 使用宿主机网络模式**

```yaml
network_mode: "host"
```

**2. 自定义网络**

```yaml
networks:
  dns-network:
    driver: bridge
    ipam:
      config:
        - subnet: 172.20.0.0/16
```

### 健康检查

默认健康检查配置：

```yaml
healthcheck:
  test: ["CMD", "curl", "-f", "http://localhost:5000/health"]
  interval: 30s
  timeout: 3s
  retries: 3
  start_period: 10s
```

## Docker Compose 完整示例

### 基础部署

```yaml
version: '3.8'

services:
  dns-core:
    image: dns-core-server:latest
    container_name: dns-core-server
    restart: unless-stopped
    ports:
      - "53:53/udp"
      - "5000:5000"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
    volumes:
      - ./appsettings.docker.json:/app/appsettings.json:ro
```

### 生产环境部署

```yaml
version: '3.8'

services:
  dns-core:
    image: dns-core-server:latest
    container_name: dns-core-prod
    restart: always

    ports:
      - "53:53/udp"
      - "5000:5000"

    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:5000
      - TZ=Asia/Shanghai

    volumes:
      - ./config/appsettings.prod.json:/app/appsettings.json:ro
      - ./logs:/app/logs

    networks:
      - dns-prod-network

    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5000/health"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 30s

    deploy:
      resources:
        limits:
          cpus: '2.0'
          memory: 1G
        reservations:
          cpus: '0.5'
          memory: 256M

    logging:
      driver: "json-file"
      options:
        max-size: "50m"
        max-file: "5"

networks:
  dns-prod-network:
    driver: bridge
```

## 常用命令

### 容器管理

```bash
# 启动容器
docker-compose up -d

# 停止容器
docker-compose down

# 重启容器
docker-compose restart

# 查看状态
docker-compose ps

# 查看日志
docker-compose logs -f

# 进入容器
docker exec -it dns-core-server bash
```

### 镜像管理

```bash
# 构建镜像
docker build -t dns-core-server:latest .

# 查看镜像
docker images | grep dns-core

# 删除镜像
docker rmi dns-core-server:latest

# 导出镜像
docker save dns-core-server:latest -o dns-core-server.tar

# 导入镜像
docker load -i dns-core-server.tar
```

### 调试命令

```bash
# 查看容器详情
docker inspect dns-core-server

# 查看资源使用
docker stats dns-core-server

# 查看网络
docker network inspect dns-network

# 测试健康检查
docker exec dns-core-server curl http://localhost:5000/health
```

## 故障排查

### 问题 1: 端口 53 权限被拒绝

**症状**:
```
Permission denied when binding to port 53
```

**解决方案**:

1. 使用高端口映射
```yaml
ports:
  - "1053:53/udp"
```

2. 使用特权模式（不推荐）
```yaml
privileged: true
```

3. 设置 CAP_NET_BIND_SERVICE 能力
```yaml
cap_add:
  - NET_BIND_SERVICE
```

### 问题 2: 容器无法启动

**检查步骤**:

1. 查看日志
```bash
docker-compose logs dns-core
```

2. 检查配置文件
```bash
docker run --rm -it dns-core-server:latest cat /app/appsettings.json
```

3. 验证镜像
```bash
docker run --rm dns-core-server:latest dotnet DnsCore.dll --version
```

### 问题 3: DNS 查询失败

**检查步骤**:

1. 验证容器网络
```bash
docker exec dns-core-server ping 8.8.8.8
```

2. 测试 DNS 端口
```bash
nc -u localhost 53
```

3. 查看 DNS 日志
```bash
docker-compose logs -f dns-core | grep DNS
```

### 问题 4: 配置文件未加载

**解决方案**:

1. 验证挂载
```bash
docker exec dns-core-server ls -la /app/appsettings.json
```

2. 检查权限
```bash
docker exec dns-core-server cat /app/appsettings.json
```

## 性能优化

### 1. 多阶段构建优化

Dockerfile 已使用多阶段构建，最终镜像只包含运行时文件。

### 2. 镜像大小优化

```bash
# 查看镜像大小
docker images dns-core-server

# 使用 Alpine 基础镜像（可选）
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
```

### 3. 缓存优化

构建时利用 Docker 缓存层：

```dockerfile
# 先复制项目文件，利用缓存
COPY ["src/DnsCore/DnsCore.csproj", "src/DnsCore/"]
RUN dotnet restore

# 再复制源代码
COPY . .
```

## 监控和日志

### 日志查看

```bash
# 实时日志
docker-compose logs -f

# 最近 100 行
docker-compose logs --tail=100

# 特定服务日志
docker-compose logs dns-core

# 带时间戳
docker-compose logs -f -t
```

### 健康监控

```bash
# 检查健康状态
docker inspect --format='{{.State.Health.Status}}' dns-core-server

# 查看健康检查历史
docker inspect --format='{{json .State.Health}}' dns-core-server | jq
```

## 备份和恢复

### 备份配置

```bash
# 备份配置文件
docker cp dns-core-server:/app/appsettings.json ./backup/

# 导出容器
docker export dns-core-server > dns-core-backup.tar
```

### 恢复配置

```bash
# 恢复配置文件
docker cp ./backup/appsettings.json dns-core-server:/app/

# 重启容器应用配置
docker-compose restart
```

## CI/CD 集成

### GitHub Actions 示例

```yaml
name: Docker Build and Push

on:
  push:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Build Docker image
        run: docker build -t dns-core-server:latest .

      - name: Run tests
        run: docker run --rm dns-core-server:latest dotnet test

      - name: Push to registry
        run: |
          echo "${{ secrets.DOCKER_PASSWORD }}" | docker login -u "${{ secrets.DOCKER_USERNAME }}" --password-stdin
          docker push dns-core-server:latest
```

## 安全建议

1. **使用非 root 用户**
   - Dockerfile 已配置 `dnscore` 用户运行

2. **只读文件系统**
```yaml
read_only: true
tmpfs:
  - /tmp
```

3. **限制能力**
```yaml
cap_drop:
  - ALL
cap_add:
  - NET_BIND_SERVICE
```

4. **网络隔离**
```yaml
networks:
  - dns-internal
```

## 相关资源

- [Dockerfile](../Dockerfile)
- [docker-compose.yaml](../docker-compose.yaml)
- [appsettings.docker.json](../appsettings.docker.json)
- [项目 README](../README.md)
