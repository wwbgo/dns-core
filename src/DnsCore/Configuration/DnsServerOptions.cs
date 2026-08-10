using DnsCore.Models;

namespace DnsCore.Configuration;

/// <summary>
/// DNS 服务器配置选项
/// </summary>
public class DnsServerOptions
{
    public int Port { get; set; } = 53;

    /// <summary>
    /// 监听地址。默认同时监听 IPv4 与 IPv6：
    /// 原实现只绑 IPv4，IPv6 客户端无法连接。
    /// </summary>
    public string ListenAddress { get; set; } = "::";

    /// <summary>
    /// 服务器主机名，用于自动应答反向 DNS 查询（PTR 记录）。
    /// 当客户端查询服务器 IP 的 PTR 记录时（如 nslookup 启动时），自动返回此主机名。
    /// 例如：设为 "dns-server.local" 后，nslookup 会显示 "服务器: dns-server.local" 而不是 "UnKnown"。
    /// </summary>
    public string? Hostname { get; set; }

    public List<string> UpstreamDnsServers { get; set; } = new();
    public List<DnsRecord> CustomRecords { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();
    public CacheOptions Cache { get; set; } = new();
    public DnsSecurityOptions Security { get; set; } = new();
    public UpstreamOptions Upstream { get; set; } = new();

    /// <summary>
    /// 是否启用上游 DNS 查询。当自定义记录不存在时：
    /// - true: 查询上游 DNS
    /// - false: 返回 SERVFAIL，让客户端尝试系统配置的下一个 DNS 服务器
    /// </summary>
    public bool EnableUpstreamDnsQuery { get; set; } = true;

    /// <summary>
    /// 是否为每个查询打印 Information 级日志。
    /// 默认关闭：逐查询日志既是主要性能开销，也完整记录了客户端的查询历史（隐私）。
    /// </summary>
    public bool LogEveryQuery { get; set; }
}

/// <summary>
/// 上游解析配置
/// </summary>
public sealed class UpstreamOptions
{
    /// <summary>单次上游查询超时（毫秒）</summary>
    public int TimeoutMilliseconds { get; set; } = 3000;

    /// <summary>
    /// 是否并行竞速查询所有上游（取最先返回的成功应答）。
    /// 默认 false，按列表顺序逐个尝试：顺序模式下列表次序即优先级，
    /// 适合"首选内网 DNS，失败才走公网"这类部署。
    /// 改为 true 可换取更低的尾延迟，但每次查询都会同时打所有上游。
    /// </summary>
    public bool RaceUpstreams { get; set; }

    /// <summary>上游查询的最大并发数</summary>
    public int MaxConcurrentQueries { get; set; } = 512;
}
