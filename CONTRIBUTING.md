# 贡献指南

感谢您对 DNS Core Server 项目的关注！我们欢迎所有形式的贡献。

## 如何贡献

### 报告问题

如果您发现了 bug 或有功能建议：

1. 在 [Issues](../../issues) 中搜索，确保问题尚未被报告
2. 创建新 issue，提供详细描述：
   - Bug 报告应包含：复现步骤、期望行为、实际行为、环境信息
   - 功能建议应包含：使用场景、期望功能、可能的实现方案

### 提交代码

1. **Fork 项目**
   ```bash
   git clone https://github.com/your-username/dns-core.git
   cd dns-core
   ```

2. **创建分支**
   ```bash
   git checkout -b feature/your-feature-name
   # 或
   git checkout -b fix/your-bug-fix
   ```

3. **开发**
   - 遵循项目的代码风格（参考 `.editorconfig`）
   - 为新功能编写测试
   - 确保所有测试通过：`dotnet test`
   - 更新相关文档

4. **提交更改**
   ```bash
   git add .
   git commit -m "feat: 添加某个功能"
   ```

   提交信息格式：
   - `feat`: 新功能
   - `fix`: Bug 修复
   - `docs`: 文档更新
   - `test`: 测试相关
   - `refactor`: 代码重构
   - `chore`: 构建/工具相关

5. **推送并创建 Pull Request**
   ```bash
   git push origin feature/your-feature-name
   ```

## 开发指南

### 环境要求

- .NET 8.0 SDK 或更高版本
- 推荐使用 Visual Studio 2022 或 Visual Studio Code

### 项目结构

```
dns-core/
├── src/DnsCore/          # 主项目源代码
│   ├── Configuration/    # 配置相关
│   ├── Models/          # 数据模型
│   ├── Protocol/        # DNS 协议实现
│   └── Services/        # 核心服务
├── tests/               # 测试项目
│   └── DnsCore.Tests/   # 单元测试
└── docs/                # 文档
```

### 代码规范

- 使用 C# 命名约定
- 公共 API 必须有 XML 文档注释
- 保持代码简洁和可读
- 单个方法不超过 50 行（复杂逻辑除外）
- 遵循 SOLID 原则

### 测试要求

- 新功能必须包含单元测试
- 测试覆盖率应不低于 80%
- 测试应该快速、独立、可重复

运行测试：
```bash
dotnet test
```

### 构建和运行

```bash
# 构建
dotnet build

# 运行
dotnet run --project src/DnsCore

# 运行测试
dotnet test
```

## 代码审查

所有 Pull Request 都需要经过代码审查。审查标准：

- ✅ 代码质量和风格
- ✅ 测试覆盖率
- ✅ 文档完整性
- ✅ 性能影响
- ✅ 安全性考虑

## 行为准则

- 尊重所有贡献者
- 欢迎建设性批评
- 专注于最佳技术解决方案
- 保持友好和专业

## 许可证

通过贡献代码，您同意您的贡献将在 MIT 许可证下发布。

## 获取帮助

如有疑问，可以：
- 创建 Issue 提问
- 查阅项目文档
- 查看 `CLAUDE.md` 了解项目架构

感谢您的贡献！🎉
