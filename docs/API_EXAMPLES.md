# DNS Core Server - API 使用示例

完整的 RESTful API 使用示例和最佳实践。

## 目录

- [基础操作](#基础操作)
- [高级场景](#高级场景)
- [批量操作](#批量操作)
- [错误处理](#错误处理)
- [性能优化](#性能优化)

---

## 基础操作

### 1. 健康检查

**检查服务器状态**

```bash
# Linux/Mac
curl http://localhost:5000/health

# Windows PowerShell
Invoke-WebRequest -Uri http://localhost:5000/health
```

**响应示例:**
```json
{
  "status": "Healthy",
  "totalRecords": 5
}
```

---

### 2. 获取所有 DNS 记录

**请求:**
```bash
curl http://localhost:5000/api/dns/records
```

**响应示例:**
```json
[
  {
    "domain": "example.local",
    "type": "A",
    "value": "192.168.1.100",
    "ttl": 3600
  },
  {
    "domain": "*.dev.local",
    "type": "A",
    "value": "10.0.0.1",
    "ttl": 300
  }
]
```

---

### 3. 添加 DNS 记录

#### 3.1 添加 A 记录（IPv4）

```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "web.local",
    "type": "A",
    "value": "192.168.1.10",
    "ttl": 3600
  }'
```

#### 3.2 添加 AAAA 记录（IPv6）

```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "web6.local",
    "type": "AAAA",
    "value": "2001:db8::1",
    "ttl": 3600
  }'
```

#### 3.3 添加 CNAME 记录

```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "www.example.local",
    "type": "CNAME",
    "value": "example.local",
    "ttl": 7200
  }'
```

#### 3.4 添加 TXT 记录

```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "example.local",
    "type": "TXT",
    "value": "v=spf1 include:_spf.google.com ~all",
    "ttl": 3600
  }'
```

#### 3.5 添加 MX 记录

```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "example.local",
    "type": "MX",
    "value": "10 mail.example.local",
    "ttl": 3600
  }'
```

---

### 4. 查询特定记录

**请求:**
```bash
curl http://localhost:5000/api/dns/records/example.local/A
```

**响应示例:**
```json
[
  {
    "domain": "example.local",
    "type": "A",
    "value": "192.168.1.100",
    "ttl": 3600
  }
]
```

---

### 5. 删除 DNS 记录

**请求:**
```bash
curl -X DELETE http://localhost:5000/api/dns/records/example.local/A
```

**响应:**
```
204 No Content
```

---

### 6. 清空所有记录

**警告：此操作会删除所有自定义 DNS 记录！**

```bash
curl -X DELETE http://localhost:5000/api/dns/records
```

---

## 高级场景

### 场景 1: 泛域名配置

**配置开发环境泛域名**

```bash
# 所有 *.dev.local 解析到本地开发服务器
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "*.dev.local",
    "type": "A",
    "value": "127.0.0.1",
    "ttl": 60
  }'

# 测试：api.dev.local, www.dev.local 都会解析到 127.0.0.1
```

**多级泛域名**

```bash
# 配置 *.api.dev.local
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "*.api.dev.local",
    "type": "A",
    "value": "192.168.1.50",
    "ttl": 300
  }'

# v1.api.dev.local -> 192.168.1.50
# v2.api.dev.local -> 192.168.1.50
```

---

### 场景 2: 微服务环境配置

**配置多个微服务**

```bash
# API 服务
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "api.myapp.local",
    "type": "A",
    "value": "192.168.1.10",
    "ttl": 300
  }'

# Web 服务
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "web.myapp.local",
    "type": "A",
    "value": "192.168.1.11",
    "ttl": 300
  }'

# 数据库
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "db.myapp.local",
    "type": "A",
    "value": "192.168.1.12",
    "ttl": 300
  }'
```

---

### 场景 3: 负载均衡（多 A 记录）

**同一域名配置多个 IP**

```bash
# 第一个 IP
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "lb.example.local",
    "type": "A",
    "value": "192.168.1.10",
    "ttl": 60
  }'

# 第二个 IP
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "lb.example.local",
    "type": "A",
    "value": "192.168.1.11",
    "ttl": 60
  }'

# DNS 查询会返回两个 IP，客户端可以轮询使用
```

---

## 批量操作

### PowerShell 批量导入脚本

**batch-import.ps1:**
```powershell
# DNS 记录批量导入脚本

$records = @(
    @{ domain = "app1.local"; type = "A"; value = "192.168.1.101"; ttl = 3600 },
    @{ domain = "app2.local"; type = "A"; value = "192.168.1.102"; ttl = 3600 },
    @{ domain = "app3.local"; type = "A"; value = "192.168.1.103"; ttl = 3600 },
    @{ domain = "*.dev.local"; type = "A"; value = "10.0.0.1"; ttl = 300 }
)

$baseUrl = "http://localhost:5000/api/dns/records"

foreach ($record in $records) {
    $body = $record | ConvertTo-Json

    try {
        $response = Invoke-WebRequest -Uri $baseUrl -Method POST `
            -ContentType "application/json" -Body $body

        Write-Host "✓ 已添加: $($record.domain) -> $($record.value)" -ForegroundColor Green
    }
    catch {
        Write-Host "✗ 失败: $($record.domain) - $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "`n批量导入完成！" -ForegroundColor Cyan
```

**使用方法:**
```powershell
.\batch-import.ps1
```

---

### Bash 批量导入脚本

**batch-import.sh:**
```bash
#!/bin/bash
# DNS 记录批量导入脚本

BASE_URL="http://localhost:5000/api/dns/records"

# 定义记录数组
declare -a RECORDS=(
    '{"domain":"app1.local","type":"A","value":"192.168.1.101","ttl":3600}'
    '{"domain":"app2.local","type":"A","value":"192.168.1.102","ttl":3600}'
    '{"domain":"app3.local","type":"A","value":"192.168.1.103","ttl":3600}'
    '{"domain":"*.dev.local","type":"A","value":"10.0.0.1","ttl":300}'
)

# 遍历并添加记录
for record in "${RECORDS[@]}"; do
    domain=$(echo $record | jq -r '.domain')
    value=$(echo $record | jq -r '.value')

    response=$(curl -s -X POST "$BASE_URL" \
        -H "Content-Type: application/json" \
        -d "$record" \
        -w "%{http_code}")

    http_code="${response: -3}"

    if [ "$http_code" == "200" ] || [ "$http_code" == "201" ]; then
        echo "✓ 已添加: $domain -> $value"
    else
        echo "✗ 失败: $domain (HTTP $http_code)"
    fi
done

echo ""
echo "批量导入完成！"
```

**使用方法:**
```bash
chmod +x batch-import.sh
./batch-import.sh
```

---

## 错误处理

### 常见错误代码

| HTTP 代码 | 说明 | 处理方法 |
|-----------|------|----------|
| 200 | 成功 | - |
| 400 | 请求格式错误 | 检查 JSON 格式和必需字段 |
| 404 | 记录不存在 | 确认域名和类型正确 |
| 500 | 服务器错误 | 查看服务器日志 |

### 错误示例

**错误请求（缺少必需字段）:**
```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "test.local"
  }'
```

**错误响应:**
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Type": ["The Type field is required."],
    "Value": ["The Value field is required."]
  }
}
```

---

## 性能优化

### 1. TTL 设置建议

| 场景 | 建议 TTL | 说明 |
|------|----------|------|
| 生产环境静态记录 | 86400 (24小时) | 减少 DNS 查询负载 |
| 开发环境 | 60-300 (1-5分钟) | 快速更新测试 |
| 负载均衡 | 60 (1分钟) | 快速故障转移 |
| 泛域名记录 | 300 (5分钟) | 平衡性能和灵活性 |

### 2. 使用 UDP vs TCP

**何时使用 UDP:**
- 标准 DNS 查询（< 512 字节）
- 需要低延迟
- 大多数客户端默认行为

**何时使用 TCP:**
- 响应大小 > 512 字节
- 传输大量 TXT 记录
- 区域传输（AXFR/IXFR）
- 安全要求较高的场景

**测试 TCP 查询:**
```bash
# 使用 dig 强制 TCP
dig @localhost +tcp example.local

# 使用 nslookup 强制 TCP（Windows）
nslookup -vc example.local localhost
```

---

## Python 集成示例

**dns_client.py:**
```python
import requests
import json

class DnsApiClient:
    def __init__(self, base_url="http://localhost:5000"):
        self.base_url = base_url
        self.api_url = f"{base_url}/api/dns/records"

    def add_record(self, domain, record_type, value, ttl=3600):
        """添加 DNS 记录"""
        data = {
            "domain": domain,
            "type": record_type,
            "value": value,
            "ttl": ttl
        }
        response = requests.post(self.api_url, json=data)
        response.raise_for_status()
        return response.json()

    def get_all_records(self):
        """获取所有记录"""
        response = requests.get(self.api_url)
        response.raise_for_status()
        return response.json()

    def get_record(self, domain, record_type):
        """查询特定记录"""
        url = f"{self.api_url}/{domain}/{record_type}"
        response = requests.get(url)
        response.raise_for_status()
        return response.json()

    def delete_record(self, domain, record_type):
        """删除记录"""
        url = f"{self.api_url}/{domain}/{record_type}"
        response = requests.delete(url)
        response.raise_for_status()
        return True

    def health_check(self):
        """健康检查"""
        response = requests.get(f"{self.base_url}/health")
        response.raise_for_status()
        return response.json()

# 使用示例
if __name__ == "__main__":
    client = DnsApiClient()

    # 健康检查
    print("服务器状态:", client.health_check())

    # 添加记录
    client.add_record("test.local", "A", "192.168.1.100", 3600)
    print("✓ 记录已添加")

    # 查询记录
    records = client.get_record("test.local", "A")
    print("查询结果:", records)

    # 获取所有记录
    all_records = client.get_all_records()
    print(f"总记录数: {len(all_records)}")
```

---

## JavaScript/Node.js 集成示例

**dns-client.js:**
```javascript
const axios = require('axios');

class DnsApiClient {
    constructor(baseUrl = 'http://localhost:5000') {
        this.baseUrl = baseUrl;
        this.apiUrl = `${baseUrl}/api/dns/records`;
    }

    async addRecord(domain, type, value, ttl = 3600) {
        const response = await axios.post(this.apiUrl, {
            domain,
            type,
            value,
            ttl
        });
        return response.data;
    }

    async getAllRecords() {
        const response = await axios.get(this.apiUrl);
        return response.data;
    }

    async getRecord(domain, type) {
        const response = await axios.get(`${this.apiUrl}/${domain}/${type}`);
        return response.data;
    }

    async deleteRecord(domain, type) {
        await axios.delete(`${this.apiUrl}/${domain}/${type}`);
        return true;
    }

    async healthCheck() {
        const response = await axios.get(`${this.baseUrl}/health`);
        return response.data;
    }
}

// 使用示例
(async () => {
    const client = new DnsApiClient();

    try {
        // 健康检查
        const health = await client.healthCheck();
        console.log('服务器状态:', health);

        // 添加记录
        await client.addRecord('test.local', 'A', '192.168.1.100', 3600);
        console.log('✓ 记录已添加');

        // 查询记录
        const records = await client.getRecord('test.local', 'A');
        console.log('查询结果:', records);

        // 获取所有记录
        const allRecords = await client.getAllRecords();
        console.log(`总记录数: ${allRecords.length}`);

    } catch (error) {
        console.error('错误:', error.message);
    }
})();
```

---

## 总结

### 最佳实践

1. **使用合适的 TTL 值** - 根据使用场景设置
2. **错误处理** - 始终检查 HTTP 状态码
3. **批量操作** - 使用脚本自动化管理
4. **监控** - 定期检查健康状态
5. **备份** - 定期导出 DNS 记录配置

### 快速参考

| 操作 | HTTP 方法 | 端点 |
|------|-----------|------|
| 健康检查 | GET | `/health` |
| 获取所有记录 | GET | `/api/dns/records` |
| 查询记录 | GET | `/api/dns/records/{domain}/{type}` |
| 添加记录 | POST | `/api/dns/records` |
| 删除记录 | DELETE | `/api/dns/records/{domain}/{type}` |
| 清空记录 | DELETE | `/api/dns/records` |

### 相关文档

- [README.md](../README.md) - 项目说明
- [WEB_INTERFACE_GUIDE.md](WEB_INTERFACE_GUIDE.md) - Web 界面使用指南
- [WILDCARD_DNS_GUIDE.md](WILDCARD_DNS_GUIDE.md) - 泛域名使用指南
- [DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md) - Docker 部署指南
