namespace DnsCore.Models;

/// <summary>
/// DNS 查询问题
/// </summary>
public sealed class DnsQuestion
{
    public required string Name { get; init; }
    public required DnsRecordType Type { get; init; }
    public ushort Class { get; init; } = 1; // IN (Internet)

    public override string ToString() => $"{Name} {Type} {Class}";
}
