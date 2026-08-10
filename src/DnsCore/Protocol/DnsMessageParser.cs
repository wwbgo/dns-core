using DnsCore.Models;

namespace DnsCore.Protocol;

/// <summary>
/// DNS 消息解析与构建。
/// 所有解析路径都做严格边界检查，畸形报文抛 InvalidDataException。
/// </summary>
public static class DnsMessageParser
{
    /// <summary>
    /// 解析 DNS 查询（兼容旧签名）
    /// </summary>
    public static (DnsHeader header, List<DnsQuestion> questions) ParseQuery(byte[] data)
        => ParseQuery(data.AsSpan());

    /// <summary>
    /// 解析 DNS 查询（Span 版本，兼容旧签名）
    /// </summary>
    public static (DnsHeader header, List<DnsQuestion> questions) ParseQuery(ReadOnlySpan<byte> data)
    {
        var query = Parse(data);
        return (query.Header, query.Questions);
    }

    /// <summary>
    /// 完整解析 DNS 查询，包含 EDNS0 OPT 记录信息
    /// </summary>
    public static DnsQuery Parse(ReadOnlySpan<byte> data)
    {
        var header = DnsHeader.FromBytes(data);

        // QDCOUNT 完全由客户端控制，原实现直接循环 65535 次导致越界读取
        if (header.QuestionCount > DnsLimits.MaxQuestionCount)
            throw new InvalidDataException($"DNS question 数量异常: {header.QuestionCount}");

        var reader = new DnsReader(data) { Position = DnsHeader.Size };
        List<DnsQuestion> questions = new(header.QuestionCount);

        for (var i = 0; i < header.QuestionCount; i++)
        {
            var name = reader.ReadDomainName();
            var type = (DnsRecordType)reader.ReadUInt16();
            var classValue = reader.ReadUInt16();

            questions.Add(new DnsQuestion { Name = name, Type = type, Class = classValue });
        }

        var edns = TryReadEdns(ref reader, header);

        return new DnsQuery { Header = header, Questions = questions, Edns = edns };
    }

    /// <summary>
    /// 在 additional 区寻找 OPT 伪记录。解析失败不影响主流程，只是退化为无 EDNS。
    /// </summary>
    private static EdnsInfo? TryReadEdns(ref DnsReader reader, DnsHeader header)
    {
        if (header.AdditionalCount == 0)
            return null;

        try
        {
            // 跳过 answer 与 authority 区
            for (var i = 0; i < header.AnswerCount + header.AuthorityCount; i++)
                SkipResourceRecord(ref reader);

            for (var i = 0; i < header.AdditionalCount; i++)
            {
                reader.SkipDomainName();
                var type = (DnsRecordType)reader.ReadUInt16();
                var payloadSize = reader.ReadUInt16();   // OPT 中 CLASS 字段复用为 UDP payload size
                var ttlField = reader.ReadUInt32();      // OPT 中 TTL 字段为 extended-rcode + flags
                var rdLength = reader.ReadUInt16();
                reader.Skip(rdLength);

                if (type == DnsRecordType.OPT)
                {
                    return new EdnsInfo
                    {
                        UdpPayloadSize = payloadSize,
                        DnssecOk = (ttlField & 0x8000) != 0
                    };
                }
            }
        }
        catch (InvalidDataException)
        {
            // additional 区畸形：忽略 EDNS，按传统 512 字节处理
        }

        return null;
    }

    private static void SkipResourceRecord(ref DnsReader reader)
    {
        reader.SkipDomainName();
        reader.Skip(2 + 2 + 4);              // TYPE + CLASS + TTL
        var rdLength = reader.ReadUInt16();
        reader.Skip(rdLength);
    }

    /// <summary>
    /// 构建 DNS 响应（兼容旧签名：权威应答、不做 UDP 截断）
    /// </summary>
    public static byte[] BuildResponse(DnsHeader header, List<DnsQuestion> questions, List<DnsRecord> answers)
        => BuildResponse(new DnsResponseBuildRequest
        {
            Header = header,
            Questions = questions,
            Answers = answers,
            IsAuthoritative = true,
            MaxSize = DnsLimits.MaxMessageSize
        });

    /// <summary>
    /// 构建 DNS 响应。
    /// </summary>
    public static byte[] BuildResponse(DnsResponseBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Header;
        var answers = request.Answers ?? [];

        header.SetAsResponse(request.IsAuthoritative);
        header.SetRecursionAvailable();
        header.SetResponseCode(request.ResponseCode);

        // 关键：清零计数后再按实际写入量回填。
        // 沿用请求头的 ARCOUNT（EDNS0 OPT 会置 1）会让应答自称带附加记录而实际没有。
        header.ClearCounts();
        header.QuestionCount = (ushort)(request.Questions?.Count ?? 0);

        var writer = new DnsWriter(512);
        writer.WriteHeader(header);

        foreach (var question in request.Questions ?? [])
        {
            writer.WriteDomainName(question.Name);
            writer.WriteUInt16((ushort)question.Type);
            writer.WriteUInt16(question.Class);
        }

        var written = 0;
        var truncated = false;

        foreach (var answer in answers)
        {
            var checkpoint = writer.Position;

            try
            {
                WriteResourceRecord(writer, answer);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                // 单条记录编码失败不应毁掉整个应答：回滚该条继续
                writer.Rewind(checkpoint);
                continue;
            }

            // 超出本次允许的报文上限：回滚并置 TC 位，客户端据此改用 TCP
            if (writer.Position > request.MaxSize)
            {
                writer.Rewind(checkpoint);
                truncated = true;
                break;
            }

            written++;
        }

        // 若请求带 EDNS0，应答也需带 OPT 记录
        if (request.IncludeEdnsOpt)
        {
            var checkpoint = writer.Position;
            WriteOptRecord(writer, request.EdnsPayloadSize);

            if (writer.Position > request.MaxSize)
                writer.Rewind(checkpoint);
            else
                header.AdditionalCount = 1;
        }

        header.AnswerCount = (ushort)written;
        if (truncated)
            header.SetTruncated();

        // 回填头部（计数与标志位在写入答案后才最终确定）
        writer.PatchHeader(header);

        return writer.ToArray();
    }

    private static void WriteResourceRecord(DnsWriter writer, DnsRecord record)
    {
        writer.WriteDomainName(record.Domain);
        writer.WriteUInt16((ushort)record.Type);
        writer.WriteUInt16(1); // CLASS = IN
        writer.WriteUInt32((uint)Math.Max(0, record.TTL));

        // 先占位 RDLENGTH，写完 RDATA 再回填实际长度
        var lengthOffset = writer.Position;
        writer.WriteUInt16(0);

        var rdataStart = writer.Position;
        DnsRdataWriter.Write(writer, record.Type, record.Value);
        var rdataLength = writer.Position - rdataStart;

        if (rdataLength > ushort.MaxValue)
            throw new InvalidOperationException($"RDATA 超长: {rdataLength}");

        writer.PatchUInt16(lengthOffset, (ushort)rdataLength);
    }

    /// <summary>写入 EDNS0 OPT 伪记录（RFC 6891）</summary>
    private static void WriteOptRecord(DnsWriter writer, int payloadSize)
    {
        writer.WriteByte(0);                                    // 根域名
        writer.WriteUInt16((ushort)DnsRecordType.OPT);
        writer.WriteUInt16((ushort)Math.Clamp(
            payloadSize, DnsLimits.MaxUdpMessageSize, DnsLimits.MaxEdnsPayloadSize)); // UDP payload size
        writer.WriteUInt32(0);                                  // extended RCODE + flags
        writer.WriteUInt16(0);                                  // RDLENGTH
    }
}

/// <summary>
/// 构建响应所需的参数
/// </summary>
public sealed class DnsResponseBuildRequest
{
    public required DnsHeader Header { get; init; }
    public required List<DnsQuestion> Questions { get; init; }
    public List<DnsRecord>? Answers { get; init; }

    /// <summary>是否权威应答：仅本地自定义记录为 true</summary>
    public bool IsAuthoritative { get; init; }

    public DnsResponseCode ResponseCode { get; init; } = DnsResponseCode.NoError;

    /// <summary>报文字节上限；UDP 下为客户端可接收的大小，超出则置 TC 位</summary>
    public int MaxSize { get; init; } = DnsLimits.MaxMessageSize;

    /// <summary>请求带 EDNS0 时，应答需回带 OPT 记录</summary>
    public bool IncludeEdnsOpt { get; init; }

    public int EdnsPayloadSize { get; init; } = DnsLimits.MaxEdnsPayloadSize;
}
