namespace DnsCore.Models;

/// <summary>
/// EDNS0 信息（来自请求的 OPT 伪记录，RFC 6891）
/// </summary>
public sealed record EdnsInfo
{
    /// <summary>客户端声明的 UDP 接收缓冲大小</summary>
    public required int UdpPayloadSize { get; init; }

    /// <summary>客户端是否置位 DO（DNSSEC OK）</summary>
    public bool DnssecOk { get; init; }
}

/// <summary>
/// 解析后的 DNS 查询
/// </summary>
public sealed record DnsQuery
{
    public required DnsHeader Header { get; init; }
    public required List<DnsQuestion> Questions { get; init; }

    /// <summary>请求携带的 EDNS0 信息；无 OPT 记录时为 null</summary>
    public EdnsInfo? Edns { get; init; }

    /// <summary>
    /// 本次应答允许的 UDP 最大字节数：
    /// 有 EDNS0 时取客户端声明值（上限 4096），否则为 512。
    /// </summary>
    public int MaxUdpResponseSize => Edns is null
        ? DnsLimits.MaxUdpMessageSize
        : Math.Clamp(Edns.UdpPayloadSize, DnsLimits.MaxUdpMessageSize, DnsLimits.MaxEdnsPayloadSize);
}
