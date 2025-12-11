# DNS Core Server

一个使用 C# 开发的现代化 DNS 服务器，支持自定义 DNS 记录、上游 DNS 转发和 RESTful API 管理。

## 功能特性

- ✅ 自定义 DNS 记录（A, AAAA, CNAME, TXT 等）
- ✅ **泛域名支持**（使用 `*.example.com` 匹配所有子域名）
- ✅ **多种持久化方案**（支持 JSON 文件、SQLite、LiteDB 三种存储方式）
- ✅ 上游 DNS 转发（支持自定义或使用系统默认 DNS）
- ✅ **UDP 和 TCP 双协议支持**（符合 RFC 1035 标准）
- ✅ **Web 管理界面**（现代化的可视化管理控制台）
- ✅ RESTful API 管理接口
- ✅ Swagger/OpenAPI 文档
- ✅ 配置文件管理
- ✅ 详细的日志记录
- ✅ 基于 .NET 10 和 C# 13 最新特性

## 项目结构

```
dns-core/
├── src/
│   └── DnsCore/              # 主项目
│       ├── Configuration/    # 配置管理
│       ├── Models/          # 数据模型
│       ├── Protocol/        # DNS 协议实现
│       ├── Repositories/    # 持久化仓储
│       ├── Services/        # 核心服务
│       ├── wwwroot/         # Web 静态文件
│       └── Program.cs       # 程序入口
├── tests/
│   └── DnsCore.Tests/       # 单元测试
├── docs/                    # 文档
├── scripts/                 # 🔧 脚本工具集
├── DnsCore.sln             # 解决方案文件
└── README.md               # 本文件
```

## 快速开始

### 前置要求

- .NET 10.0 SDK 或更高版本
- 管理员权限（监听 53 端口需要）

### 编译项目

```bash
# 使用解决方案文件构建
dotnet build DnsCore.sln

# 或直接构建主项目
dotnet build src/DnsCore/DnsCore.csproj
```

### 运行服务器

**Windows (需要管理员权限):**
```bash
dotnet run --project src/DnsCore/DnsCore.csproj
```

**Linux:**
```bash
sudo dotnet run --project src/DnsCore/DnsCore.csproj
```

## Web 管理界面

DNS Core Server 提供了一个现代化的 Web 管理界面，可以方便地管理 DNS 记录。

### 访问界面

启动服务器后，在浏览器中打开：

```
http://localhost:5000
```

### 功能特性

- 🎨 现代化的响应式设计
- 📊 实时显示服务器状态和记录统计
- ➕ 可视化添加 DNS 记录
- 🔍 实时搜索过滤记录
- 🗑️ 一键删除记录
- 🔄 自动刷新（每 30 秒）
- 📱 支持移动设备

### 界面截图

Web 界面包含以下功能：

1. **状态栏** - 显示服务器健康状态和记录总数
2. **添加记录表单** - 支持所有 DNS 记录类型
3. **记录列表** - 可搜索、可排序的记录表格
4. **操作按钮** - 删除单条记录或清空所有记录

## RESTful API 管理

除了 Web 界面，DNS Core Server 还提供 RESTful API 用于管理自定义 DNS 记录。

### Swagger UI

启动服务器后，访问 `http://localhost:5000/swagger` 查看交互式 API 文档。

### API 端点

- `GET /health` - 健康检查
- `GET /api/dns/records` - 获取所有自定义记录
- `POST /api/dns/records` - 添加自定义记录
- `GET /api/dns/records/{domain}/{type}` - 查询指定记录
- `DELETE /api/dns/records/{domain}/{type}` - 删除指定记录
- `DELETE /api/dns/records` - 清空所有自定义记录

### API 使用示例

**添加自定义记录:**
```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "example.local",
    "type": "A",
    "value": "192.168.1.100",
    "ttl": 3600
  }'
```

**查询记录:**
```bash
curl http://localhost:5000/api/dns/records/example.local/A
```

**删除记录:**
```bash
curl -X DELETE http://localhost:5000/api/dns/records/example.local/A
```

## 配置说明

编辑 `src/DnsCore/appsettings.json` 文件配置 DNS 服务器：

```json
{
  "DnsServer": {
    "Port": 53,
    "UpstreamDnsServers": [
      "8.8.8.8",
      "1.1.1.1"
    ],
    "EnableUpstreamDnsQuery": false,
    "CustomRecords": [
      {
        "Domain": "example.local",
        "Type": "A",
        "Value": "192.168.1.100",
        "TTL": 3600
      }
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

### 配置项说明

**DnsServer 配置:**
- **Port**: DNS 服务器监听端口，默认 53
- **UpstreamDnsServers**: 上游 DNS 服务器列表（可选）
  - 留空则使用系统默认 DNS 服务器
  - 可以配置多个，按顺序尝试
- **EnableUpstreamDnsQuery**: 是否启用上游 DNS 查询（默认 false）
  - `true` - 当自定义记录不存在时，查询上游 DNS
  - `false` - 当自定义记录不存在时，返回 SERVFAIL（服务器故障），让客户端尝试系统配置的下一个 DNS 服务器
  - 适用场景：如果只想解析自定义记录而不转发到公共 DNS，设置为 false，客户端会自动回退到系统的其他 DNS 服务器
- **CustomRecords**: 自定义 DNS 记录列表
  - **Domain**: 域名
  - **Type**: 记录类型（A, AAAA, CNAME, TXT 等）
  - **Value**: 记录值
  - **TTL**: 生存时间（秒）

**Web 服务器配置:**
- HTTP 端口默认为 5000，可通过环境变量 `ASPNETCORE_URLS` 修改
- 开发模式下自动启用 Swagger UI

## 持久化存储

DNS Core Server 支持三种持久化方案，可以确保 DNS 记录在服务器重启后不会丢失。

### 支持的持久化提供者

1. **JSON 文件**（默认）
   - 简单轻量，易于阅读和手动编辑
   - 适合小规模数据（<10000 条记录）
   - 无需额外依赖

2. **SQLite**
   - 成熟的关系型数据库
   - 支持复杂查询和索引
   - 适合中大规模数据

3. **LiteDB**
   - 轻量级 NoSQL 数据库
   - .NET 原生支持，易于使用
   - 性能优秀，适合各种规模

### 配置持久化

在 `appsettings.json` 中添加 `Persistence` 配置：

```json
{
  "DnsServer": {
    "Port": 53,
    "Persistence": {
      "Provider": "JsonFile",
      "FilePath": "data/dns-records.json",
      "AutoSave": true,
      "AutoSaveInterval": 0
    }
  }
}
```

**配置项说明：**
- **Provider**: 持久化提供者类型
  - `JsonFile` - JSON 文件存储
  - `Sqlite` - SQLite 数据库
  - `LiteDb` - LiteDB 数据库
- **FilePath**: 数据文件路径
  - JSON: `data/dns-records.json`
  - SQLite: `data/dns-records.db`
  - LiteDB: `data/dns-records.litedb`
- **AutoSave**: 是否启用自动保存（默认 true）
- **AutoSaveInterval**: 自动保存间隔（秒），0 表示立即保存

### 切换持久化方案

**使用 SQLite：**
```json
{
  "Persistence": {
    "Provider": "Sqlite",
    "FilePath": "data/dns-records.db"
  }
}
```

**使用 LiteDB：**
```json
{
  "Persistence": {
    "Provider": "LiteDb",
    "FilePath": "data/dns-records.litedb"
  }
}
```

### 数据迁移

如果需要在不同持久化方案之间迁移数据：

1. 导出现有记录（通过 API 获取所有记录）
2. 更改配置文件中的 `Provider` 和 `FilePath`
3. 重启服务器
4. 导入记录（通过 API 添加记录）

## 支持的记录类型

- **A**: IPv4 地址记录
- **AAAA**: IPv6 地址记录
- **CNAME**: 别名记录
- **NS**: 名称服务器记录
- **PTR**: 指针记录
- **TXT**: 文本记录
- **MX**: 邮件交换记录
- **SRV**: 服务定位记录

## 泛域名支持

DNS Core Server 支持泛域名（Wildcard DNS）记录，可以匹配任意子域名。

### 使用方法

在域名字段中使用 `*.` 前缀：

```json
{
  "Domain": "*.example.com",
  "Type": "A",
  "Value": "192.168.1.100",
  "TTL": 3600
}
```

### 匹配规则

1. **精确匹配优先**：如果同时存在精确记录和泛域名记录，优先返回精确记录
   - `www.example.com` (精确) > `*.example.com` (泛域名)

2. **最具体的泛域名优先**：如果有多个泛域名，优先匹配最具体的
   - `*.dev.example.com` > `*.example.com`

3. **不匹配基础域名**：泛域名 `*.example.com` 不会匹配 `example.com` 本身

### 示例

**添加泛域名记录：**
```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "*.dev.local",
    "type": "A",
    "value": "10.0.0.1",
    "ttl": 3600
  }'
```

**测试泛域名解析：**
```bash
# 以下查询都会返回 10.0.0.1
nslookup api.dev.local 127.0.0.1
nslookup www.dev.local 127.0.0.1
nslookup anything.dev.local 127.0.0.1
```

**多级泛域名：**
```bash
# 添加 *.dev.example.com
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "*.dev.example.com",
    "type": "A",
    "value": "10.0.0.1",
    "ttl": 3600
  }'

# 添加 *.example.com
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "*.example.com",
    "type": "A",
    "value": "192.168.1.1",
    "ttl": 3600
  }'

# api.dev.example.com 匹配 *.dev.example.com -> 10.0.0.1
# www.example.com 匹配 *.example.com -> 192.168.1.1
```

## UDP 和 TCP 协议支持

DNS Core Server 完全支持 UDP 和 TCP 两种协议，符合 RFC 1035 标准要求。

### 协议特性

**UDP 协议（端口 53/UDP）**
- 默认的 DNS 传输协议
- 适用于大多数 DNS 查询
- 低延迟、高效率
- 响应大小限制：512 字节（传统）或 4096 字节（EDNS0）

**TCP 协议（端口 53/TCP）**
- 支持大型 DNS 响应（超过 UDP 限制）
- 适用于区域传输（AXFR/IXFR）
- 可靠的传输保证
- 无大小限制（最大 65535 字节）

### TCP 使用场景

1. **大型响应**：当 DNS 响应超过 512 字节时
2. **多记录查询**：返回大量 A 记录或 TXT 记录
3. **安全场景**：某些安全应用要求使用 TCP
4. **客户端回退**：UDP 查询失败后自动切换到 TCP

### 测试 TCP DNS 查询

**使用 dig 测试 TCP:**
```bash
# Linux/Mac - 使用 TCP 协议查询
dig @127.0.0.1 +tcp example.local

# 查看 TCP 连接详情
dig @127.0.0.1 +tcp +trace example.local
```

**使用 nslookup 测试:**
```bash
# Windows - nslookup 在需要时会自动使用 TCP
nslookup -vc example.local 127.0.0.1
```

## 工作原理

**DNS 查询流程:**
1. DNS 服务器同时在 UDP 53 和 TCP 53 端口监听客户端查询请求
2. 首先在自定义记录中查找匹配项
3. 如果找到匹配，返回自定义记录
4. 如果未找到且 `EnableUpstreamDnsQuery` 为 `true`，转发到上游 DNS 服务器
   - 如果上游 DNS 返回结果，返回给客户端
   - 如果上游 DNS 未找到，返回 NXDOMAIN（域名不存在）
5. 如果未找到且 `EnableUpstreamDnsQuery` 为 `false`，返回 SERVFAIL（服务器故障）
   - 客户端收到 SERVFAIL 后会自动尝试系统配置的下一个 DNS 服务器
   - 这样可以实现只解析自定义记录，其他域名由系统的其他 DNS 服务器处理
6. TCP 查询会自动处理消息长度前缀（2字节大端序）

**Web API 管理:**
1. HTTP 服务器在 5000 端口（可配置）接收 API 请求
2. 支持实时添加、查询、删除自定义 DNS 记录
3. 所有更改立即生效，无需重启服务器

## 开发

### 运行单元测试

项目包含完整的单元测试套件（38个测试用例），覆盖所有核心组件：

```bash
# 运行所有测试
dotnet test

# 运行测试并显示详细输出
dotnet test --verbosity normal
```

**测试覆盖:**
- ✅ DNS 协议解析和构建
- ✅ 自定义记录存储和查询
- ✅ 多种 DNS 记录类型
- ✅ 域名压缩和解压

详细测试报告请查看 [TEST_REPORT.md](docs/TEST_REPORT.md)

### 功能测试

**测试 DNS 查询:**

使用 `nslookup` 或 `dig` 测试 DNS 服务器：

```bash
# Windows
nslookup example.local 127.0.0.1

# Linux/Mac
dig @127.0.0.1 example.local
```

**测试 Web 界面:**

1. 启动服务器后，打开浏览器访问 `http://localhost:5000`
2. 在表单中填写域名、记录类型、记录值和 TTL
3. 点击"添加记录"按钮
4. 在记录列表中查看新添加的记录
5. 使用搜索框过滤记录
6. 点击"删除"按钮移除记录

**测试 Web API:**

```bash
# 健康检查
curl http://localhost:5000/health

# 获取所有记录
curl http://localhost:5000/api/dns/records

# 访问 Web 管理界面
浏览器打开: http://localhost:5000

# 访问 Swagger UI
浏览器打开: http://localhost:5000/swagger
```

## 技术栈

- **.NET 10.0** - 最新的 .NET 平台
- **C# 13** - 使用最新语言特性
  - Primary constructors
  - Collection expressions
  - Required properties
  - Pattern matching
  - Expression-bodied members
- **ASP.NET Core** - Web 框架和 Minimal API
- **Swashbuckle.AspNetCore** - Swagger/OpenAPI 支持
- **Docker** - 容器化部署
- **xUnit** - 单元测试框架
- **FluentAssertions** - 流式断言库
- **Moq** - Mock 测试库

## 文档

### 快速上手
- [QUICKSTART.md](QUICKSTART.md) - **5分钟快速开始指南** ⚡
- [README.md](README.md) - 项目完整说明（本文件）

### 开发文档
- [CLAUDE.md](CLAUDE.md) - Claude Code 开发指南
- [CONTRIBUTING.md](CONTRIBUTING.md) - 贡献指南
- [docs/BUILD_SCRIPTS.md](docs/BUILD_SCRIPTS.md) - **构建脚本完整指南** 🔨

### 使用指南
- [docs/WEB_INTERFACE_GUIDE.md](docs/WEB_INTERFACE_GUIDE.md) - Web 界面使用指南
- [docs/WILDCARD_DNS_GUIDE.md](docs/WILDCARD_DNS_GUIDE.md) - 泛域名使用指南
- [docs/API_EXAMPLES.md](docs/API_EXAMPLES.md) - **API 使用示例大全** 📚

### 部署文档
- [docs/DOCKER_DEPLOYMENT.md](docs/DOCKER_DEPLOYMENT.md) - Docker 部署指南

### 测试文档
- [docs/TEST_REPORT.md](docs/TEST_REPORT.md) - 测试报告
- [docs/TEST_COVERAGE_REPORT.md](docs/TEST_COVERAGE_REPORT.md) - **详细测试覆盖率报告** 📊

### 脚本工具
- [SCRIPTS.md](SCRIPTS.md) - **脚本快速参考** 📋
- [scripts/README.md](scripts/README.md) - **脚本工具集完整说明** 🔧

## 快速开始

### 方法 1: Docker 部署（推荐）

```bash
# Windows
docker-start.bat

# Linux/Mac
chmod +x docker-start.sh
./docker-start.sh
```

或使用 Docker Compose：

```bash
docker-compose up -d
```

### 方法 2: 本地运行

使用启动脚本快速启动服务器：

**Windows（以管理员身份运行）**:
```cmd
start-server.bat
```

**Linux/Mac**:
```bash
chmod +x start-server.sh
./start-server.sh
```

### 访问服务

启动后访问：
- **Web 管理界面**: http://localhost:5000
- **Swagger 文档**: http://localhost:5000/swagger
- **DNS 服务**: UDP/TCP 端口 53

## 贡献

欢迎贡献！请查看 [CONTRIBUTING.md](CONTRIBUTING.md) 了解如何参与项目开发。

## 许可证

本项目采用 MIT 许可证 - 查看 [LICENSE](LICENSE) 文件了解详情
