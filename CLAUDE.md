# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

DNS Core Server 是一个使用 C# 开发的现代化高性能 DNS 服务器，支持自定义 DNS 记录、上游 DNS 转发、Web 管理界面和 RESTful API 管理功能。

**技术栈:**
- .NET 10.0（最新版本）
- C# 13（使用最新语言特性）
- ASP.NET Core（Web 框架和 Minimal API）
- UDP/TCP Socket 编程（DNS 协议）
- Swashbuckle.AspNetCore（Swagger/OpenAPI）
- Microsoft.Extensions.Configuration
- Microsoft.Extensions.Logging
- Microsoft.Extensions.Hosting（BackgroundService）

**性能优化特性:**
- Span<T> 和 Memory<T> - 零拷贝内存操作
- ArrayPool<T> - 内存池化减少 GC 压力
- LRU 缓存 - DNS 查询结果缓存
- 资源复用 - UdpClient 单例复用

## 项目结构

```
dns-core/
├── src/DnsCore/              # 主项目源代码
│   ├── Configuration/        # 配置选项类
│   ├── Models/              # DNS 数据模型
│   ├── Protocol/            # DNS 协议实现
│   ├── Repositories/        # 持久化仓储实现
│   │   ├── IDnsRecordRepository.cs    # 仓储接口
│   │   ├── JsonFileRepository.cs      # JSON 文件存储
│   │   ├── SqliteRepository.cs        # SQLite 数据库
│   │   └── LiteDbRepository.cs        # LiteDB 数据库
│   ├── Services/            # 核心服务
│   ├── wwwroot/             # Web 静态文件
│   │   ├── index.html       # Web 管理界面
│   │   ├── styles.css       # 样式文件
│   │   └── app.js           # JavaScript 逻辑
│   ├── Program.cs           # 程序入口
│   ├── DnsCore.csproj       # 项目文件
│   └── appsettings.json     # 配置文件
├── tests/DnsCore.Tests/     # 单元测试项目
│   ├── Models/              # 模型测试
│   ├── Protocol/            # 协议测试
│   ├── Services/            # 服务测试
│   └── DnsCore.Tests.csproj # 测试项目文件
├── docs/                    # 项目文档
│   └── TEST_REPORT.md       # 测试报告
├── .editorconfig            # 编辑器配置
├── .gitignore              # Git 忽略文件
├── CONTRIBUTING.md          # 贡献指南
├── LICENSE                  # MIT 许可证
├── README.md               # 项目说明
├── CLAUDE.md               # 本文件
├── DnsCore.sln             # 解决方案文件
├── start-server.bat         # Windows 启动脚本
└── start-server.sh          # Linux/Mac 启动脚本
```

## 常用命令

### 构建项目
```bash
# 构建整个解决方案
dotnet build DnsCore.sln

# 构建主项目
dotnet build src/DnsCore/DnsCore.csproj

# 构建测试项目
dotnet build tests/DnsCore.Tests/DnsCore.Tests.csproj
```

### 运行服务器
```bash
# 使用快速启动脚本（推荐）
# Windows（以管理员身份运行）
start-server.bat

# Linux/Mac
./start-server.sh

# 或直接使用 dotnet run
# Windows（需要管理员权限）
dotnet run --project src/DnsCore/DnsCore.csproj

# Linux
sudo dotnet run --project src/DnsCore/DnsCore.csproj
```

服务器启动后，访问以下地址：
- **Web 管理界面**: http://localhost:5000
- **Swagger API 文档**: http://localhost:5000/swagger
- **DNS 服务**: UDP 端口 53

### 运行测试
```bash
# 运行所有测试
dotnet test

# 运行测试并显示详细输出
dotnet test --verbosity normal

# 运行测试并生成覆盖率报告
dotnet test --collect:"XPlat Code Coverage"

# 运行特定测试项目
dotnet test tests/DnsCore.Tests/DnsCore.Tests.csproj
```

### 发布项目
```bash
# 发布到 publish 目录
dotnet publish src/DnsCore/DnsCore.csproj -c Release -o ./publish

# 发布为单文件可执行程序
dotnet publish src/DnsCore/DnsCore.csproj -c Release -r win-x64 --self-contained -o ./publish
```

## 项目架构

### 核心组件

1. **DnsServer** (`src/DnsCore/Services/DnsServer.cs`)
   - DNS 服务器核心，负责接收和处理 UDP/TCP 请求
   - 协调自定义记录查询和上游 DNS 转发
   - 使用 primary constructor 和 sealed class
   - **性能优化**：TCP 缓冲区使用 ArrayPool 复用

2. **DnsServerHostedService** (`src/DnsCore/Services/DnsServerHostedService.cs`)
   - 实现 BackgroundService，作为托管服务运行 DNS 服务器
   - 与 ASP.NET Core 生命周期集成

3. **CustomRecordStore** (`src/DnsCore/Services/CustomRecordStore.cs`)
   - 管理自定义 DNS 记录的存储和查询
   - **支持泛域名匹配**（`*.example.com` 格式）
   - 支持精确匹配和 ANY 类型查询
   - 泛域名匹配优先级：精确匹配 > 最具体的泛域名 > 较宽泛的泛域名
   - 使用 ConcurrentDictionary 确保线程安全
   - **集成持久化支持**，通过 IDnsRecordRepository 保存和加载记录

4. **持久化仓储** (`src/DnsCore/Repositories/`)
   - **IDnsRecordRepository** - 定义持久化操作的接口
   - **JsonFileRepository** - JSON 文件存储实现
     - 使用 System.Text.Json 序列化
     - 支持文件锁保证并发安全
   - **SqliteRepository** - SQLite 数据库实现
     - 使用 Microsoft.Data.Sqlite
     - 支持事务和索引
   - **LiteDbRepository** - LiteDB 数据库实现
     - 使用 LiteDB NuGet 包
     - 自动索引优化查询性能

5. **UpstreamDnsResolver** (`src/DnsCore/Services/UpstreamDnsResolver.cs`)
   - 处理上游 DNS 查询
   - 支持自定义上游服务器或使用系统 DNS
   - 包含响应解析逻辑
   - **性能优化**：集成 DNS 缓存，UdpClient 单例复用

6. **DnsCache** (`src/DnsCore/Services/DnsCache.cs`)
   - **性能优化**：DNS 查询结果缓存（LRU 策略）
   - 根据 TTL 自动过期
   - 最大缓存 10,000 条记录
   - 重复查询响应时间降低 80-95%

7. **DnsCacheCleanupService** (`src/DnsCore/Services/DnsCacheCleanupService.cs`)
   - **性能优化**：后台服务定期清理过期缓存
   - 每分钟清理一次

8. **DnsMessageParser** (`src/DnsCore/Protocol/DnsMessageParser.cs`)
   - DNS 协议解析器
   - 处理 DNS 消息的序列化和反序列化
   - 支持域名压缩
   - **性能优化**：使用 Span<T> 和 ArrayPool<T> 减少内存分配

9. **Web 管理界面** (`src/DnsCore/wwwroot/`)
   - **index.html** - 现代化的单页应用界面
   - **styles.css** - 响应式 CSS 样式（渐变背景、卡片设计）
   - **app.js** - 完整的 CRUD 功能实现
   - 特性：
     - 实时显示服务器状态
     - 可视化添加/删除记录
     - 实时搜索过滤
     - 自动刷新（30秒间隔）
     - 友好的错误提示

10. **Web API** (`src/DnsCore/Program.cs`)
   - 使用 Minimal API 提供 RESTful 接口
   - 支持实时管理 DNS 记录
   - 集成 Swagger/OpenAPI 文档
   - 静态文件服务（UseStaticFiles, UseDefaultFiles）

### DNS 查询流程（性能优化版）

1. 接收客户端 DNS 查询（UDP/TCP 53 端口）
2. 使用 **Span<T>** 解析 DNS 查询消息（零拷贝）
3. 在 CustomRecordStore 中查找匹配记录
4. 如果找到，返回自定义记录
5. 如果未找到且 EnableUpstreamDnsQuery 为 true，使用 UpstreamDnsResolver 转发查询
   - **先查询缓存**（微秒级响应）
   - 缓存未命中，查询上游 DNS
   - 成功：**缓存结果**并返回
   - 失败：返回 NXDOMAIN
6. 如果未找到且 EnableUpstreamDnsQuery 为 false，返回 SERVFAIL
   - 客户端会自动尝试系统配置的下一个 DNS 服务器
7. 使用 **ArrayPool** 构建并返回 DNS 响应（减少内存分配）

### Web API 流程

1. HTTP 服务器在 5000 端口（可配置）接收 API 请求
2. 通过依赖注入获取 CustomRecordStore 实例
3. 执行添加、查询、删除等操作
4. 返回 JSON 格式响应

### 配置文件

- `src/DnsCore/appsettings.json`: 主配置文件
  - DnsServer.Port: DNS 监听端口（默认 53）
  - DnsServer.UpstreamDnsServers: 上游 DNS 列表（空则使用系统 DNS）
  - DnsServer.EnableUpstreamDnsQuery: 是否启用上游 DNS 查询（默认 true）
    - true: 自定义记录不存在时查询上游 DNS
    - false: 自定义记录不存在时返回 SERVFAIL，让客户端尝试下一个 DNS 服务器
  - DnsServer.CustomRecords: 自定义 DNS 记录
  - DnsServer.Persistence: 持久化配置
    - Provider: 持久化提供者（JsonFile、Sqlite、LiteDb）
    - FilePath: 数据文件路径
    - AutoSave: 是否启用自动保存
    - AutoSaveInterval: 自动保存间隔（秒）
  - Logging: 日志级别配置
- 环境变量 `ASPNETCORE_URLS`: HTTP 服务器监听地址（默认 http://localhost:5000）

### Web 管理界面

- `GET /` - Web 管理控制台（index.html）
- 功能：
  - 实时状态监控
  - 添加 DNS 记录表单
  - 记录列表显示
  - 搜索过滤功能
  - 删除记录操作
  - 清空所有记录

### RESTful API 端点

- `GET /health` - 健康检查
- `GET /api/dns/records` - 获取所有自定义记录
- `POST /api/dns/records` - 添加自定义记录
- `GET /api/dns/records/{domain}/{type}` - 查询指定记录
- `DELETE /api/dns/records/{domain}/{type}` - 删除指定记录
- `DELETE /api/dns/records` - 清空所有自定义记录
- `GET /swagger` - Swagger UI 文档（仅开发模式）

## 测试

项目包含完整的单元测试（**47 个测试用例**），覆盖以下组件：
- DNS 模型（DnsHeader, DnsRecord, DnsQuestion） - `tests/DnsCore.Tests/Models/`
- DNS 协议解析器（DnsMessageParser） - `tests/DnsCore.Tests/Protocol/`
- 自定义记录存储（CustomRecordStore） - `tests/DnsCore.Tests/Services/`
- **泛域名匹配**（9 个专门测试用例）
  - 基本泛域名匹配
  - 精确匹配优先级
  - 多级泛域名
  - 最具体泛域名优先
  - 大小写不敏感
  - 深层子域名匹配

测试框架：
- xUnit
- FluentAssertions（流畅断言）
- Moq（模拟框架）

详细测试报告：`docs/TEST_REPORT.md`

## 性能优化

项目经过全面的性能优化，详见 `docs/PERFORMANCE_OPTIMIZATION.md`。

**优化成果：**
- 📈 吞吐量提升 50-100%
- 📉 内存使用降低 30-50%
- 📉 GC 压力降低 40-60%
- ⚡ 缓存命中响应时间降低 80-95%

**优化技术：**
1. **DNS 协议解析优化** - 使用 Span<T> 和 ArrayPool
2. **DNS 查询缓存** - LRU 缓存策略，根据 TTL 自动过期
3. **TCP 缓冲区优化** - ArrayPool 复用内存
4. **UdpClient 复用** - 单例模式减少资源创建/销毁

## C# 13 语言特性

项目充分利用了 C# 13 的最新特性：

1. **Primary Constructors** - 所有服务类使用主构造函数
   ```csharp
   public sealed class DnsServer(
       ILogger<DnsServer> logger,
       CustomRecordStore customRecordStore,
       UpstreamDnsResolver upstreamResolver,
       DnsServerOptions options)
   ```

2. **Collection Expressions** - 使用 `[]` 初始化集合
   ```csharp
   List<DnsQuestion> questions = [];
   ```

3. **Required Properties** - 模型使用 required 属性
   ```csharp
   public required string Domain { get; init; }
   ```

4. **Pattern Matching** - 使用 property patterns
   ```csharp
   if (answers is { Count: > 0 })
   ```

5. **Expression-bodied Members** - 简化方法定义
   ```csharp
   private static ushort ReadUInt16(byte[] data, int offset) =>
       (ushort)((data[offset] << 8) | data[offset + 1]);
   ```

## 泛域名功能

### 使用方法

泛域名使用 `*` 通配符，可以匹配任意子域名：

```json
{
  "Domain": "*.example.com",
  "Type": "A",
  "Value": "192.168.1.100",
  "TTL": 3600
}
```

### 匹配优先级

1. **精确匹配** - 最高优先级
   - `www.example.com` (精确记录)

2. **最具体的泛域名** - 次优先级
   - `*.dev.example.com` (三级泛域名)

3. **较宽泛的泛域名** - 最低优先级
   - `*.example.com` (二级泛域名)

### 示例

```
记录配置:
- www.example.com -> 192.168.1.1 (精确)
- *.dev.example.com -> 10.0.0.1 (具体泛域名)
- *.example.com -> 192.168.1.2 (宽泛泛域名)

查询结果:
- www.example.com -> 192.168.1.1 (精确匹配)
- api.dev.example.com -> 10.0.0.1 (具体泛域名)
- shop.example.com -> 192.168.1.2 (宽泛泛域名)
- example.com -> 无匹配 (泛域名不匹配基础域名)
```

## 注意事项

- 监听 53 端口需要管理员/root 权限
- 在 Windows 上运行时必须使用"以管理员身份运行"
- 自定义记录优先级高于上游 DNS 查询
- 支持域名压缩指针，遵循 RFC 1035 规范
- **泛域名匹配规则**：
  - 精确匹配 > 最具体泛域名 > 较宽泛泛域名
  - 泛域名不匹配基础域名本身
  - 大小写不敏感
- HTTP API 默认在 5000 端口，可通过 `ASPNETCORE_URLS` 环境变量修改
- 所有 API 更改立即生效，无需重启服务器
- 使用 ConcurrentDictionary 确保线程安全的记录存储
