# 泛域名 DNS 记录使用指南

## 概述

泛域名（Wildcard DNS）允许您使用单个 DNS 记录来匹配多个子域名。例如，`*.example.com` 可以匹配 `www.example.com`、`api.example.com`、`blog.example.com` 等所有子域名。

## 基本语法

泛域名使用星号 (`*`) 作为通配符：

```
*.example.com
*.dev.example.com
*.api.example.com
```

## 匹配规则

### 1. 精确匹配优先

当同时存在精确记录和泛域名记录时，**精确记录总是优先**。

**示例:**
```
记录 1: www.example.com    -> 192.168.1.100 (精确)
记录 2: *.example.com       -> 192.168.1.200 (泛域名)

查询 www.example.com        -> 192.168.1.100 ✓ (返回精确记录)
查询 api.example.com        -> 192.168.1.200 ✓ (匹配泛域名)
```

### 2. 最具体的泛域名优先

当有多个泛域名记录时，**最具体的泛域名优先匹配**。

**示例:**
```
记录 1: *.example.com       -> 192.168.1.1 (二级泛域名)
记录 2: *.dev.example.com   -> 10.0.0.1    (三级泛域名)

查询 api.dev.example.com    -> 10.0.0.1    ✓ (匹配更具体的泛域名)
查询 www.example.com        -> 192.168.1.1 ✓ (匹配二级泛域名)
```

### 3. 不匹配基础域名

泛域名**不会匹配基础域名本身**。

**示例:**
```
记录: *.example.com         -> 192.168.1.100

查询 www.example.com        -> 192.168.1.100 ✓
查询 example.com            -> 无结果      ✗ (不匹配基础域名)
```

要使 `example.com` 也能解析，需要单独添加：
```
记录 1: example.com         -> 192.168.1.100
记录 2: *.example.com       -> 192.168.1.100
```

### 4. 匹配任意深度的子域名

泛域名可以匹配**任意深度的子域名**。

**示例:**
```
记录: *.example.com         -> 192.168.1.100

查询 www.example.com                -> 192.168.1.100 ✓
查询 api.v1.example.com             -> 192.168.1.100 ✓
查询 a.b.c.d.e.example.com          -> 192.168.1.100 ✓
```

### 5. 大小写不敏感

泛域名匹配**不区分大小写**。

**示例:**
```
记录: *.Example.COM         -> 192.168.1.100

查询 www.example.com        -> 192.168.1.100 ✓
查询 API.EXAMPLE.COM        -> 192.168.1.100 ✓
查询 Test.Example.Com       -> 192.168.1.100 ✓
```

## 使用场景

### 场景 1: 开发环境

为所有开发子域名提供统一的 IP 地址：

```json
{
  "Domain": "*.dev.local",
  "Type": "A",
  "Value": "192.168.100.1",
  "TTL": 300
}
```

这样 `api.dev.local`、`web.dev.local`、`db.dev.local` 都会解析到同一个 IP。

### 场景 2: 微服务架构

为所有微服务子域名配置统一入口：

```json
{
  "Domain": "*.services.example.com",
  "Type": "A",
  "Value": "10.0.0.100",
  "TTL": 3600
}
```

`user.services.example.com`、`order.services.example.com` 等都会解析到负载均衡器。

### 场景 3: 多租户系统

为每个租户提供独立子域名：

```json
{
  "Domain": "*.saas.example.com",
  "Type": "CNAME",
  "Value": "app-server.example.com",
  "TTL": 3600
}
```

`tenant1.saas.example.com`、`tenant2.saas.example.com` 都会指向主应用服务器。

### 场景 4: CDN 配置

为所有静态资源域名配置 CDN：

```json
{
  "Domain": "*.cdn.example.com",
  "Type": "CNAME",
  "Value": "cdn-edge.cloudfront.net",
  "TTL": 86400
}
```

## 完整示例

### 示例 1: 混合配置

```json
[
  {
    "Domain": "example.com",
    "Type": "A",
    "Value": "203.0.113.10",
    "TTL": 3600
  },
  {
    "Domain": "www.example.com",
    "Type": "CNAME",
    "Value": "example.com",
    "TTL": 3600
  },
  {
    "Domain": "*.example.com",
    "Type": "A",
    "Value": "203.0.113.20",
    "TTL": 3600
  },
  {
    "Domain": "*.api.example.com",
    "Type": "A",
    "Value": "203.0.113.30",
    "TTL": 3600
  }
]
```

**匹配结果:**
- `example.com` → `203.0.113.10` (精确匹配)
- `www.example.com` → `203.0.113.10` (CNAME 到 example.com)
- `blog.example.com` → `203.0.113.20` (泛域名 *.example.com)
- `v1.api.example.com` → `203.0.113.30` (更具体的泛域名)
- `shop.example.com` → `203.0.113.20` (泛域名 *.example.com)

### 示例 2: 通过 Web 界面添加

1. 打开浏览器访问 `http://localhost:5000`
2. 在"域名"字段输入 `*.dev.local`
3. 选择"类型" 为 `A`
4. 在"记录值"字段输入 `192.168.1.100`
5. 设置"TTL" 为 `3600`
6. 点击"添加记录"

### 示例 3: 通过 API 添加

```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "*.staging.example.com",
    "type": "A",
    "value": "10.0.0.50",
    "ttl": 1800
  }'
```

## 测试泛域名

### 使用 nslookup (Windows)

```cmd
nslookup api.dev.local 127.0.0.1
nslookup www.dev.local 127.0.0.1
nslookup test123.dev.local 127.0.0.1
```

### 使用 dig (Linux/Mac)

```bash
dig @127.0.0.1 api.dev.local
dig @127.0.0.1 www.dev.local
dig @127.0.0.1 test123.dev.local
```

### 使用 curl

```bash
# 添加泛域名记录
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{"domain":"*.test.local","type":"A","value":"127.0.0.1","ttl":300}'

# 查询特定记录
curl http://localhost:5000/api/dns/records/api.test.local/A
```

## 注意事项

### ✅ 推荐做法

1. **为基础域名单独添加记录**
   ```
   example.com      -> 203.0.113.10
   *.example.com    -> 203.0.113.20
   ```

2. **使用合理的 TTL 值**
   - 开发环境: 300-600 秒
   - 生产环境: 3600-7200 秒

3. **测试优先级**
   - 添加泛域名前先测试精确匹配
   - 确认匹配优先级符合预期

### ⚠️ 注意事项

1. **安全性**
   - 谨慎使用顶级泛域名（如 `*.com`）
   - 生产环境建议限制泛域名范围

2. **性能**
   - 泛域名查询会遍历可能的匹配
   - 精确记录性能更好

3. **DNS 缓存**
   - 客户端可能会缓存 DNS 结果
   - 修改记录后可能需要清除缓存

## 故障排查

### 问题 1: 泛域名不生效

**检查清单:**
- [ ] 确认域名格式正确（`*.example.com`）
- [ ] 检查是否有精确匹配记录
- [ ] 验证记录类型是否正确
- [ ] 查看服务器日志

### 问题 2: 基础域名无法解析

**原因:** 泛域名不匹配基础域名

**解决方案:** 为基础域名单独添加记录
```json
{
  "Domain": "example.com",
  "Type": "A",
  "Value": "192.168.1.100",
  "TTL": 3600
}
```

### 问题 3: 匹配了错误的泛域名

**原因:** 多个泛域名冲突

**解决方案:**
1. 检查所有泛域名记录
2. 确认匹配优先级
3. 使用更具体的泛域名

## 高级用法

### 组合使用多个记录类型

```json
[
  {
    "Domain": "*.example.com",
    "Type": "A",
    "Value": "192.168.1.100",
    "TTL": 3600
  },
  {
    "Domain": "*.example.com",
    "Type": "TXT",
    "Value": "v=spf1 include:_spf.google.com ~all",
    "TTL": 3600
  }
]
```

### 分层泛域名策略

```json
[
  {
    "Domain": "*.example.com",
    "Type": "A",
    "Value": "192.168.1.1",
    "TTL": 3600,
    "Comment": "一级子域名"
  },
  {
    "Domain": "*.dev.example.com",
    "Type": "A",
    "Value": "10.0.0.1",
    "TTL": 300,
    "Comment": "开发环境"
  },
  {
    "Domain": "*.staging.example.com",
    "Type": "A",
    "Value": "10.0.1.1",
    "TTL": 600,
    "Comment": "预发布环境"
  }
]
```

## 相关资源

- [RFC 4592 - The Role of Wildcards in the Domain Name System](https://tools.ietf.org/html/rfc4592)
- [Web 界面使用指南](WEB_INTERFACE_GUIDE.md)
- [项目 README](../README.md)
