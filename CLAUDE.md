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
- O(1) LRU 缓存（双向链表 + 字典）- DNS 查询结果缓存，含否定缓存
- 上游查询每次使用独立 socket，支持顺序尝试（默认）与并行竞速两种模式
- 放大的 socket 缓冲 - 避免突发流量下内核丢包

**安全特性:**
- 管理 API 强制 API Key 鉴权（默认启用）+ 来源网段限制（默认仅本机）
- DNS 查询客户端网段 ACL（默认仅私有网段，避免成为开放解析器）
- 按客户端 IP 的令牌桶限流
- 上游应答校验随机 TXID / 源地址 / question，防缓存投毒
- 解析器全路径边界检查，畸形报文不产生异常风暴

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

### 容器构建（Docker / Podman 通用）

所有容器脚本会自动探测引擎，优先 `docker`，其次 `podman`；
探测逻辑在 `scripts/container-engine.sh` 与 `scripts/container-engine.bat`。

```bash
# 自动探测引擎构建
./docker-build.sh -t v1.0.0

# 强制指定引擎
CONTAINER_ENGINE=podman ./docker-build.sh

# 交互式容器管理（启停、日志、进入容器、compose）
./docker-run.sh

# compose（自动选择 podman compose / podman-compose / docker compose）
DNS_HOST_PORT=5353 WEB_HOST_PORT=8080 podman compose up -d
```

**Podman 特有差异（脚本已自动处理）：**

1. **镜像格式** —— Podman 默认输出 OCI 格式，`HEALTHCHECK` 是 Docker 格式的
   扩展，OCI 下会被静默丢弃。脚本对 podman 自动追加 `--format docker`。
   手工构建需自行加上，否则健康检查失效。
2. **特权端口** —— 容器以非 root 运行且需绑定 53 端口。镜像对 `dotnet`
   二进制设置了 `cap_net_bind_service` 文件能力；但 compose 的
   `no-new-privileges` 会阻止文件能力提升，因此 compose 中另需
   `cap_add: NET_BIND_SERVICE`（实测：缺失时 bind 抛 SocketException(13)）。
3. **bind mount 目录** —— Docker 自动创建缺失的挂载源目录，Podman 不会，
   会报 `statfs .../data: no such file or directory`。仓库保留
   `data/.gitkeep` 以确保目录存在。
4. **rootless** —— rootless Podman 无法绑定 53 端口，脚本会检测并提示
   三种处置方式（放宽 `ip_unprivileged_port_start` / 改用高端口 / rootful）。

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
   - 处理上游 DNS 查询，支持自定义上游服务器或使用系统 DNS
   - **每次查询使用独立且已 Connect 的 socket**
   - 两种查询模式（`Upstream:RaceUpstreams`）：
     - `false`（默认）**顺序尝试** — 按列表顺序逐个查询，失败才试下一个，列表次序即优先级
     - `true` **并行竞速** — 同时查询全部上游，取最先返回的应答
   - 使用随机 TXID，并校验应答的 TXID / 源地址 / question 是否匹配
   - 不转发客户端原始报文（避免客户端 TXID 外泄）

5.1 **UpstreamSettingsStore** (`src/DnsCore/Services/UpstreamSettingsStore.cs`)
   - 上游配置的运行时读写，支持通过 Web 界面/API 修改并**立即生效**（无需重启）
   - 生效原理：DnsServer 与 UpstreamDnsResolver 在每次查询时读取选项属性
     （而非构造时快照），因此改写单例 `DnsServerOptions` 即刻影响后续查询
   - 持久化到 `data/upstream-settings.json`（与 `appsettings.json` 分离：
     配置文件是部署期初始值，运行时改动写独立文件，避免回写配置文件丢注释、
     以及与容器只读挂载冲突）
   - 上游变更后自动清空 DNS 缓存（旧上游的结果可能与新上游不一致）
   - 校验：拒绝域名、拒绝本机地址（会形成查询环路）、拒绝重复项、
     **拒绝 inet_aton 简写**（`IPAddress.TryParse("223.5.5")` 会静默解析成
     `223.5.0.5`，上游少打一位会指向另一台服务器）

6. **DnsCache** (`src/DnsCore/Services/DnsCache.cs`)
   - O(1) LRU 缓存（双向链表 + 字典）
   - **返回的 TTL 按剩余存活时间递减**，避免客户端二次缓存过久
   - 支持否定缓存（NXDOMAIN / NODATA），TTL 上下限可配置
   - 返回记录副本，防止调用方修改污染缓存
   - 容量与各项 TTL 通过 `DnsServer:Cache` 配置

7. **DnsCacheCleanupService** (`src/DnsCore/Services/DnsCacheCleanupService.cs`)
   - 后台服务定期清理过期缓存，间隔可配置

8. **安全组件**
   - **ApiSecurityMiddleware** - 管理 API 鉴权（定长时间比较 Key）与来源网段限制
   - **NetworkAcl** - CIDR 网段匹配，正确处理 IPv4-mapped IPv6
   - **ClientRateLimiter** - 按客户端 IP 的令牌桶限流，自动回收空闲桶

8. **DnsMessageParser** (`src/DnsCore/Protocol/DnsMessageParser.cs`)
   - DNS 协议解析器
   - 处理 DNS 消息的序列化和反序列化
   - 支持域名压缩
   - **性能优化**：使用 Span<T> 和 ArrayPool<T> 减少内存分配

9. **Web 管理界面** (`src/DnsCore/wwwroot/`)
   - **index.html** - 单页应用界面，内联 SVG 图标精灵（不用 emoji，
     以保证跨平台渲染一致并可随字色变化）
   - **styles.css** - 设计令牌驱动的样式，深浅双主题
   - **app.js** - 完整的 CRUD 与配置管理逻辑
   - **标签页结构**（WAI-ARIA tabs 模式）：
     - **DNS 记录** — 添加表单 + 记录列表
     - **上游 DNS** — 转发开关、服务器列表、查询模式、超时
     - 标签上显示记录数徽标与"未保存改动"圆点（在另一标签页也能看到）
     - 支持方向键 / Home / End 导航，roving tabindex（Tab 键跳过未选中标签）
     - 所选标签持久化到 localStorage；未知值回落到默认标签
   - 其他特性：
     - 实时状态监控 + 缓存统计（条目数、命中率）
     - 实时搜索过滤，含结果计数与空状态
     - 深色主题跟随系统，可手动切换并持久化
     - 自动刷新（30 秒）；上游面板有未保存改动时不覆盖用户输入
     - Toast 可堆叠，401 去重（首屏并发请求只提示一次）
   - **无障碍**：文字对比度达 WCAG AA，交互元素命中区 ≥24px（2.5.8），
     支持 `prefers-reduced-motion`，键盘焦点可见（`:focus-visible`）
   - **响应式**：窄屏下表格转为卡片布局（`data-label` 生成行内标签），
     而非横向滚动

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
   - **先查询缓存**（含否定缓存，微秒级响应）
   - 缓存未命中，按配置的模式查询上游 DNS（默认顺序尝试，可切为并行竞速）
   - 成功：**缓存结果**并返回，沿用上游 RCODE（区分 NXDOMAIN 与 NODATA），AA 位置 0
   - 上游全部失败：返回 **SERVFAIL**（服务端故障，不可谎称域名不存在）
6. 如果未找到且 EnableUpstreamDnsQuery 为 false，返回 SERVFAIL
   - 客户端会自动尝试系统配置的下一个 DNS 服务器
7. 构建响应：计数按实际写入量回填、域名压缩、超出 UDP 上限时置 TC 位

**响应构建要点：**
- 响应头的 AN/NS/AR 计数必须清零后按实际写入量回填。沿用请求头的 ARCOUNT
  （EDNS0 OPT 会置 1）会让应答自称带附加记录而实际没有，客户端判定报文畸形
- 泛域名命中时，owner name 必须改写为客户端实际查询的域名，
  直接写 `*.example.com` 会因 owner 与 question 不匹配而被客户端丢弃
- 仅本地自定义记录置 AA 位，转发的上游应答不得声称权威

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
- 顶栏常驻服务状态指示灯（圆点 + 文字，健康时缓慢呼吸）
- 顶部四张统计卡片：当前 QPS（含 60 秒迷你趋势图）、平均延迟、缓存条目、缓存命中率
- 可折叠的「详细监控数据」面板：请求量与延迟各 6 项指标，折叠状态持久化
- **标签页「DNS 记录」**：
  - 添加记录表单（记录类型切换时联动 placeholder 与提示）
  - 记录列表（表头吸顶、类型标签着色、泛域名 `*.` 前缀高亮）
  - 搜索过滤、删除记录、清空所有记录
- **标签页「上游 DNS」**：
  - 转发总开关、上游服务器列表（标签式增删 + 常用公共 DNS 快捷添加）
  - 查询模式（顺序尝试 / 并行竞速）、超时设置
  - 显示当前实际生效的上游；保存前有"未保存改动"提示与二次确认警示

### RESTful API 端点

- `GET /health` - 健康检查
- `GET /api/dns/records` - 获取所有自定义记录
- `POST /api/dns/records` - 添加自定义记录
- `GET /api/dns/records/{domain}/{type}` - 查询指定记录
- `DELETE /api/dns/records/{domain}/{type}` - 删除指定记录
- `DELETE /api/dns/records` - 清空所有自定义记录
- `PUT /api/dns/records/{domain}/{type}` - 更新指定记录
- `GET /api/upstream` - 读取上游 DNS 配置（含实际生效的服务器列表）
- `PUT /api/upstream` - 修改上游 DNS 配置，**立即生效且持久化**
- `GET /api/cache/stats` - 缓存统计（条目数、命中率）
- `DELETE /api/cache` - 清空 DNS 缓存
- `GET /api/qps` - 查询量统计（各时间窗口聚合值 + 最近 60 秒逐秒序列）
- `GET /api/qps/latency` - 响应延迟统计（平均/极值/P50/P95/P99）
- `GET /swagger` - Swagger UI 文档（仅开发模式）

除 `/health` 外，所有 `/api/*` 端点都受 API Key 鉴权与来源网段限制保护。

### 上游 DNS 配置（Web 界面）

Web 管理界面的"上游 DNS 配置"面板可直接修改，保存后立即生效、无需重启：

- **转发总开关** — 关闭后未命中的查询直接返回 SERVFAIL
- **服务器列表** — 标签式增删，顺序模式下显示优先级序号；提供常用公共 DNS 快捷添加
- **查询模式** — 顺序尝试 / 并行竞速
- **超时** — 200–30000 毫秒
- 面板底部显示**当前实际生效的上游**；列表留空时会标注这些地址来自系统自动探测

## 测试

项目包含完整的单元测试（**226 个测试用例**，`dotnet test` 全绿），覆盖以下组件：
- **协议回归测试** - `tests/DnsCore.Tests/Protocol/DnsProtocolRegressionTests.cs`
  （计数回填、泛域名 owner name、畸形报文、label/域名校验、RCODE、截断、RDATA 编码、压缩）
- **服务回归测试** - `tests/DnsCore.Tests/Services/ServiceRegressionTests.cs`
  （缓存 TTL 递减/LRU 淘汰/否定缓存、记录存储并发一致性、网段 ACL、限流）
- **持久化契约测试** - `tests/DnsCore.Tests/Repositories/DnsRecordRepositoryTests.cs`
  （JSON/SQLite/LiteDB 三种实现跑同一套用例：往返保真、整组替换语义、
  按类型删除、重开实例后数据仍在、特殊字符与 Unicode）
- **统计回归测试** - `tests/DnsCore.Tests/Services/DnsQueryStatisticsTests.cs`
  （时间窗口边界、当前小时计入、跨分钟不重复计数、空闲不清空历史、槽位复用不读陈旧值）
- **延迟统计测试** - `tests/DnsCore.Tests/Services/DnsLatencyStatisticsTests.cs`
  （NaN/负值/无穷拒绝、环形窗口淘汰、累计极值不随淘汰丢失、百分位、并发读写）
- **上游设置测试** - `tests/DnsCore.Tests/Services/UpstreamSettingsTests.cs`
  （默认顺序模式、IP 严格校验含 inet_aton 简写、环路防护、运行时生效、
  持久化与重载、损坏/非法持久化文件的容错）
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
- 域名压缩（RFC 1035 §4.1.4）读写双向支持；SRV/SOA 的 target 按规范不压缩
- **泛域名匹配规则**：
  - 精确匹配 > 最具体泛域名 > 较宽泛泛域名
  - 泛域名不匹配基础域名本身
  - 大小写不敏感
  - 应答时 owner name 改写为实际查询名
- HTTP API 默认在 5000 端口，可通过 `ASPNETCORE_URLS` 环境变量修改
- 所有 API 更改立即生效，无需重启服务器
- 记录存储使用 `ConcurrentDictionary<string, ImmutableList<DnsRecord>>`，
  整体替换而非原地修改（可变 List 会被写入与查询线程并发访问）

## 安全配置（必读）

**管理 API 默认要求 API Key**，通过环境变量提供：

```bash
export DNSCORE_API_KEY=<your-key>     # Linux/Mac
set DNSCORE_API_KEY=<your-key>        # Windows
```

启用鉴权但未设置 Key 时服务会**拒绝启动**，而不是静默放行管理接口。
仅在完全可信的隔离网络中才可设置 `ApiSecurity:RequireApiKey=false`。

其他默认值：
- 管理 API 来源限制：仅 `127.0.0.1` / `::1`
- DNS 查询客户端限制：仅私有网段（放开 `AllowedClientNetworks` 前请确认不会成为开放解析器）
- 单 IP 限流：1000 QPS
- 逐查询日志默认关闭（`LogEveryQuery`），开启会记录所有客户端的查询历史
