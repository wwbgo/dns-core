using DnsCore.Models;
using System.Text;

namespace DnsCore.Protocol;

/// <summary>
/// 带严格边界检查的 DNS 线格式读取器。
/// 所有越界、非法 label、指针环、超长域名都抛 InvalidDataException，
/// 而非 IndexOutOfRangeException —— 畸形报文属于预期输入，不是程序错误。
/// </summary>
public ref struct DnsReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; set; }

    public int Length => _data.Length;

    public int Remaining => _data.Length - Position;

    private void EnsureAvailable(int count, int at)
    {
        if (at < 0 || count < 0 || at > _data.Length - count)
            throw new InvalidDataException(
                $"DNS 报文越界：偏移 {at} 处需要 {count} 字节，报文总长 {_data.Length}");
    }

    public byte ReadByte()
    {
        EnsureAvailable(1, Position);
        return _data[Position++];
    }

    public ushort ReadUInt16()
    {
        EnsureAvailable(2, Position);
        var value = (ushort)((_data[Position] << 8) | _data[Position + 1]);
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(4, Position);
        var value = ((uint)_data[Position] << 24) | ((uint)_data[Position + 1] << 16)
                  | ((uint)_data[Position + 2] << 8) | _data[Position + 3];
        Position += 4;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        EnsureAvailable(count, Position);
        var slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }

    public void Skip(int count)
    {
        EnsureAvailable(count, Position);
        Position += count;
    }

    /// <summary>
    /// 读取域名，支持压缩指针。
    /// 指针只允许向前跳（指向更小的偏移），从根本上排除指针环。
    /// </summary>
    public string ReadDomainName()
    {
        var builder = new StringBuilder(64);
        var totalLength = 0;
        var jumps = 0;
        var jumped = false;
        var readPos = Position;
        var continuePos = Position;

        while (true)
        {
            EnsureAvailable(1, readPos);
            var lengthByte = _data[readPos];

            // 压缩指针（高两位 11）
            if ((lengthByte & 0xC0) == 0xC0)
            {
                EnsureAvailable(2, readPos);
                var pointer = ((lengthByte & 0x3F) << 8) | _data[readPos + 1];

                if (!jumped)
                {
                    continuePos = readPos + 2;
                    jumped = true;
                }

                if (++jumps > DnsLimits.MaxCompressionJumps)
                    throw new InvalidDataException("DNS 压缩指针跳转过多");

                // 指针必须向前跳，否则构成环
                if (pointer >= readPos)
                    throw new InvalidDataException($"DNS 压缩指针非法：{pointer} 未指向当前位置 {readPos} 之前");

                if (pointer < DnsHeader.Size)
                    throw new InvalidDataException($"DNS 压缩指针非法：{pointer} 指向报文头部");

                readPos = pointer;
                continue;
            }

            // label 长度的高两位必须为 00，0x40/0x80 是保留值
            if ((lengthByte & 0xC0) != 0)
                throw new InvalidDataException($"DNS label 长度字节使用了保留位：0x{lengthByte:X2}");

            // 域名结束
            if (lengthByte == 0)
            {
                readPos++;
                break;
            }

            if (lengthByte > DnsLimits.MaxLabelLength)
                throw new InvalidDataException($"DNS label 超长：{lengthByte} > {DnsLimits.MaxLabelLength}");

            readPos++;
            EnsureAvailable(lengthByte, readPos);

            // +1 计入 label 长度字节本身，与线格式长度一致
            totalLength += lengthByte + 1;
            if (totalLength > DnsLimits.MaxDomainNameLength)
                throw new InvalidDataException($"DNS 域名超长：> {DnsLimits.MaxDomainNameLength} 字节");

            if (builder.Length > 0)
                builder.Append('.');

            var labelSpan = _data.Slice(readPos, lengthByte);
            for (var i = 0; i < labelSpan.Length; i++)
                builder.Append((char)labelSpan[i]);

            readPos += lengthByte;
        }

        Position = jumped ? continuePos : readPos;
        return builder.ToString();
    }

    /// <summary>跳过域名而不构造字符串</summary>
    public void SkipDomainName() => _ = ReadDomainName();
}
