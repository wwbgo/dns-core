namespace DnsCore.Models;

/// <summary>
/// DNS 记录
/// </summary>
public sealed class DnsRecord
{
    public required string Domain { get; init; }
    public required DnsRecordType Type { get; init; }
    public required string Value { get; init; }
    public int TTL { get; init; } = 3600;

    public override string ToString() => $"{Domain} {Type} {Value} (TTL: {TTL})";
}
