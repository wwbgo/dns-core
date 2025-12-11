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
}
