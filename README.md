# DNS Core Server

一个简单易用的本地 DNS 服务器，带 Web 管理界面。适合统一管理内网域名、开发测试域名和 hosts 规则。

## 主要功能

- 可视化管理 DNS 记录
- 支持 A、AAAA、CNAME、TXT、MX、NS、SRV、PTR、CAA
- 支持泛域名，例如 `*.dev.local`
- 支持同一个域名多条 A/AAAA 记录
- 支持多 IP 权重轮询
- 支持 hosts 文件粘贴导入
- 支持从 hosts URL 导入，并可按周期自动同步
- 支持管理多个 hosts URL 来源
- 未命中本地记录时，可转发到上游 DNS
- 自动过滤重复 DNS 记录

## 快速启动

### 方式一：Docker 运行

```bash
docker run -d \
  --name dns-core \
  -p 53:53/udp \
  -p 53:53/tcp \
  -p 5000:5000 \
  -v ./data:/app/data \
  --restart unless-stopped \
  wwbgo/dns-core:latest
```

### 方式二：本地脚本运行

Windows 使用管理员身份运行：

```cmd
start-server.bat
```

Linux / macOS：

```bash
chmod +x start-server.sh
./start-server.sh
```

### 方式三：dotnet 运行

```bash
dotnet run --project src/DnsCore/DnsCore.csproj
```

Linux 监听 53 端口时需要 root：

```bash
sudo dotnet run --project src/DnsCore/DnsCore.csproj
```

## 访问地址

启动后打开：

- Web 管理界面：`http://localhost:5000`
- 健康检查：`http://localhost:5000/health`
- Swagger API 文档：`http://localhost:5000/swagger`

DNS 服务监听：

- UDP `53`
- TCP `53`

## Web 管理界面

页面顶部菜单：

- `监控`：查看 QPS、延迟、缓存和运行时长
- `DNS 记录`：添加、编辑、搜索和删除 DNS 记录
- `Hosts 导入`：导入 hosts 文件或 URL
- `上游 DNS`：配置转发上游、查询模式和超时

### DNS 记录

打开 `DNS 记录` 菜单后：

1. 填写域名、类型、记录值、TTL 和权重。
2. 点击 `添加记录`。
3. 在列表中可编辑或删除记录。
4. 搜索框可按域名、类型、记录值和权重过滤。

泛域名示例：

```text
域名：*.dev.local
类型：A
记录值：192.168.1.100
```

同一域名多条 A 记录：

```text
app.local A 192.168.1.10 权重 1
app.local A 192.168.1.11 权重 3
```

权重仅对 A/AAAA 多值记录生效。

### Hosts 导入

`Hosts 导入` 菜单支持：

- 粘贴 hosts 文本
- 选择本地 hosts 文件
- 从 `http/https` URL 导入
- 保存 hosts URL 来源，并设置同步周期和 TTL

hosts 文件格式：

```text
192.168.1.10 app.local alias.local
2001:db8::1 v6.local
```

导入时会自动过滤：

- 同域名
- 同记录类型
- 同记录值

的重复记录。

### 上游 DNS

在 `上游 DNS` 菜单中可以：

- 开启或关闭上游转发
- 添加多个上游 DNS 服务器
- 选择顺序尝试或并行竞速
- 设置超时时间

本地记录优先级始终高于上游 DNS。

## 常用 DNS 测试

Windows：

```cmd
nslookup app.local 127.0.0.1
```

Linux / macOS：

```bash
dig @127.0.0.1 app.local
```

如果本地没有匹配记录，并且关闭了上游转发，客户端会继续尝试系统配置的其他 DNS 服务器。

## 配置文件

主配置文件：

```text
src/DnsCore/appsettings.json
```

常用配置：

```json
{
  "DnsServer": {
    "Port": 53,
    "EnableUpstreamDnsQuery": true,
    "UpstreamDnsServers": [
      "223.5.5.5",
      "119.29.29.29"
    ],
    "CustomRecords": [
      {
        "Domain": "example.local",
        "Type": "A",
        "Value": "192.168.1.100",
        "TTL": 3600,
        "Weight": 1
      }
    ]
  }
}
```

常用配置项：

- `Port`：DNS 监听端口，默认 `53`
- `EnableUpstreamDnsQuery`：是否转发上游，默认 `true`
- `UpstreamDnsServers`：上游 DNS 地址列表，留空使用系统 DNS
- `CustomRecords`：启动时加载的初始记录
- `HostsImport:AllowLoopback`：是否允许导入 `localhost` / `127.0.0.1` URL，默认 `false`

## 管理 API 安全

生产环境建议设置 API Key：

Windows：

```cmd
set DNSCORE_API_KEY=你的密钥
```

Linux / macOS：

```bash
export DNSCORE_API_KEY=你的密钥
```

设置后，Web 管理界面会提示输入 API Key。

## 常用 API

| 操作 | 方法 | 地址 |
| --- | --- | --- |
| 获取记录 | `GET` | `/api/dns/records` |
| 添加记录 | `POST` | `/api/dns/records` |
| 更新记录 | `PUT` | `/api/dns/records/{domain}/{type}` |
| 删除记录 | `DELETE` | `/api/dns/records/{domain}/{type}` |
| 清空记录 | `DELETE` | `/api/dns/records` |
| 获取 hosts 来源 | `GET` | `/api/hosts/sources` |
| 添加 hosts 来源 | `POST` | `/api/hosts/sources` |
| 删除 hosts 来源 | `DELETE` | `/api/hosts/sources/{id}` |
| 导入 hosts | `POST` | `/api/hosts/import` |

详细 API 示例见 [docs/API_EXAMPLES.md](docs/API_EXAMPLES.md)。

## 更多文档

- [Web 界面使用指南](docs/WEB_INTERFACE_GUIDE.md)
- [泛域名使用指南](docs/WILDCARD_DNS_GUIDE.md)
- [API 使用示例](docs/API_EXAMPLES.md)
- [容器部署指南](DOCKER.md)

## 许可证

MIT License
