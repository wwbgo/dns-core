namespace DnsCore.Models;

/// <summary>
/// DNS 响应码（RFC 1035）
/// </summary>
public enum DnsResponseCode : byte
{
    NoError = 0,
    FormErr = 1,
    ServFail = 2,
    NxDomain = 3,
    NotImp = 4,
    Refused = 5
}

/// <summary>
/// DNS 消息头（12 字节）
/// </summary>
public sealed class DnsHeader
{
    public const int Size = 12;

    public ushort TransactionId { get; set; }
    public ushort Flags { get; set; }
    public ushort QuestionCount { get; set; }
    public ushort AnswerCount { get; set; }
    public ushort AuthorityCount { get; set; }
    public ushort AdditionalCount { get; set; }

    public bool IsQuery => (Flags & 0x8000) == 0;
    public bool IsResponse => (Flags & 0x8000) != 0;

    /// <summary>操作码（0 = 标准查询）</summary>
    public int Opcode => (Flags >> 11) & 0x0F;

    /// <summary>客户端是否请求递归（RD 位）</summary>
    public bool RecursionDesired => (Flags & 0x0100) != 0;

    /// <summary>是否被截断（TC 位）</summary>
    public bool IsTruncated => (Flags & 0x0200) != 0;

    /// <summary>是否权威应答（AA 位）</summary>
    public bool IsAuthoritative => (Flags & 0x0400) != 0;

    /// <summary>响应码（Flags 低 4 位）</summary>
    public DnsResponseCode ResponseCode => (DnsResponseCode)(Flags & 0x000F);

    /// <summary>置为响应（权威应答，保持向后兼容）</summary>
    public void SetAsResponse() => SetAsResponse(true);

    /// <summary>
    /// 置为响应。isAuthoritative 仅在应答本地自定义记录时为 true；
    /// 转发上游的应答不得声称权威。
    /// </summary>
    public void SetAsResponse(bool isAuthoritative)
    {
        Flags |= 0x8000; // QR = 1

        if (isAuthoritative)
            Flags |= 0x0400;                        // AA = 1
        else
            Flags = (ushort)(Flags & ~0x0400);      // AA = 0
    }

    public void SetRecursionAvailable() => Flags |= 0x0080; // RA = 1

    public void SetTruncated() => Flags |= 0x0200; // TC = 1

    /// <summary>
    /// 设置响应码。必须先清零低 4 位：直接 |= 会与原有低位累加成错误的 RCODE。
    /// </summary>
    public void SetResponseCode(DnsResponseCode code)
        => Flags = (ushort)((Flags & 0xFFF0) | ((ushort)code & 0x000F));

    /// <summary>
    /// 清零各计数。构建响应前必须调用：请求头中的 AR/NS 计数
    /// （例如 EDNS0 OPT 记录带来的 ARCOUNT=1）若被沿用，
    /// 客户端会认为存在实际未写入的记录，从而判定报文畸形。
    /// </summary>
    public void ClearCounts()
    {
        AnswerCount = 0;
        AuthorityCount = 0;
        AdditionalCount = 0;
    }

    public byte[] ToBytes()
    {
        byte[] bytes = new byte[Size];
        WriteTo(bytes);
        return bytes;
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"DNS 头需要至少 {Size} 字节", nameof(destination));

        WriteUInt16(destination, 0, TransactionId);
        WriteUInt16(destination, 2, Flags);
        WriteUInt16(destination, 4, QuestionCount);
        WriteUInt16(destination, 6, AnswerCount);
        WriteUInt16(destination, 8, AuthorityCount);
        WriteUInt16(destination, 10, AdditionalCount);
    }

    public static DnsHeader FromBytes(byte[] data, int offset = 0)
        => FromBytes(data.AsSpan(), offset);

    public static DnsHeader FromBytes(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (offset < 0 || data.Length - offset < Size)
            throw new InvalidDataException(
                $"DNS 报文过短：需要 {Size} 字节头部，实际可用 {Math.Max(0, data.Length - offset)} 字节");

        return new DnsHeader
        {
            TransactionId = ReadUInt16(data, offset),
            Flags = ReadUInt16(data, offset + 2),
            QuestionCount = ReadUInt16(data, offset + 4),
            AnswerCount = ReadUInt16(data, offset + 6),
            AuthorityCount = ReadUInt16(data, offset + 8),
            AdditionalCount = ReadUInt16(data, offset + 10)
        };
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteUInt16(Span<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)(value & 0xFF);
    }
}
