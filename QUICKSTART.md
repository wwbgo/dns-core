# DNS Core Server - 快速开始指南

5分钟快速上手 DNS Core Server！

## 🚀 最快启动方式

### 方式 1: Docker（推荐）

**仅需 2 步:**

```bash
# 1. 启动服务
docker-start.bat         # Windows
./docker-start.sh        # Linux/Mac

# 2. 打开浏览器
浏览器访问: http://localhost:5000
```

**就这么简单！✨**

---

### 方式 2: 本地运行

**前置要求:**
- .NET 10.0 SDK

**3 步启动:**

```bash
# 1. 构建项目
dotnet build DnsCore.sln

# 2. 启动服务器（需要管理员权限）
# Windows: 以管理员身份运行
dotnet run --project src/DnsCore/DnsCore.csproj

# Linux/Mac
sudo dotnet run --project src/DnsCore/DnsCore.csproj

# 3. 访问管理界面
浏览器访问: http://localhost:5000
```

---

## 📋 第一次使用

### 1. 添加第一条 DNS 记录

**方法 A: 使用 Web 界面（最简单）**

1. 打开 http://localhost:5000
2. 填写表单：
   - 域名: `test.local`
   - 类型: `A`
   - 记录值: `192.168.1.100`
   - TTL: `3600`
3. 点击"添加记录"

**方法 B: 使用 API**

```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "test.local",
    "type": "A",
    "value": "192.168.1.100",
    "ttl": 3600
  }'
```

### 2. 测试 DNS 解析

**Windows:**
```cmd
nslookup test.local 127.0.0.1
```

**Linux/Mac:**
```bash
dig @127.0.0.1 test.local
```

**预期结果:**
```
Name:    test.local
Address: 192.168.1.100
```

✅ **恭喜！您的 DNS 服务器已正常工作！**

---

## 🎯 常见使用场景

### 场景 1: 本地开发环境

**配置本地域名解析:**

```bash
# 添加开发域名
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "myapp.local",
    "type": "A",
    "value": "127.0.0.1",
    "ttl": 60
  }'

# 现在可以访问 http://myapp.local
```

---

### 场景 2: 泛域名开发环境

**所有 *.dev.local 解析到本地:**

```bash
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{
    "domain": "*.dev.local",
    "type": "A",
    "value": "127.0.0.1",
    "ttl": 60
  }'

# 现在可以访问:
# - api.dev.local
# - web.dev.local
# - admin.dev.local
# 等等，所有子域名都会解析到 127.0.0.1
```

---

### 场景 3: 微服务环境

**快速配置多个服务:**

```bash
# API 服务
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{"domain":"api.myapp.local","type":"A","value":"192.168.1.10","ttl":300}'

# Web 服务
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{"domain":"web.myapp.local","type":"A","value":"192.168.1.11","ttl":300}'

# 数据库
curl -X POST http://localhost:5000/api/dns/records \
  -H "Content-Type: application/json" \
  -d '{"domain":"db.myapp.local","type":"A","value":"192.168.1.12","ttl":300}'
```

---

## 🔧 配置系统 DNS

### Windows

**方法 1: 图形界面**

1. 控制面板 → 网络和共享中心 → 更改适配器设置
2. 右键网络连接 → 属性
3. 双击"Internet 协议版本 4 (TCP/IPv4)"
4. 选择"使用下面的 DNS 服务器地址"
5. 首选 DNS 服务器: `127.0.0.1`
6. 备用 DNS 服务器: `8.8.8.8`（可选）

**方法 2: PowerShell（管理员）**

```powershell
# 查看网络接口
Get-NetAdapter

# 设置 DNS（替换 "以太网" 为你的接口名称）
Set-DnsClientServerAddress -InterfaceAlias "以太网" -ServerAddresses ("127.0.0.1","8.8.8.8")
```

---

### Linux

**Ubuntu/Debian (使用 systemd-resolved):**

```bash
# 编辑配置
sudo nano /etc/systemd/resolved.conf

# 修改以下行:
[Resolve]
DNS=127.0.0.1
FallbackDNS=8.8.8.8

# 重启服务
sudo systemctl restart systemd-resolved
```

**CentOS/RHEL:**

```bash
# 编辑网络配置
sudo nano /etc/sysconfig/network-scripts/ifcfg-eth0

# 添加:
DNS1=127.0.0.1
DNS2=8.8.8.8

# 重启网络
sudo systemctl restart NetworkManager
```

---

### macOS

**图形界面:**

1. 系统偏好设置 → 网络
2. 选择活动网络连接 → 高级
3. DNS 标签页
4. 点击 "+" 添加 `127.0.0.1`
5. 点击"好"

**命令行:**

```bash
# 获取网络服务名称
networksetup -listallnetworkservices

# 设置 DNS（替换 "Wi-Fi" 为实际名称）
sudo networksetup -setdnsservers "Wi-Fi" 127.0.0.1 8.8.8.8

# 清除 DNS 缓存
sudo dscacheutil -flushcache
sudo killall -HUP mDNSResponder
```

---

## 📊 验证安装

运行验证脚本检查所有功能:

```bash
# Windows
verify-功能.bat

# Linux/Mac
chmod +x verify-功能.sh
./verify-功能.sh
```

**输出示例:**
```
========================================
  DNS Core Server - 功能验证
========================================

[1/6] 检查 .NET 环境...
✓ .NET 环境正常

[2/6] 清理并构建项目...
✓ 项目构建成功

[3/6] 运行单元测试...
✓ 所有测试通过

[4/6] 检查项目文件完整性...
✓ 项目文件完整

[5/6] 验证配置文件...
✓ 配置文件存在

[6/6] 验证文档完整性...
✓ 文档文件完整

========================================
  验证完成！
========================================

项目状态: ✓ 就绪
```

---

## 🎓 下一步学习

### 基础文档
1. [README.md](README.md) - 完整项目文档
2. [WEB_INTERFACE_GUIDE.md](docs/WEB_INTERFACE_GUIDE.md) - Web 界面详细指南
3. [API_EXAMPLES.md](docs/API_EXAMPLES.md) - API 使用示例

### 高级功能
1. [WILDCARD_DNS_GUIDE.md](docs/WILDCARD_DNS_GUIDE.md) - 泛域名使用指南
2. [DOCKER_DEPLOYMENT.md](docs/DOCKER_DEPLOYMENT.md) - Docker 部署指南

---

## 🐛 常见问题

### Q1: 端口 53 被占用

**错误信息:**
```
Error: Failed to bind to address http://+:53
```

**解决方案:**

**Windows:**
```powershell
# 查看占用端口的进程
netstat -ano | findstr :53

# 停止 DNS 客户端服务（临时）
net stop dnscache

# 或使用高端口运行（修改 appsettings.json）
{
  "DnsServer": {
    "Port": 5353  // 改为高端口
  }
}
```

**Linux:**
```bash
# 查看占用端口的进程
sudo lsof -i :53

# 停止 systemd-resolved（如果需要）
sudo systemctl stop systemd-resolved
```

---

### Q2: 权限不足

**错误信息:**
```
Permission denied when binding to port 53
```

**解决方案:**

**Windows:** 以管理员身份运行

**Linux:**
```bash
# 方法 1: 使用 sudo
sudo dotnet run --project src/DnsCore/DnsCore.csproj

# 方法 2: 设置端口绑定权限
sudo setcap 'cap_net_bind_service=+ep' /usr/bin/dotnet

# 方法 3: 使用 Docker（推荐）
./docker-start.sh
```

---

### Q3: DNS 查询不工作

**检查步骤:**

1. **验证服务器运行:**
   ```bash
   curl http://localhost:5000/health
   ```

2. **检查记录是否添加:**
   ```bash
   curl http://localhost:5000/api/dns/records
   ```

3. **测试 DNS 查询:**
   ```bash
   # 明确指定 DNS 服务器
   nslookup test.local 127.0.0.1
   ```

4. **检查防火墙:**
   ```bash
   # Windows
   netsh advfirewall firewall add rule name="DNS Server" dir=in action=allow protocol=UDP localport=53

   # Linux
   sudo ufw allow 53/udp
   sudo ufw allow 53/tcp
   ```

---

### Q4: Web 界面无法访问

**检查步骤:**

1. **验证服务器运行:**
   ```bash
   curl http://localhost:5000/health
   ```

2. **检查端口占用:**
   ```bash
   # Windows
   netstat -ano | findstr :5000

   # Linux
   sudo lsof -i :5000
   ```

3. **检查防火墙:**
   ```bash
   # Windows
   netsh advfirewall firewall add rule name="DNS Web UI" dir=in action=allow protocol=TCP localport=5000

   # Linux
   sudo ufw allow 5000/tcp
   ```

---

## 📞 获取帮助

- **文档:** 查看 `docs/` 目录下的详细文档
- **API 文档:** http://localhost:5000/swagger
- **示例代码:** [docs/API_EXAMPLES.md](docs/API_EXAMPLES.md)

---

## 🎉 快速开始完成！

现在您已经成功设置并运行了 DNS Core Server！

**建议下一步:**

1. ✅ 添加几条测试记录
2. ✅ 配置系统 DNS 指向本地服务器
3. ✅ 测试泛域名功能
4. ✅ 探索 Web 管理界面
5. ✅ 阅读高级功能文档

**享受使用 DNS Core Server！** 🚀
