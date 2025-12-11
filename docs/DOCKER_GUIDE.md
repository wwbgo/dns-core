# DNS Core Server - Docker 部署指南

本指南介绍如何使用 Docker 部署 DNS Core Server，包括持久化配置。

## 🐳 快速开始

### 方式 1: 使用 Docker Compose（推荐）

```bash
# 启动服务
docker-compose up -d

# 查看日志
docker-compose logs -f

# 停止服务
docker-compose down
```

### 方式 2: 使用 Docker 命令

```bash
# 构建镜像
docker build -t dns-core:latest .

# 运行容器
docker run -d \
  --name dns-core \
  -p 53:53/udp \
  -p 53:53/tcp \
  -p 5000:5000 \
  -v dns-data:/app/data \
  --cap-add=NET_BIND_SERVICE \
  dns-core:latest

# 查看日志
docker logs -f dns-core
```

### 方式 3: 使用构建脚本（Windows）

```bash
# 运行构建脚本
docker-build.bat
```

## 📦 镜像说明

### 基础镜像
- **构建阶段**: `mcr.microsoft.com/dotnet/sdk:10.0`
- **运行阶段**: `mcr.microsoft.com/dotnet/aspnet:10.0`

### 镜像特点
- ✅ 多阶段构建，镜像体积小
- ✅ 使用非 root 用户运行（安全）
- ✅ 支持持久化数据卷
- ✅ 内置健康检查
- ✅ UTF-8 编码支持（中文无乱码）

## 🔧 配置说明

### 1. 持久化配置

**使用数据卷挂载**:
```yaml
volumes:
  - dns-data:/app/data
```

**使用绑定挂载**:
```yaml
volumes:
  - ./data:/app/data
```

### 2. 自定义配置文件

**挂载自定义 appsettings.json**:
```yaml
volumes:
  - ./my-appsettings.json:/app/appsettings.json:ro
```

**appsettings.json 示例**:
```json
{
  "DnsServer": {
    "Port": 53,
    "Persistence": {
      "Provider": "JsonFile",
      "FilePath": "/app/data/dns-records.json",
      "AutoSave": true
    }
  }
}
```

### 3. 环境变量

| 变量 | 说明 | 默认值 |
|------|------|--------|
| `ASPNETCORE_URLS` | HTTP 监听地址 | `http://+:5000` |
| `ASPNETCORE_ENVIRONMENT` | 运行环境 | `Production` |
| `TZ` | 时区 | `UTC` |

## 🌐 端口映射

| 容器端口 | 协议 | 说明 |
|---------|------|------|
| 53 | UDP | DNS 查询端口 |
| 53 | TCP | DNS 查询端口（TCP） |
| 5000 | TCP | Web 管理界面和 API |

## 💾 持久化方案

### JSON 文件（默认）
```yaml
environment:
  - DNSSERVER__PERSISTENCE__PROVIDER=JsonFile
  - DNSSERVER__PERSISTENCE__FILEPATH=/app/data/dns-records.json
```

### SQLite 数据库
```yaml
environment:
  - DNSSERVER__PERSISTENCE__PROVIDER=Sqlite
  - DNSSERVER__PERSISTENCE__FILEPATH=/app/data/dns-records.db
```

### LiteDB 数据库
```yaml
environment:
  - DNSSERVER__PERSISTENCE__PROVIDER=LiteDb
  - DNSSERVER__PERSISTENCE__FILEPATH=/app/data/dns-records.litedb
```

## 🔐 权限和安全

### 53 端口绑定

DNS 默认使用 53 端口，需要特殊权限：

**选项 1: 添加网络绑定能力**（推荐）
```yaml
cap_add:
  - NET_BIND_SERVICE
```

**选项 2: 使用特权模式**
```yaml
privileged: true
```

**选项 3: 使用 host 网络模式**
```yaml
network_mode: host
```

**选项 4: 端口重映射**（非标准 DNS）
```yaml
ports:
  - "5353:53/udp"  # 使用 5353 端口
```

## 📝 完整 docker-compose.yml 示例

```yaml
version: '3.8'

services:
  dns-core:
    build:
      context: .
      dockerfile: Dockerfile
    container_name: dns-core-server
    restart: unless-stopped
    
    ports:
      - "53:53/udp"
      - "53:53/tcp"
      - "5000:5000"
    
    environment:
      - ASPNETCORE_URLS=http://+:5000
      - ASPNETCORE_ENVIRONMENT=Production
      - TZ=Asia/Shanghai
    
    volumes:
      - dns-data:/app/data
      - ./appsettings.json:/app/appsettings.json:ro
    
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5000/health"]
      interval: 30s
      timeout: 3s
      retries: 3
      start_period: 5s
    
    cap_add:
      - NET_BIND_SERVICE
    
    logging:
      driver: "json-file"
      options:
        max-size: "10m"
        max-file: "3"

volumes:
  dns-data:
    driver: local
```

## 🚀 常用命令

### 查看容器状态
```bash
docker ps
docker-compose ps
```

### 查看日志
```bash
# Docker
docker logs dns-core
docker logs -f dns-core --tail 100

# Docker Compose
docker-compose logs
docker-compose logs -f --tail 100
```

### 进入容器
```bash
docker exec -it dns-core /bin/bash
```

### 重启容器
```bash
docker restart dns-core
docker-compose restart
```

### 停止和删除
```bash
# Docker
docker stop dns-core
docker rm dns-core

# Docker Compose
docker-compose down
docker-compose down -v  # 同时删除数据卷
```

### 查看资源使用
```bash
docker stats dns-core
```

## 🧪 测试 DNS 服务

### 从主机测试
```bash
# 测试 DNS 查询
nslookup example.local localhost

# 使用 dig
dig @localhost example.local

# 使用 curl 测试 API
curl http://localhost:5000/health
curl http://localhost:5000/api/dns/records
```

### 从容器内测试
```bash
docker exec -it dns-core curl http://localhost:5000/health
```

## 📊 性能优化

### 1. 资源限制
```yaml
deploy:
  resources:
    limits:
      cpus: '0.5'
      memory: 512M
    reservations:
      cpus: '0.25'
      memory: 256M
```

### 2. 日志管理
```yaml
logging:
  driver: "json-file"
  options:
    max-size: "10m"
    max-file: "3"
```

## 🐛 故障排查

### 问题 1: 无法绑定 53 端口
**解决方案**:
- 检查是否有其他服务占用 53 端口
- 确保添加了 `NET_BIND_SERVICE` 能力
- 或使用非标准端口映射

### 问题 2: 容器无法启动
**排查步骤**:
```bash
# 查看详细日志
docker logs dns-core

# 检查配置
docker inspect dns-core

# 验证镜像
docker images | grep dns-core
```

### 问题 3: 数据未持久化
**检查**:
- 确认数据卷已正确挂载
- 检查配置文件中的持久化设置
- 验证数据目录权限

### 问题 4: 中文乱码
**解决方案**:
- 镜像已配置 UTF-8 编码
- 检查环境变量 `LANG=C.UTF-8`

## 📚 参考资料

- [Docker 官方文档](https://docs.docker.com/)
- [Docker Compose 文档](https://docs.docker.com/compose/)
- [DNS Core Server GitHub](https://github.com/your-repo/dns-core)

---

**最后更新**: 2025-12-11
**版本**: v1.0.0
