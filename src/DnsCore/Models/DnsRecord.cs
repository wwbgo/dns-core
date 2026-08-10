namespace DnsCore.Models;

/// <summary>
/// DNS 记录。
/// 使用 record 类型以支持 with 表达式：应答泛域名匹配时，
/// 必须把 owner name 改写为客户端实际查询的域名，否则客户端会丢弃应答。
/// </summary>
public sealed record DnsRecord
{
    public required string Domain { get; init; }
    public required DnsRecordType Type { get; init; }
    public required string Value { get; init; }
    public int TTL { get; init; } = 3600;

    /// <summary>
    /// 是否为泛域名记录（owner name 以 "*." 开头）
    /// </summary>
    public bool IsWildcard => Domain.StartsWith("*.", StringComparison.Ordinal);

    public override string ToString() => $"{Domain} {Type} {Value} (TTL: {TTL})";
}
