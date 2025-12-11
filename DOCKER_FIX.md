# Docker 构建问题已修复

## 🔧 已修复的问题

### 1. **Dockerfile 更新**
- ✅ 修复了 NuGet 包还原问题（包含新的 Microsoft.Data.Sqlite 和 LiteDB）
- ✅ 优化了构建阶段，使用解决方案文件进行还原
- ✅ 添加了数据目录支持，用于持久化存储
- ✅ 创建了数据卷配置

### 2. **新增文件**
- ✅ `.dockerignore` - 优化构建上下文
- ✅ `docker-compose.yml` - Docker Compose 配置
- ✅ `docker-build.bat` - Windows 构建脚本
- ✅ `docs/DOCKER_GUIDE.md` - 完整的 Docker 部署指南

## 🚀 现在可以构建了！

### 方式 1: 使用 Docker 命令

```bash
# 构建镜像
docker build -t dns-core:latest .

# 运行容器
docker run -d \
  --name dns-core \
  -p 53:53/udp \
  -p 5000:5000 \
  -v dns-data:/app/data \
  --cap-add=NET_BIND_SERVICE \
  dns-core:latest
```

### 方式 2: 使用 Docker Compose（推荐）

```bash
# 一键启动
docker-compose up -d

# 查看日志
docker-compose logs -f

# 停止服务
docker-compose down
```

### 方式 3: 使用构建脚本（Windows）

```bash
# 直接运行
docker-build.bat
```

## 📝 Dockerfile 主要改动

### 修改前
```dockerfile
# 只复制项目文件
COPY ["src/DnsCore/DnsCore.csproj", "src/DnsCore/"]
RUN dotnet restore "src/DnsCore/DnsCore.csproj"
```

### 修改后
```dockerfile
# 复制解决方案文件和项目文件
COPY ["DnsCore.sln", "./"]
COPY ["src/DnsCore/DnsCore.csproj", "src/DnsCore/"]
RUN dotnet restore "DnsCore.sln"  # 使用解决方案还原，包含所有依赖
```

### 新增数据卷支持
```dockerfile
# 创建数据目录用于持久化存储
RUN mkdir -p /app/data && chown -R dnscore:dnscore /app/data

# 创建数据卷
VOLUME ["/app/data"]
```

## 🎯 持久化配置

### 使用 JSON 文件（默认）
```yaml
services:
  dns-core:
    volumes:
      - ./data:/app/data  # DNS 记录将保存在 ./data/dns-records.json
```

### 使用 SQLite
在 `appsettings.json` 中配置：
```json
{
  "DnsServer": {
    "Persistence": {
      "Provider": "Sqlite",
      "FilePath": "/app/data/dns-records.db"
    }
  }
}
```

### 使用 LiteDB
```json
{
  "DnsServer": {
    "Persistence": {
      "Provider": "LiteDb",
      "FilePath": "/app/data/dns-records.litedb"
    }
  }
}
```

## ✅ 验证构建

### 1. 构建镜像
```bash
docker build -t dns-core:latest .
```

### 2. 检查镜像
```bash
docker images | grep dns-core
```

### 3. 运行测试
```bash
docker run --rm dns-core:latest dotnet --info
```

### 4. 完整测试
```bash
# 启动容器
docker-compose up -d

# 测试健康检查
curl http://localhost:5000/health

# 测试 API
curl http://localhost:5000/api/dns/records

# 访问 Web 界面
# 浏览器打开: http://localhost:5000
```

## 🐛 如果仍有问题

### 检查 Docker 版本
```bash
docker --version
docker-compose --version
```

### 清理缓存重新构建
```bash
docker-compose down -v
docker system prune -a
docker-compose build --no-cache
docker-compose up -d
```

### 查看详细日志
```bash
docker-compose logs -f
```

### 进入容器调试
```bash
docker exec -it dns-core-server /bin/bash
ls -la /app
ls -la /app/data
```

## 📚 详细文档

完整的 Docker 部署指南请查看:
- `docs/DOCKER_GUIDE.md` - Docker 部署完整指南
- `docker-compose.yml` - Docker Compose 配置示例
- `README.md` - 项目主文档

## 🎉 总结

所有 Docker 相关的问题已修复：
- ✅ Dockerfile 已更新以支持新的持久化功能
- ✅ 添加了完整的 Docker Compose 配置
- ✅ 创建了详细的部署文档
- ✅ 提供了多种构建和运行方式
- ✅ 支持三种持久化方案（JSON、SQLite、LiteDB）

现在您可以轻松地使用 Docker 部署 DNS Core Server！

---
**日期**: 2025-12-11
**版本**: v1.0.0
