namespace DnsCore.Configuration;

/// <summary>
/// 可在运行时通过管理 API 修改的上游解析设置。
/// 与 appsettings.json 分开持久化：配置文件是部署期的初始值，
/// 前端的改动写入独立文件，避免运行时回写配置文件（会丢注释、且与容器只读挂载冲突）。
/// </summary>
public sealed class UpstreamSettings
{
    /// <summary>自定义记录未命中时是否转发上游</summary>
    public bool EnableUpstreamDnsQuery { get; set; } = true;

    /// <summary>
    /// 上游 DNS 服务器列表。为空时自动使用本机系统 DNS，
    /// 系统 DNS 也不可用时兜底使用公共 DNS。
    /// </summary>
    public List<string> UpstreamDnsServers { get; set; } = [];

    /// <summary>单次上游查询超时（毫秒）</summary>
    public int TimeoutMilliseconds { get; set; } = 3000;

    /// <summary>
    /// false = 按列表顺序逐个尝试（列表次序即优先级）；
    /// true = 并行竞速，取最先返回的应答。
    /// </summary>
    public bool RaceUpstreams { get; set; }

    /// <summary>从当前生效的选项快照生成</summary>
    public static UpstreamSettings FromOptions(DnsServerOptions options) => new()
    {
        EnableUpstreamDnsQuery = options.EnableUpstreamDnsQuery,
        UpstreamDnsServers = [.. options.UpstreamDnsServers],
        TimeoutMilliseconds = options.Upstream.TimeoutMilliseconds,
        RaceUpstreams = options.Upstream.RaceUpstreams
    };
}

/// <summary>上游设置的当前状态，含实际生效的服务器列表</summary>
public sealed record UpstreamStatus
{
    public required bool EnableUpstreamDnsQuery { get; init; }
    public required List<string> UpstreamDnsServers { get; init; }
    public required int TimeoutMilliseconds { get; init; }
    public required bool RaceUpstreams { get; init; }

    /// <summary>
    /// 实际生效的上游地址。当 UpstreamDnsServers 为空时，
    /// 这里会显示自动探测到的系统 DNS，便于前端说明"当前正在用哪些上游"。
    /// </summary>
    public required List<string> EffectiveServers { get; init; }

    /// <summary>EffectiveServers 是否来自系统自动探测</summary>
    public required bool UsingSystemDns { get; init; }
}
