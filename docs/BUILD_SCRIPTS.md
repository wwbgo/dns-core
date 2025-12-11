# DNS Core Server - 构建脚本完整指南

详细的构建脚本使用说明和最佳实践。

---

## 📋 目录

- [脚本概览](#脚本概览)
- [基础构建脚本](#基础构建脚本)
- [高级构建脚本](#高级构建脚本)
- [Docker 构建](#docker-构建)
- [构建流程图](#构建流程图)
- [常见场景](#常见场景)
- [故障排查](#故障排查)
- [CI/CD 集成](#cicd-集成)

---

## 📊 脚本概览

### 构建脚本分类

| 类别 | 脚本 | 用途 | 速度 |
|------|------|------|------|
| **标准构建** | `build.bat/sh` | Debug 构建 + 测试 | 🐢 中速 |
| **Release 构建** | `build-release.bat/sh` | 生产优化构建 + 测试 | 🐢 中速 |
| **快速构建** | `build-quick.bat/sh` | 仅构建，跳过测试 | 🚀 快速 |
| **重新构建** | `rebuild.bat/sh` | 清理 + 完整构建 | 🐌 慢速 |
| **Docker 构建** | `build-docker.bat/sh` | Docker 镜像构建 | 🐢 中速 |

### 脚本位置

```
dns-core/
├── scripts/                    # 🔧 所有构建脚本的主目录
│   ├── build.bat/sh           # 标准构建
│   ├── build-release.bat/sh   # Release 构建
│   ├── build-quick.bat/sh     # 快速构建
│   ├── rebuild.bat/sh         # 重新构建
│   └── build-docker.bat/sh    # Docker 构建
│
└── docker-build.bat/sh        # 快捷方式 (调用 scripts/build-docker)
```

---

## 🔨 基础构建脚本

### 1. 标准构建 (build.bat/sh)

**用途：** 日常开发的标准构建流程

**包含步骤：**
1. 还原 NuGet 包
2. 构建解决方案 (Debug 配置)
3. 运行所有单元测试

**使用方法：**

```bash
# Windows
cd scripts
build.bat

# Linux/Mac
cd scripts
./build.sh
```

**输出位置：**
```
src/DnsCore/bin/Debug/net10.0/
tests/DnsCore.Tests/bin/Debug/net10.0/
```

**适用场景：**
- ✅ 日常开发
- ✅ 提交代码前验证
- ✅ 合并分支前检查
- ✅ 本地完整测试

**预期耗时：** 约 10-30 秒

---

### 2. Release 构建 (build-release.bat/sh)

**用途：** 构建生产环境优化版本

**包含步骤：**
1. 还原 NuGet 包
2. 构建解决方案 (Release 配置)
3. 运行所有单元测试 (Release 模式)

**使用方法：**

```bash
# Windows
cd scripts
build-release.bat

# Linux/Mac
cd scripts
./build-release.sh
```

**输出位置：**
```
src/DnsCore/bin/Release/net10.0/
tests/DnsCore.Tests/bin/Release/net10.0/
```

**Release 优化：**
- 🚀 代码优化
- 📦 体积优化
- ⚡ 性能优化
- 🔒 调试符号移除

**适用场景：**
- ✅ 发布前验证
- ✅ 性能测试
- ✅ 基准测试
- ✅ 生产部署前

**预期耗时：** 约 15-40 秒

---

### 3. 快速构建 (build-quick.bat/sh)

**用途：** 快速迭代开发，跳过测试

**包含步骤：**
1. 还原 NuGet 包（静默模式）
2. 构建解决方案 (Debug 配置)
3. ⚠️ **跳过测试步骤**

**使用方法：**

```bash
# Windows
cd scripts
build-quick.bat

# Linux/Mac
cd scripts
./build-quick.sh
```

**输出位置：**
```
src/DnsCore/bin/Debug/net10.0/
```

**适用场景：**
- ✅ 快速验证编译错误
- ✅ 代码重构时快速检查
- ✅ 修改后立即运行
- ⚠️ **不推荐提交前使用**

**预期耗时：** 约 5-15 秒

**注意事项：**
```
⚠️ 警告：此脚本跳过测试步骤
建议在最终提交前运行完整构建：
- scripts/build.bat (Windows)
- ./scripts/build.sh (Linux/Mac)
```

---

## 🔧 高级构建脚本

### 4. 重新构建 (rebuild.bat/sh)

**用途：** 解决构建缓存问题，从零开始构建

**包含步骤：**
1. **清理所有构建输出** (调用 clean 脚本)
   - 删除 bin/ 目录
   - 删除 obj/ 目录
   - 清理 NuGet 缓存
2. 还原 NuGet 包
3. 构建解决方案 (Debug 配置)
4. 运行所有单元测试

**使用方法：**

```bash
# Windows
cd scripts
rebuild.bat

# Linux/Mac
cd scripts
./rebuild.sh
```

**输出位置：**
```
src/DnsCore/bin/Debug/net10.0/
tests/DnsCore.Tests/bin/Debug/net10.0/
```

**适用场景：**
- ✅ 构建错误无法解决
- ✅ 切换分支后出现问题
- ✅ NuGet 包损坏
- ✅ 增量构建不正确
- ✅ 发布前最终验证

**预期耗时：** 约 20-50 秒

**何时使用：**

```bash
# 场景 1: 遇到奇怪的构建错误
构建失败，但代码看起来正确
→ 运行 rebuild.sh

# 场景 2: 切换分支
git checkout main
→ 运行 rebuild.sh

# 场景 3: NuGet 包问题
包版本冲突或损坏
→ 运行 rebuild.sh
```

---

## 🐳 Docker 构建

### 5. Docker 镜像构建 (build-docker.bat/sh)

**用途：** 构建 Docker 容器镜像

**包含步骤：**
1. 检查 Docker 是否运行
2. 使用 Dockerfile 构建镜像
3. 显示镜像信息

**使用方法：**

```bash
# Windows
cd scripts
build-docker.bat

# 或使用根目录快捷方式
docker-build.bat

# Linux/Mac
cd scripts
./build-docker.sh

# 或使用根目录快捷方式
./docker-build.sh
```

**镜像信息：**
- **镜像名称：** `dns-core-server:latest`
- **基础镜像：** `mcr.microsoft.com/dotnet/aspnet:10.0`
- **构建方式：** Multi-stage build
- **镜像大小：** 约 200-300 MB

**Dockerfile 阶段：**

```dockerfile
# 阶段 1: Build (构建环境)
FROM mcr.microsoft.com/dotnet/sdk:10.0
- 还原依赖
- 编译代码
- 运行测试

# 阶段 2: Runtime (运行环境)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
- 仅复制编译产物
- 最小化镜像大小
```

**后续操作：**

```bash
# 启动容器
docker-compose up -d

# 查看日志
docker-compose logs -f

# 停止容器
docker-compose down

# 运行测试
docker run --rm dns-core-server:latest dotnet test
```

**适用场景：**
- ✅ 容器化部署
- ✅ 云环境部署
- ✅ Kubernetes 部署
- ✅ 开发环境隔离
- ✅ CI/CD 流水线

**预期耗时：** 约 1-3 分钟（首次），10-30 秒（增量）

---

## 📊 构建流程图

### 标准构建流程

```
开始
  ↓
还原 NuGet 包
  ↓
编译源代码
  ↓
运行单元测试
  ↓
[测试通过?]
  ↓ 是
✓ 构建成功
  ↓ 否
✗ 构建失败
```

### 重新构建流程

```
开始
  ↓
清理构建输出
  ↓
清理 NuGet 缓存
  ↓
还原 NuGet 包
  ↓
编译源代码
  ↓
运行单元测试
  ↓
[测试通过?]
  ↓ 是
✓ 构建成功
  ↓ 否
✗ 构建失败
```

### Docker 构建流程

```
开始
  ↓
检查 Docker 状态
  ↓
[Docker 运行?]
  ↓ 是
读取 Dockerfile
  ↓
阶段 1: SDK 镜像
  ├─ 还原依赖
  ├─ 编译代码
  └─ 运行测试
  ↓
阶段 2: Runtime 镜像
  ├─ 复制编译产物
  └─ 配置入口点
  ↓
✓ 镜像构建完成
  ↓ 否
✗ Docker 未运行
```

---

## 🎯 常见场景

### 场景 1: 日常开发流程

```bash
# 1. 拉取最新代码
git pull

# 2. 标准构建
./scripts/build.sh

# 3. 修改代码
# ... 编辑文件 ...

# 4. 快速构建验证
./scripts/build-quick.sh

# 5. 运行服务器测试
./start-server.sh

# 6. 最终提交前完整构建
./scripts/build.sh

# 7. 提交代码
git add .
git commit -m "feat: xxx"
git push
```

### 场景 2: 发布版本流程

```bash
# 1. 清理项目
./scripts/clean.sh

# 2. Release 构建
./scripts/build-release.sh

# 3. 发布所有平台
./scripts/publish.sh win-x64
./scripts/publish.sh linux-x64
./scripts/publish.sh osx-x64

# 4. 构建 Docker 镜像
./scripts/build-docker.sh

# 5. 测试 Docker 容器
docker-compose up -d
./scripts/health-check.sh
docker-compose down

# 6. 打标签
git tag v1.0.0
git push --tags
```

### 场景 3: 解决构建问题

```bash
# 问题：构建失败，但代码看起来正确

# 步骤 1: 尝试重新构建
./scripts/rebuild.sh

# 如果仍然失败
# 步骤 2: 手动深度清理
./scripts/clean.sh
rm -rf ~/.nuget/packages/dnscore*
dotnet nuget locals all --clear

# 步骤 3: 重新构建
./scripts/rebuild.sh

# 如果还是失败
# 步骤 4: 验证环境
dotnet --version  # 检查 .NET 版本
dotnet --info     # 查看详细信息
```

### 场景 4: 切换分支

```bash
# 1. 保存当前工作
git stash

# 2. 切换分支
git checkout feature-branch

# 3. 重新构建
./scripts/rebuild.sh

# 4. 继续工作
# ...

# 5. 切换回主分支
git checkout main
./scripts/rebuild.sh
```

---

## 🛠️ 故障排查

### 问题 1: NuGet 还原失败

**错误信息：**
```
error NU1301: Unable to load the service index for source
```

**解决方法：**

```bash
# 方法 1: 清理 NuGet 缓存
dotnet nuget locals all --clear
./scripts/rebuild.sh

# 方法 2: 检查网络连接
ping api.nuget.org

# 方法 3: 使用国内镜像（中国用户）
# 编辑 nuget.config，添加：
<packageSources>
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  <add key="华为云" value="https://mirrors.huaweicloud.com/repository/nuget/v3/index.json" />
</packageSources>
```

### 问题 2: 编译错误

**错误信息：**
```
error CS0246: The type or namespace name 'XXX' could not be found
```

**解决方法：**

```bash
# 1. 检查项目引用
dotnet list reference

# 2. 重新构建
./scripts/rebuild.sh

# 3. 检查 .csproj 文件
# 确保 PackageReference 正确
```

### 问题 3: 测试失败

**错误信息：**
```
Test Run Failed.
Total tests: 52
     Passed: 51
     Failed: 1
```

**解决方法：**

```bash
# 1. 查看详细测试输出
dotnet test --verbosity detailed

# 2. 运行特定测试
dotnet test --filter "FullyQualifiedName~DnsServerTests"

# 3. 清理并重新运行
./scripts/rebuild.sh
```

### 问题 4: Docker 构建失败

**错误信息：**
```
Cannot connect to the Docker daemon
```

**解决方法：**

```bash
# Windows
# 启动 Docker Desktop

# Linux
sudo systemctl start docker
sudo systemctl status docker

# Mac
# 启动 Docker Desktop

# 验证 Docker
docker info
```

### 问题 5: 端口占用

**错误信息：**
```
Failed to bind to address http://0.0.0.0:5000: address already in use
```

**解决方法：**

```bash
# Windows
netstat -ano | findstr :5000
taskkill /PID <PID> /F

# Linux/Mac
lsof -i :5000
kill -9 <PID>

# 或修改端口
export ASPNETCORE_URLS="http://localhost:5001"
./start-server.sh
```

---

## 🔄 CI/CD 集成

### GitHub Actions 示例

```yaml
name: Build and Test

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v3

    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '10.0.x'

    - name: Build
      run: ./scripts/build.sh

    - name: Test
      run: ./scripts/test.sh coverage

    - name: Upload coverage
      uses: codecov/codecov-action@v3
      with:
        files: ./coverage/coverage.cobertura.xml

  docker:
    runs-on: ubuntu-latest
    needs: build

    steps:
    - uses: actions/checkout@v3

    - name: Build Docker image
      run: ./scripts/build-docker.sh

    - name: Test Docker image
      run: |
        docker run --rm dns-core-server:latest dotnet test
```

### GitLab CI 示例

```yaml
stages:
  - build
  - test
  - docker

build-job:
  stage: build
  image: mcr.microsoft.com/dotnet/sdk:10.0
  script:
    - chmod +x scripts/*.sh
    - ./scripts/build.sh
  artifacts:
    paths:
      - src/DnsCore/bin/
      - tests/DnsCore.Tests/bin/

test-job:
  stage: test
  image: mcr.microsoft.com/dotnet/sdk:10.0
  script:
    - ./scripts/test.sh coverage
  coverage: '/Total.*?(\d+\.?\d*)%/'

docker-job:
  stage: docker
  image: docker:latest
  services:
    - docker:dind
  script:
    - ./scripts/build-docker.sh
    - docker push dns-core-server:latest
```

### Jenkins Pipeline 示例

```groovy
pipeline {
    agent any

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Build') {
            steps {
                sh './scripts/build.sh'
            }
        }

        stage('Test') {
            steps {
                sh './scripts/test.sh coverage'
            }
            post {
                always {
                    junit 'tests/**/TestResults/*.xml'
                    publishHTML([
                        reportDir: 'coverage',
                        reportFiles: 'index.html',
                        reportName: 'Coverage Report'
                    ])
                }
            }
        }

        stage('Docker Build') {
            when {
                branch 'main'
            }
            steps {
                sh './scripts/build-docker.sh'
            }
        }
    }
}
```

---

## 📈 性能优化

### 构建性能对比

| 脚本 | 首次构建 | 增量构建 | 包含测试 | 优化等级 |
|------|---------|---------|---------|---------|
| `build.sh` | ~30s | ~15s | ✅ | Debug |
| `build-release.sh` | ~40s | ~20s | ✅ | Release |
| `build-quick.sh` | ~15s | ~8s | ❌ | Debug |
| `rebuild.sh` | ~50s | ~50s | ✅ | Debug |
| `build-docker.sh` | ~180s | ~30s | ✅ | Release |

### 加速构建技巧

**1. 使用本地 NuGet 缓存**

```bash
# 预热 NuGet 缓存
dotnet restore DnsCore.sln
```

**2. 使用增量构建**

```bash
# 避免每次都 rebuild
./scripts/build.sh  # 而不是 rebuild.sh
```

**3. 并行构建**

```bash
# 在 .csproj 中启用并行构建
dotnet build -m:4  # 使用 4 个并行进程
```

**4. 使用 Docker 构建缓存**

```bash
# 保持 Dockerfile 层顺序优化
# 将不常变化的层放在前面
```

---

## 💡 最佳实践

### 1. 日常开发

✅ **推荐做法：**
- 使用 `build-quick.sh` 快速验证
- 提交前运行 `build.sh` 完整验证
- 定期运行 `test.sh coverage` 检查覆盖率

❌ **不推荐做法：**
- 跳过测试直接提交
- 从不运行完整构建
- 忽略编译警告

### 2. 提交代码前

✅ **必须执行：**
```bash
./scripts/clean.sh        # 清理
./scripts/build.sh        # 完整构建
./scripts/test.sh         # 运行测试
```

### 3. 发布版本前

✅ **必须执行：**
```bash
./scripts/clean.sh              # 清理
./scripts/build-release.sh      # Release 构建
./scripts/test.sh coverage      # 覆盖率测试
./scripts/publish.sh linux-x64  # 发布
./scripts/build-docker.sh       # Docker 镜像
```

### 4. 遇到问题时

✅ **第一步：**
```bash
./scripts/rebuild.sh  # 重新构建
```

✅ **第二步：**
```bash
./scripts/clean.sh
dotnet nuget locals all --clear
./scripts/rebuild.sh
```

---

## 📚 相关文档

- [SCRIPTS.md](../SCRIPTS.md) - 脚本快速参考
- [scripts/README.md](../scripts/README.md) - 脚本工具集完整说明
- [QUICKSTART.md](../QUICKSTART.md) - 快速开始指南
- [README.md](../README.md) - 项目文档
- [CONTRIBUTING.md](../CONTRIBUTING.md) - 贡献指南

---

## 🎓 总结

### 构建脚本选择指南

| 场景 | 推荐脚本 | 理由 |
|------|---------|------|
| 日常开发 | `build-quick.sh` | 快速验证 |
| 提交前 | `build.sh` | 完整验证 |
| 发布前 | `build-release.sh` | 生产优化 |
| 遇到问题 | `rebuild.sh` | 深度清理 |
| Docker 部署 | `build-docker.sh` | 容器化 |

### 快速命令

```bash
# 最常用的 5 个命令
./scripts/build-quick.sh     # 快速构建
./scripts/build.sh           # 标准构建
./scripts/build-release.sh   # Release 构建
./scripts/rebuild.sh         # 重新构建
./scripts/build-docker.sh    # Docker 构建
```

---

**构建脚本让 DNS Core Server 的开发更加高效和可靠！** 🚀
