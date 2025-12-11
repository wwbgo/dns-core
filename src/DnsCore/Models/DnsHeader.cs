namespace DnsCore.Models;

/// <summary>
/// DNS 消息头
/// </summary>
public sealed class DnsHeader
{
    public ushort TransactionId { get; set; }
    public ushort Flags { get; set; }
    public ushort QuestionCount { get; set; }
    public ushort AnswerCount { get; set; }
    public ushort AuthorityCount { get; set; }
    public ushort AdditionalCount { get; set; }

    public bool IsQuery => (Flags & 0x8000) == 0;
    public bool IsResponse => (Flags & 0x8000) != 0;

    public void SetAsResponse()
    {
        Flags |= 0x8000; // QR bit = 1 (response)
        Flags |= 0x0400; // AA bit = 1 (authoritative answer)
    }

    public void SetRecursionAvailable() => Flags |= 0x0080; // RA bit = 1

    public byte[] ToBytes()
    {
        byte[] bytes = new byte[12];
        WriteUInt16(bytes, 0, TransactionId);
        WriteUInt16(bytes, 2, Flags);
        WriteUInt16(bytes, 4, QuestionCount);
        WriteUInt16(bytes, 6, AnswerCount);
        WriteUInt16(bytes, 8, AuthorityCount);
        WriteUInt16(bytes, 10, AdditionalCount);
        return bytes;
    }

    public static DnsHeader FromBytes(byte[] data, int offset = 0) => new()
    {
        TransactionId = ReadUInt16(data, offset),
        Flags = ReadUInt16(data, offset + 2),
        QuestionCount = ReadUInt16(data, offset + 4),
        AnswerCount = ReadUInt16(data, offset + 6),
        AuthorityCount = ReadUInt16(data, offset + 8),
        AdditionalCount = ReadUInt16(data, offset + 10)
    };

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)(value & 0xFF);
    }
}
