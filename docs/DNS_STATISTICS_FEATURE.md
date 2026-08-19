# DNS 查询统计

## 功能

Web 控制台展示两组运行指标：

- **查询量** —— 最近 1 秒 / 1 分钟 / 1 小时 / 24 小时的查询数，累计查询数，运行时长
- **响应延迟** —— 平均 / 最小 / 最大 / P50 / P95 / P99

顶部卡片显示当前 QPS（含 60 秒迷你趋势图）与平均延迟，完整指标在可折叠的「详细监控数据」面板内。前端每 30 秒自动刷新。

## 一个容易误解的点：一次 `nslookup` 会让计数增加 4–5

统计的是**协议层 DNS 查询包**，不是"用户操作次数"。一次 `nslookup example.com` 实际发出：

```
1.0.0.127.in-addr.arpa  PTR    ← 反查服务器名，用于显示 "Server: ..."
example.com             A
example.com             AAAA
example.com             A      ← 失败时重试
example.com             AAAA   ← 失败时重试
```

`nslookup` 并发查 A + AAAA，启动时先做一次 PTR 反查；若服务返回 SERVFAIL 还会重试。用单个原始 UDP 包验证过，计数只加 1。

`dig` 只发 1 个包，`ping` 通常 1–2 个（取决于系统 IPv6 配置）。

## 实现

### DnsQueryStatistics

`src/DnsCore/Services/DnsQueryStatistics.cs`

**核心设计：槽位索引由绝对时间戳决定，没有"滚动"这个概念。**

```csharp
private const int SecondSlots = 3600;   // 秒级槽覆盖 1 小时
private const int MinuteSlots = 1440;   // 分钟级槽覆盖 1 天

private readonly int[]  _secondCounts = new int[SecondSlots];
private readonly long[] _secondStamps = new long[SecondSlots];   // 每槽自带它代表的时刻
```

写入时用绝对 Unix 时刻取模定位槽；槽内时刻与目标不符即先归零再计数：

```csharp
var i = SlotOf(tick, slots);
if (stamps[i] != tick) { stamps[i] = tick; counts[i] = 0; }
counts[i]++;
```

读取窗口时逐 tick 校验槽内时刻，不符的视为 0：

```csharp
for (var t = endTick - window + 1; t <= endTick; t++)
{
    var i = SlotOf(t, slots);
    if (stamps[i] == t) total += counts[i];
}
```

秒级与分钟级两层**各自独立写入**，不做层间聚合。

**为什么不用分层滚动**

早先的实现维护"当前指针 + 上次滚动时间"，向上逐层聚合，有三个叠加缺陷：

1. 上层桶只在跨界时填充 —— 当前小时/当前天的数据永远不被计入，导致「最近一天」在服务启动后的第一个小时内恒显示 0
2. 层间聚合取"最近 N 个槽之和"而非"刚过去的那一段" —— 滚动间隔不精确时重复计数
3. 滚动只由请求触发 —— 空闲超过秒级数组长度后，一次请求会走 `Array.Clear` 把仍在窗口内的历史整片清空

绝对时间戳方案从结构上消除了这三种可能：无需滚动，空闲不丢数据，每个窗口都精确。

**成本**

- 内存固定 ~60KB（3600 + 1440 个槽，各含 int + long），无 GC 压力
- 最贵的查询是「最近一天」，遍历 1440 个槽，微秒级
- 写入路径在 `lock` 内只做两次数组索引 + 自增

**边界处理**

- 空槽用 `-1` 标记，避免与"Unix 纪元第 0 秒"混淆
- `SlotOf` 处理负 tick（注入时钟可能早于 1970 年）
- `FloorDiv` 向下取整，C# 的 `/` 对负数向零取整会让纪元前的时刻归错桶

### DnsLatencyStatistics

`src/DnsCore/Services/DnsLatencyStatistics.cs`

保留最近 1,000 条延迟样本用于计算百分位数，累计的平均/最小/最大不受样本上限影响。

### 记录点

`DnsServer.ProcessDnsQueryAsync` 在报文**解析成功后**立即计入：

```csharp
statistics.RecordQuery();
```

FormErr（无 question 区）和 NotImp（不支持的 Opcode）也计入 —— 这些请求服务同样处理并应答了，属于真实到达的流量。只有解析失败的畸形包不计（在此之前已 return）。

延迟在 `finally` 块记录，覆盖所有返回路径。

## API

两个端点，均受 `/api/*` 的 API Key 鉴权与来源网段限制保护。

### `GET /api/qps`

```json
{
  "totalQueries": 1523,
  "perSecond": 3,
  "perMinute": 142,
  "perHour": 4820,
  "perDay": 51203,
  "recentSeconds": [0, 2, 5, 3, "…共 60 项，按时间升序，末位为当前秒"],
  "uptimeSeconds": 7284.5
}
```

各窗口聚合值由后端直接给出，前端不再自行求和 —— 早先前端把按小时分桶的数组求和当「最近一天」，正是那个 bug 的来源。

### `GET /api/qps/latency`

```json
{
  "totalRequests": 1523,
  "averageMs": 19.06,
  "minMs": 0.02,
  "maxMs": 1098.42,
  "p50Ms": 0.04,
  "p95Ms": 70.44,
  "p99Ms": 1057.39
}
```

P50 通常极低（缓存命中），P95/P99 反映上游查询与超时。

## 测试

`tests/DnsCore.Tests/Services/DnsQueryStatisticsTests.cs` —— 14 个用例，通过注入 `Func<DateTimeOffset>` 精确驱动时间窗口。

针对上述三个缺陷的回归用例：

| 用例 | 覆盖的缺陷 |
|---|---|
| `PerDay_ShouldCountCurrentHour_NotOnlyCompletedHours` | 当前小时不被计入 |
| `Counts_ShouldNotBeDoubleCounted_AcrossMinuteBoundary` | 层间聚合重复计数 |
| `IdleGap_ShouldNotWipeHistory` | 空闲后清空历史 |
| `SlotReuse_ShouldNotLeakStaleCounts` | 槽位复用读到陈旧值 |

其余覆盖：各窗口即时反映、窗口过期边界、`recentSeconds` 时序与求和一致性、空统计、运行时长、并发写入不丢计数。

## 手工验证

```bash
# 发单个原始 DNS 包，确认计数只加 1
python -c "
import socket
s=socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(2)
pkt=bytes([0xAB,0xCD,0x01,0x00,0,1,0,0,0,0,0,0])+b'\x08dns-core\x05local\x00'+bytes([0,1,0,1])
s.sendto(pkt,('127.0.0.1',53)); s.recvfrom(512)
"

curl -s http://localhost:5000/api/qps
```

端口以 `ASPNETCORE_URLS` 为准（默认 5000）；用 `dotnet run` 启动时会走
`Properties/launchSettings.json` 的开发配置，当前是 60046。

排查计数问题时可临时开启 `DnsServer:LogEveryQuery`，日志会打印每个查询的域名与类型 —— 注意这会记录所有客户端的查询历史，排查完应关闭。

## 相关文件

| 文件 | 作用 |
|---|---|
| `src/DnsCore/Services/DnsQueryStatistics.cs` | 查询量统计 |
| `src/DnsCore/Services/DnsLatencyStatistics.cs` | 延迟统计 |
| `src/DnsCore/Services/DnsServer.cs` | 记录点 |
| `src/DnsCore/Program.cs` | DI 注册与端点 |
| `src/DnsCore/wwwroot/index.html` | 卡片与监控面板结构 |
| `src/DnsCore/wwwroot/app.js` | `loadQueryStats` / `loadLatencyStats` / sparkline |
| `tests/DnsCore.Tests/Services/DnsQueryStatisticsTests.cs` | 时间窗口回归测试 |
