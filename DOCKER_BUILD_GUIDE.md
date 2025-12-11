# Docker 构建和推送指南

本文档介绍如何使用 `docker-build` 脚本构建和推送 DNS Core Server Docker 镜像。

## 脚本说明

项目提供了两个构建脚本：

- **docker-build.bat** - Windows 批处理脚本
- **docker-build.sh** - Linux/Mac Bash 脚本
- **docker-build-local.bat** - 快捷方式（调用 docker-build.bat，预设仓库地址）

## 使用方法

### 基本用法

#### 仅构建镜像（不推送）

```bash
# Windows
docker-build.bat

# Linux/Mac
./docker-build.sh
```

这将构建镜像并打上 `dns-core-server:latest` 标签。

---

### 指定镜像标签

```bash
# Windows
docker-build.bat -t v1.0.0

# Linux/Mac
./docker-build.sh -t v1.0.0
```

这将构建镜像并打上以下标签：
- `dns-core-server:v1.0.0`
- `dns-core-server:latest`

---

### 构建并推送到 Docker Hub

**步骤 1: 登录 Docker Hub**

```bash
docker login
# 输入用户名和密码
```

**步骤 2: 构建并推送**

```bash
# Windows
docker-build.bat -r docker.io/yourusername -t v1.0.0 --push

# Linux/Mac
./docker-build.sh -r docker.io/yourusername -t v1.0.0 --push
```

推送的镜像：
- `docker.io/yourusername/dns-core-server:v1.0.0`
- `docker.io/yourusername/dns-core-server:latest`

---

### 构建并推送到私有仓库

**步骤 1: 登录私有仓库**

```bash
docker login registry.example.com
# 输入用户名和密码
```

**步骤 2: 构建并推送**

```bash
# Windows
docker-build.bat -r registry.example.com/myproject -t latest --push

# Linux/Mac
./docker-build.sh -r registry.example.com/myproject -t latest --push
```

推送的镜像：
- `registry.example.com/myproject/dns-core-server:latest`

---

## 参数说明

| 参数 | 简写 | 说明 | 示例 |
|------|------|------|------|
| `--tag` | `-t` | 指定镜像标签 | `-t v1.0.0` |
| `--registry` | `-r` | 指定镜像仓库前缀 | `-r docker.io/username` |
| `--push` | `-p` | 构建后自动推送到仓库 | `--push` |
| `--help` | `-h` | 显示帮助信息 | `--help` |

---

## 脚本行为说明

### 默认行为（不带 --push）

1. 检查 Docker 是否安装
2. 检查 Dockerfile 是否存在
3. 构建 Docker 镜像
4. 显示构建的镜像信息
5. 显示运行命令提示

### 使用 --push 参数

1. 执行默认构建步骤 1-4
2. **如果指定了 `-r` 参数**：
   - 推送 `${REGISTRY}/${IMAGE_NAME}:${TAG}` 镜像
   - 如果 TAG 不是 `latest`，也推送 `latest` 标签
   - 显示推送成功信息
3. **如果未指定 `-r` 参数**：
   - 显示警告信息，跳过推送步骤

---

## 常见场景

### 场景 1: 本地开发测试

只构建镜像，不推送到任何仓库：

```bash
# Windows
docker-build.bat

# Linux/Mac
./docker-build.sh
```

### 场景 2: 开发版本发布

构建特定版本并推送到开发环境仓库：

```bash
# Windows
docker-build.bat -r dev-registry.company.com/team -t dev-20250611 --push

# Linux/Mac
./docker-build.sh -r dev-registry.company.com/team -t dev-20250611 --push
```

### 场景 3: 生产版本发布

构建稳定版本并推送到 Docker Hub：

```bash
# Windows
docker-build.bat -r docker.io/yourcompany -t v1.2.0 --push

# Linux/Mac
./docker-build.sh -r docker.io/yourcompany -t v1.2.0 --push
```

### 场景 4: 多仓库发布

需要推送到多个仓库时，分别执行：

```bash
# 推送到 Docker Hub
./docker-build.sh -r docker.io/yourname -t v1.0.0 --push

# 推送到私有仓库
./docker-build.sh -r registry.company.com/project -t v1.0.0 --push

# 推送到阿里云容器镜像服务
./docker-build.sh -r registry.cn-hangzhou.aliyuncs.com/namespace -t v1.0.0 --push
```

---

## 使用 docker-build-local.bat（Windows 快捷方式）

`docker-build-local.bat` 是一个预配置的快捷方式，适用于特定的公司环境：

```batch
call docker-build.bat -t latest -r docker.flexem.com/flexem %*
```

这个脚本会：
- 自动使用 `latest` 标签
- 自动使用 `docker.flexem.com/flexem` 作为仓库前缀
- 传递所有额外参数给 `docker-build.bat`

**使用示例：**

```bash
# 仅构建
docker-build-local.bat

# 构建并推送
docker-build-local.bat --push

# 自定义标签
docker-build-local.bat -t v2.0.0 --push
```

---

## 错误处理

### Docker 未安装

```
[错误] 未找到 Docker 命令！
请先安装 Docker Desktop: https://www.docker.com/products/docker-desktop
```

**解决方案：** 安装 Docker Desktop 或 Docker Engine

### Dockerfile 不存在

```
[错误] 未找到 Dockerfile 文件！
请确保在项目根目录下运行此脚本。
```

**解决方案：** 在项目根目录（`dns-core/`）下运行脚本

### 推送失败

```
[错误] 镜像推送失败！
请确保:
  1. 已登录到镜像仓库: docker login
  2. 有推送权限
  3. 网络连接正常
```

**解决方案：**

1. **检查登录状态：**
   ```bash
   docker login registry.example.com
   ```

2. **检查权限：** 确保您的账户有推送权限

3. **检查网络：** 确保可以访问镜像仓库

---

## 镜像信息

构建完成后，脚本会显示：

```
========================================
构建完成！
========================================

镜像标签: dns-core-server:latest

运行容器:
  docker run -d -p 53:53/udp -p 5000:5000 --name dns-core dns-core-server:latest

或使用 docker-compose:
  docker-compose up -d

访问地址:
  Web 管理界面: http://localhost:5000
  Swagger API:  http://localhost:5000/swagger
```

---

## 查看构建的镜像

```bash
# 查看本地所有 dns-core-server 镜像
docker images dns-core-server

# 查看指定仓库的镜像
docker images registry.example.com/project/dns-core-server
```

---

## 删除镜像

```bash
# 删除本地镜像
docker rmi dns-core-server:latest

# 删除指定标签
docker rmi dns-core-server:v1.0.0

# 强制删除（如果容器正在使用）
docker rmi -f dns-core-server:latest
```

---

## 注意事项

1. **推送前必须登录：** 使用 `--push` 前确保已通过 `docker login` 登录到目标仓库

2. **标签命名规范：** 建议使用语义化版本号（如 `v1.0.0`, `v1.2.3`）

3. **latest 标签：** 每次构建都会自动打上 `latest` 标签，推送时也会一并推送

4. **仓库前缀格式：**
   - Docker Hub: `docker.io/username` 或 `username`
   - 私有仓库: `registry.example.com/project`
   - 阿里云: `registry.cn-hangzhou.aliyuncs.com/namespace`

5. **Windows 权限：** 在 Windows 上运行 Docker 命令可能需要管理员权限

6. **Linux 权限：** 首次运行 `.sh` 脚本需要添加执行权限：
   ```bash
   chmod +x docker-build.sh
   ```

---

## 相关命令

```bash
# 查看 Docker 版本
docker --version

# 查看登录状态
cat ~/.docker/config.json

# 登出
docker logout

# 拉取推送的镜像
docker pull registry.example.com/project/dns-core-server:v1.0.0

# 运行推送的镜像
docker run -d -p 53:53/udp -p 5000:5000 \
  --name dns-core \
  registry.example.com/project/dns-core-server:v1.0.0
```

---

## 快速参考

| 操作 | 命令 |
|------|------|
| 仅构建 | `docker-build.bat` |
| 构建+标签 | `docker-build.bat -t v1.0.0` |
| 构建+推送 | `docker-build.bat -r registry/project -t v1.0.0 --push` |
| 查看帮助 | `docker-build.bat --help` |
| 查看镜像 | `docker images dns-core-server` |
| 运行容器 | `docker run -d -p 53:53/udp -p 5000:5000 dns-core-server` |

---

**更新日期：** 2025-06-11
**版本：** 1.0
