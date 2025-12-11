# 项目结构说明

## 目录结构

```
dns-core/
│
├── src/                          # 源代码目录
│   └── DnsCore/                  # 主项目
│       ├── Configuration/        # 配置管理
│       │   └── DnsServerOptions.cs
│       ├── Models/              # 数据模型
│       │   ├── DnsHeader.cs     # DNS 消息头
│       │   ├── DnsQuestion.cs   # DNS 查询问题
│       │   ├── DnsRecord.cs     # DNS 记录
│       │   └── DnsRecordType.cs # DNS 记录类型枚举
│       ├── Protocol/            # DNS 协议实现
│       │   └── DnsMessageParser.cs  # DNS 消息解析器
│       ├── Services/            # 核心服务
│       │   ├── CustomRecordStore.cs    # 自定义记录存储
│       │   ├── DnsServer.cs            # DNS 服务器核心
│       │   └── UpstreamDnsResolver.cs  # 上游 DNS 解析器
│       ├── Program.cs           # 程序入口
│       ├── DnsCore.csproj       # 项目文件
│       ├── appsettings.json     # 配置文件（需要创建）
│       └── appsettings.example.json  # 配置示例
│
├── tests/                       # 测试目录
│   └── DnsCore.Tests/          # 单元测试项目
│       ├── Models/             # 模型测试
│       │   ├── DnsHeaderTests.cs
│       │   ├── DnsQuestionTests.cs
│       │   └── DnsRecordTests.cs
│       ├── Protocol/           # 协议测试
│       │   └── DnsMessageParserTests.cs
│       ├── Services/           # 服务测试
│       │   └── CustomRecordStoreTests.cs
│       └── DnsCore.Tests.csproj
│
├── docs/                       # 文档目录
│   ├── PROJECT_STRUCTURE.md    # 本文件
│   └── TEST_REPORT.md         # 测试报告
│
├── .claude/                    # Claude Code 配置
├── .editorconfig              # 编辑器配置
├── .gitignore                 # Git 忽略文件
├── CLAUDE.md                  # Claude Code 指南
├── CONTRIBUTING.md            # 贡献指南
├── DnsCore.sln               # 解决方案文件
├── LICENSE                    # MIT 许可证
└── README.md                  # 项目说明
```

## 项目层次

### 1. 解决方案层 (Solution)

- **DnsCore.sln**: 解决方案文件，包含所有项目
  - DnsCore (主项目)
  - DnsCore.Tests (测试项目)

### 2. 主项目 (src/DnsCore)

#### Configuration (配置层)
- 负责配置管理和选项类定义
- `DnsServerOptions.cs`: DNS 服务器配置选项

#### Models (模型层)
- 定义 DNS 协议相关的数据结构
- `DnsHeader.cs`: DNS 消息头，包含事务 ID、标志位等
- `DnsQuestion.cs`: DNS 查询问题
- `DnsRecord.cs`: DNS 资源记录
- `DnsRecordType.cs`: DNS 记录类型枚举

#### Protocol (协议层)
- 实现 DNS 协议的编解码
- `DnsMessageParser.cs`: DNS 消息解析器，负责序列化和反序列化

#### Services (服务层)
- 核心业务逻辑实现
- `DnsServer.cs`: DNS 服务器主服务，协调各组件
- `CustomRecordStore.cs`: 自定义 DNS 记录的存储和查询
- `UpstreamDnsResolver.cs`: 上游 DNS 查询和响应解析

#### 程序入口
- `Program.cs`: 应用程序入口点，初始化和启动服务

### 3. 测试项目 (tests/DnsCore.Tests)

遵循与主项目相同的目录结构，每个命名空间都有对应的测试：

- **Models/**: 测试数据模型的序列化、反序列化和基本功能
- **Protocol/**: 测试 DNS 协议解析和构建
- **Services/**: 测试服务层的业务逻辑

## 依赖关系

```
Program.cs
    └─> DnsServer (Services)
        ├─> CustomRecordStore (Services)
        ├─> UpstreamDnsResolver (Services)
        ├─> DnsMessageParser (Protocol)
        └─> DnsServerOptions (Configuration)

DnsMessageParser (Protocol)
    └─> Models (DnsHeader, DnsQuestion, DnsRecord)

CustomRecordStore (Services)
    └─> Models (DnsRecord, DnsRecordType)

UpstreamDnsResolver (Services)
    ├─> Models (DnsRecord, DnsRecordType)
    └─> DnsMessageParser (Protocol)
```

## 数据流

### DNS 查询处理流程

```
1. 客户端请求 (UDP)
   ↓
2. DnsServer.ProcessRequestAsync()
   ↓
3. DnsMessageParser.ParseQuery()
   ↓
4. CustomRecordStore.Query()
   ├─ 找到 → 返回自定义记录
   └─ 未找到 → UpstreamDnsResolver.QueryAsync()
       ↓
5. DnsMessageParser.BuildResponse()
   ↓
6. 返回响应给客户端
```

## 配置文件

### appsettings.json

```json
{
  "DnsServer": {
    "Port": 53,
    "UpstreamDnsServers": ["8.8.8.8", "1.1.1.1"],
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
      "Default": "Information"
    }
  }
}
```

## 构建输出

### Debug 模式
- 输出目录: `src/DnsCore/bin/Debug/net8.0/`
- 测试输出: `tests/DnsCore.Tests/bin/Debug/net8.0/`

### Release 模式
- 输出目录: `src/DnsCore/bin/Release/net8.0/`
- 可发布目录: `./publish/`

## 开发工作流

1. **克隆项目**
   ```bash
   git clone <repository-url>
   cd dns-core
   ```

2. **还原依赖**
   ```bash
   dotnet restore DnsCore.sln
   ```

3. **构建项目**
   ```bash
   dotnet build DnsCore.sln
   ```

4. **运行测试**
   ```bash
   dotnet test
   ```

5. **运行服务器**
   ```bash
   dotnet run --project src/DnsCore/DnsCore.csproj
   ```

## 扩展指南

### 添加新的 DNS 记录类型

1. 在 `DnsRecordType.cs` 中添加枚举值
2. 在 `DnsMessageParser.cs` 中添加解析和构建逻辑
3. 在 `tests/` 中添加相应的测试用例

### 添加新的服务

1. 在 `src/DnsCore/Services/` 中创建新服务类
2. 在 `Program.cs` 中注册服务
3. 在 `tests/DnsCore.Tests/Services/` 中添加测试

### 添加新的配置选项

1. 在 `DnsServerOptions.cs` 中添加属性
2. 在 `appsettings.json` 和 `appsettings.example.json` 中添加配置项
3. 在相应的服务中使用新配置

## 代码规范

- 遵循 `.editorconfig` 中定义的代码风格
- 使用 C# 命名约定（PascalCase 用于类型和方法）
- 公共 API 必须有 XML 文档注释
- 单个方法不超过 50 行（复杂逻辑除外）
- 所有新功能必须包含单元测试

## 性能考虑

- DNS 查询处理是异步的，不会阻塞服务器
- 自定义记录使用字典存储，查询时间复杂度 O(1)
- 上游 DNS 查询有 5 秒超时限制
- 支持并发处理多个 DNS 请求

## 安全考虑

- 不记录敏感信息
- 验证 DNS 消息格式，防止恶意数据
- 限制域名压缩指针跳转次数，防止循环
- 管理员权限运行，需要适当的访问控制

---

**最后更新**: 2025-12-10
