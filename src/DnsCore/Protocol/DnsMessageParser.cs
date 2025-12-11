using DnsCore.Models;
using System.Buffers;

namespace DnsCore.Protocol;

/// <summary>
/// DNS 消息解析器（性能优化版）
/// </summary>
public sealed class DnsMessageParser
{
    /// <summary>
    /// 解析 DNS 查询消息（性能优化：使用 ReadOnlySpan）
    /// </summary>
    public static (DnsHeader header, List<DnsQuestion> questions) ParseQuery(byte[] data)
        => ParseQuery(data.AsSpan());

    /// <summary>
    /// 解析 DNS 查询消息（Span 版本）
    /// </summary>
    public static (DnsHeader header, List<DnsQuestion> questions) ParseQuery(ReadOnlySpan<byte> data)
    {
        var header = DnsHeader.FromBytes(data, 0);
        List<DnsQuestion> questions = [];

        var offset = 12; // DNS header 是 12 字节

        for (var i = 0; i < header.QuestionCount; i++)
        {
            (var name, offset) = ReadDomainName(data, offset);

            var type = (DnsRecordType)ReadUInt16(data, offset);
            offset += 2;

            var classValue = ReadUInt16(data, offset);
            offset += 2;

            questions.Add(new DnsQuestion
            {
                Name = name,
                Type = type,
                Class = classValue
            });
        }

        return (header, questions);
    }

    /// <summary>
    /// 构建 DNS 响应消息
    /// </summary>
    public static byte[] BuildResponse(DnsHeader header, List<DnsQuestion> questions, List<DnsRecord> answers)
    {
        using var ms = new MemoryStream();

        // 写入响应头
        header.SetAsResponse();
        header.SetRecursionAvailable();
        header.AnswerCount = (ushort)answers.Count;
        ms.Write(header.ToBytes());

        // 写入问题部分
        foreach (var question in questions)
        {
            WriteDomainName(ms, question.Name);
            WriteUInt16(ms, (ushort)question.Type);
            WriteUInt16(ms, question.Class);
        }

        // 写入答案部分
        foreach (var answer in answers)
        {
            WriteDomainName(ms, answer.Domain);
            WriteUInt16(ms, (ushort)answer.Type);
            WriteUInt16(ms, 1); // Class: IN
            WriteUInt32(ms, (uint)answer.TTL);

            // 根据记录类型写入数据
            byte[] rdata = answer.Type switch
            {
                DnsRecordType.A => ParseIPv4(answer.Value),
                DnsRecordType.AAAA => ParseIPv6(answer.Value),
                DnsRecordType.CNAME or DnsRecordType.NS or DnsRecordType.PTR => EncodeDomainName(answer.Value),
                DnsRecordType.TXT => EncodeTxtRecord(answer.Value),
                _ => Array.Empty<byte>()
            };

            WriteUInt16(ms, (ushort)rdata.Length);
            ms.Write(rdata);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// 读取域名（支持压缩）- 性能优化：使用 Span
    /// </summary>
    private static (string name, int offset) ReadDomainName(ReadOnlySpan<byte> data, int offset)
    {
        // 使用 ArrayPool 来复用 label 缓冲区
        char[]? labelBuffer = null;
        try
        {
            List<string> labels = [];
            var jumped = false;
            var jumpOffset = offset;
            const int maxJumps = 5;
            var jumps = 0;

            while (true)
            {
                var length = data[offset];

                // 压缩指针
                if ((length & 0xC0) == 0xC0)
                {
                    if (jumps++ > maxJumps)
                        throw new InvalidDataException("DNS 消息压缩过多");

                    if (!jumped)
                        jumpOffset = offset + 2;

                    var pointer = ((length & 0x3F) << 8) | data[offset + 1];
                    offset = pointer;
                    jumped = true;
                    continue;
                }

                // 域名结束
                if (length == 0)
                {
                    offset++;
                    break;
                }

                // 读取标签 - 使用 ArrayPool 优化
                offset++;
                if (labelBuffer == null || labelBuffer.Length < length)
                {
                    if (labelBuffer != null)
                        ArrayPool<char>.Shared.Return(labelBuffer);
                    labelBuffer = ArrayPool<char>.Shared.Rent(length);
                }

                // 使用 Span 进行 ASCII 解码
                var labelSpan = data.Slice(offset, length);
                for (int i = 0; i < length; i++)
                {
                    labelBuffer[i] = (char)labelSpan[i];
                }

                labels.Add(new string(labelBuffer, 0, length));
                offset += length;
            }

            return (string.Join('.', labels), jumped ? jumpOffset : offset);
        }
        finally
        {
            if (labelBuffer != null)
                ArrayPool<char>.Shared.Return(labelBuffer);
        }
    }

    /// <summary>
    /// 写入域名
    /// </summary>
    private static void WriteDomainName(Stream stream, string domain)
    {
        if (string.IsNullOrEmpty(domain))
        {
            stream.WriteByte(0);
            return;
        }

        var labels = domain.Split('.');
        foreach (var label in labels)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static byte[] EncodeDomainName(string domain)
    {
        using var ms = new MemoryStream();
        WriteDomainName(ms, domain);
        return ms.ToArray();
    }

    private static byte[] EncodeTxtRecord(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var result = new byte[bytes.Length + 1];
        result[0] = (byte)bytes.Length;
        Array.Copy(bytes, 0, result, 1, bytes.Length);
        return result;
    }

    private static byte[] ParseIPv4(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4)
            throw new ArgumentException($"无效的 IPv4 地址: {ip}");

        return [.. parts.Select(byte.Parse)];
    }

    private static byte[] ParseIPv6(string ip) =>
        System.Net.IPAddress.Parse(ip).GetAddressBytes();

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }
}
