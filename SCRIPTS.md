# 🔧 DNS Core Server - 脚本快速参考

所有脚本的快速参考指南。详细说明请查看 [scripts/README.md](scripts/README.md)

---

## ⚡ 快速启动（最常用）

| 脚本 | 功能 | Windows | Linux/Mac |
|------|------|---------|-----------|
| **Docker启动** | 一键启动所有服务 | `docker-start.bat` | `./docker-start.sh` |
| **本地启动** | 本地运行服务器 | `start-server.bat` | `sudo ./start-server.sh` |
| **验证功能** | 完整功能验证 | `verify-功能.bat` | `./verify-功能.sh` |

---

## 🔨 构建脚本

| 脚本 | 功能 | 命令 |
|------|------|------|
| **标准构建** | Debug 构建 + 测试 | `scripts/build.sh` |
| **Release 构建** | 生产优化构建 | `scripts/build-release.sh` |
| **快速构建** | 仅构建，跳过测试 | `scripts/build-quick.sh` |
| **重新构建** | 清理 + 完整构建 | `scripts/rebuild.sh` |

---

## 🧪 测试相关

| 脚本 | 功能 | 命令 |
|------|------|------|
| **标准测试** | 运行所有测试 | `scripts/test.sh` |
| **快速测试** | 仅核心测试 | `scripts/test.sh quick` |
| **覆盖率测试** | 生成覆盖率报告 | `scripts/test.sh coverage` |
| **详细测试** | 显示详细输出 | `scripts/test.sh verbose` |

---

## 🛠️ 开发工具

| 脚本 | 功能 | 命令 |
|------|------|------|
| **清理项目** | 删除构建输出 | `scripts/clean.sh` |
| **发布版本** | 构建生产版本 | `scripts/publish.sh [runtime]` |
| **健康检查** | 检查服务状态 | `scripts/health-check.sh` |
| **示例数据** | 添加测试数据 | `scripts/add-sample-data.sh` |

---

## 🐳 Docker相关

| 脚本 | 功能 | 命令 |
|------|------|------|
| **构建镜像** | 构建Docker镜像 | `scripts/build-docker.sh` 或 `docker-build.sh` |
| **启动服务** | 启动Docker容器 | `docker-start.sh` |

---

## 📋 脚本参数说明

### build.sh 构建模式

```bash
# 标准 Debug 构建
./scripts/build.sh

# Release 构建
./scripts/build-release.sh

# 快速构建（跳过测试）
./scripts/build-quick.sh

# 重新构建（清理 + 构建）
./scripts/rebuild.sh

# Docker 镜像构建
./scripts/build-docker.sh
```

### test.sh 测试模式

```bash
./scripts/test.sh [模式]
```

| 模式 | 说明 |
|------|------|
| `normal` | 标准测试（默认） |
| `quick` | 快速测试（仅核心） |
| `coverage` | 代码覆盖率测试 |
| `verbose` | 详细输出测试 |

### publish.sh 运行时

```bash
./scripts/publish.sh [运行时]
```

| 运行时 | 说明 |
|--------|------|
| `win-x64` | Windows 64位 |
| `linux-x64` | Linux 64位 |
| `osx-x64` | macOS 64位 |
| `portable` | 跨平台（需要.NET） |

---

## 🚀 典型工作流

### 开发流程

```bash
# 1. 验证环境
./verify-功能.sh

# 2. 快速构建
./scripts/build-quick.sh

# 3. 启动服务
./docker-start.sh

# 4. 添加示例数据
./scripts/add-sample-data.sh

# 5. 运行测试
./scripts/test.sh

# 6. 健康检查
./scripts/health-check.sh
```

### 发布流程

```bash
# 1. 清理项目
./scripts/clean.sh

# 2. Release 构建
./scripts/build-release.sh

# 3. 发布版本
./scripts/publish.sh linux-x64

# 4. 构建 Docker
./scripts/build-docker.sh

# 5. 验证发布
cd publish/linux-x64
./DnsCore
```

---

## 📖 详细文档

完整的脚本说明和使用示例请查看：

👉 **[scripts/README.md](scripts/README.md)** - 脚本工具集完整文档

---

## 💡 快速提示

| 场景 | 建议脚本 |
|------|---------|
| 🆕 **首次使用** | `verify-功能.sh` |
| 🔨 **快速构建** | `scripts/build-quick.sh` |
| 🏗️ **完整构建** | `scripts/build.sh` |
| 🚀 **生产构建** | `scripts/build-release.sh` |
| 🔄 **重新构建** | `scripts/rebuild.sh` |
| 🐳 **Docker 构建** | `scripts/build-docker.sh` |
| 🏃 **快速测试** | `scripts/test.sh quick` |
| 🔍 **检查状态** | `scripts/health-check.sh` |
| 🎯 **演示功能** | `scripts/add-sample-data.sh` |
| 🧹 **清理项目** | `scripts/clean.sh` |
| 📦 **发布版本** | `scripts/publish.sh` |

---

**更多详情请查看 [scripts/README.md](scripts/README.md)** 📚
