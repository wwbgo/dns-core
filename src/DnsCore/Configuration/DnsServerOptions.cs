using DnsCore.Models;

namespace DnsCore.Configuration;

/// <summary>
/// DNS 服务器配置选项
/// </summary>
public class DnsServerOptions
{
    public int Port { get; set; } = 53;
    public List<string> UpstreamDnsServers { get; set; } = new();
    public List<DnsRecord> CustomRecords { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();

    /// <summary>
    /// 是否启用上游 DNS 查询。当自定义记录不存在时：
    /// - true: 查询上游DNS
    /// - false: 返回 SERVFAIL，让客户端尝试系统配置的下一个 DNS 服务器
    /// </summary>
    public bool EnableUpstreamDnsQuery { get; set; } = true;
}
