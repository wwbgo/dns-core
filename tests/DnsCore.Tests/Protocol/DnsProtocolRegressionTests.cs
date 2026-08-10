using DnsCore.Models;
using DnsCore.Protocol;
using FluentAssertions;

namespace DnsCore.Tests.Protocol;

/// <summary>
/// 协议层回归测试：覆盖修复前会产出畸形报文或崩溃的场景。
/// </summary>
public class DnsProtocolRegressionTests
{
    private static DnsHeader QueryHeader(ushort additionalCount = 0, ushort questionCount = 1) => new()
    {
        TransactionId = 0x1234,
        Flags = 0x0100,
        QuestionCount = questionCount,
        AdditionalCount = additionalCount
    };

    private static List<DnsQuestion> Question(string name, DnsRecordType type = DnsRecordType.A)
        => [new DnsQuestion { Name = name, Type = type, Class = 1 }];

    // ==== 计数回填 ====

    [Fact]
    public void BuildResponse_ShouldZeroAdditionalCount_WhenRequestCarriedEdnsOpt()
    {
        // 修复前：请求 ARCOUNT=1（EDNS0 OPT）会被原样抄进响应，
        // 但响应里并未写入任何附加记录，客户端据此判定报文畸形。
        var header = QueryHeader(additionalCount: 1);
        var answers = new List<DnsRecord>
        {
            new() { Domain = "api.example.com", Type = DnsRecordType.A, Value = "192.168.1.1", TTL = 60 }
        };

        var response = DnsMessageParser.BuildResponse(header, Question("api.example.com"), answers);
        var parsed = DnsHeader.FromBytes(response);

        parsed.AnswerCount.Should().Be(1);
        parsed.AdditionalCount.Should().Be(0);
        parsed.AuthorityCount.Should().Be(0);
    }

    [Fact]
    public void BuildResponse_ShouldWriteOptRecord_WhenEdnsRequested()
    {
        var header = QueryHeader(additionalCount: 1);

        var response = DnsMessageParser.BuildResponse(new DnsResponseBuildRequest
        {
            Header = header,
            Questions = Question("example.com"),
            Answers = [],
            IncludeEdnsOpt = true,
            EdnsPayloadSize = 4096
        });

        var parsed = DnsHeader.FromBytes(response);

        // 声明 1 条附加记录，且必须真的写了 OPT
        parsed.AdditionalCount.Should().Be(1);

        var reader = new DnsReader(response) { Position = DnsHeader.Size };
        reader.ReadDomainName();
        reader.ReadUInt16();
        reader.ReadUInt16();
        reader.ReadDomainName();                       // OPT 的根域名
        ((DnsRecordType)reader.ReadUInt16()).Should().Be(DnsRecordType.OPT);
    }

    // ==== 泛域名 owner name ====

    [Fact]
    public void BuildResponse_ShouldNotEmitLiteralWildcardLabel()
    {
        // 修复前：泛域名命中会把 "*.example.com" 直接编码进应答，
        // 客户端因 owner 与 question 不匹配而丢弃。
        var answers = new List<DnsRecord>
        {
            new() { Domain = "api.example.com", Type = DnsRecordType.A, Value = "192.168.1.100", TTL = 3600 }
        };

        var response = DnsMessageParser.BuildResponse(
            QueryHeader(), Question("api.example.com"), answers);

        response.Should().NotContain((byte)'*');
    }

    // ==== 畸形报文 ====

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(11)]
    public void Parse_ShouldThrowInvalidData_WhenPacketTooShort(int size)
    {
        // 修复前抛 IndexOutOfRangeException（视为程序错误），现在是可预期的协议错误
        var act = () => DnsMessageParser.Parse(new byte[size]);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_ShouldReject_WhenQuestionCountAbsurd()
    {
        // QDCOUNT 完全由攻击者控制；修复前会循环 65535 次并越界读取
        var data = new byte[12];
        data[4] = 0xFF;
        data[5] = 0xFF;

        var act = () => DnsMessageParser.Parse(data);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_ShouldReject_SelfReferencingCompressionPointer()
    {
        var data = new byte[16];
        data[5] = 1;      // QDCOUNT = 1
        data[12] = 0xC0;  // 指针指向自身
        data[13] = 0x0C;

        var act = () => DnsMessageParser.Parse(data);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_ShouldReject_LabelLengthBeyondBuffer()
    {
        var data = new byte[20];
        data[5] = 1;
        data[12] = 200; // 声明 200 字节 label，缓冲区没有那么长

        var act = () => DnsMessageParser.Parse(data);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_ShouldReject_ReservedLabelLengthBits()
    {
        var data = new byte[20];
        data[5] = 1;
        data[12] = 0x80; // 高两位为 10，属保留值

        var act = () => DnsMessageParser.Parse(data);
        act.Should().Throw<InvalidDataException>();
    }

    // ==== 域名与 label 校验 ====

    [Fact]
    public void ValidateDomainName_ShouldReject_LabelOver63Bytes()
    {
        // 修复前 (byte)length 强转：300 字符的 label 被截成 44，静默产出损坏报文
        var act = () => DnsWriter.ValidateDomainName(new string('a', 300) + ".com");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateDomainName_ShouldReject_NameOver255Bytes()
    {
        var longName = string.Join('.', Enumerable.Repeat(new string('a', 60), 5));
        var act = () => DnsWriter.ValidateDomainName(longName);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidateDomainName_ShouldAccept_NormalName()
    {
        var act = () => DnsWriter.ValidateDomainName("api.dev.example.com");
        act.Should().NotThrow();
    }

    // 严格字符集用于 API 写入路径：这些字符无法出现在合法主机名中，
    // 但此前可被存入记录并回显到管理界面，成为注入载荷的来源。
    [Theory]
    [InlineData("x\" onfocus=\"alert(1)")]
    [InlineData("evil<script>.com")]
    [InlineData("a'b.com")]
    [InlineData("has space.com")]
    public void ValidateDomainName_Strict_ShouldRejectInjectionChars(string domain)
    {
        var act = () => DnsWriter.ValidateDomainName(domain, strictCharset: true);
        act.Should().Throw<ArgumentException>();
    }

    // 严格模式不能误伤这些合法形态
    [Theory]
    [InlineData("normal.example.com")]
    [InlineData("my-host.sub.example.com")]
    [InlineData("_sip._tcp.example.com")]              // SRV 服务名带下划线
    [InlineData("90.100.168.192.in-addr.arpa")]        // PTR 反向查询名
    [InlineData("xn--fiqs8s.com")]                     // Punycode 国际域名
    public void ValidateDomainName_Strict_ShouldAcceptLegitimateNames(string domain)
    {
        var act = () => DnsWriter.ValidateDomainName(domain, strictCharset: true);
        act.Should().NotThrow();
    }

    // 应答编码路径保持宽松：上游返回的域名形态不受本服务控制，
    // 过严会导致合法应答无法编码
    [Fact]
    public void ValidateDomainName_Lenient_ShouldStayPermissiveByDefault()
    {
        var act = () => DnsWriter.ValidateDomainName("weird_but*present.example.com");
        act.Should().NotThrow();
    }

    // ==== RCODE ====

    [Fact]
    public void SetResponseCode_ShouldReplaceLowBits_NotAccumulate()
    {
        // 修复前用 |= 叠加，低位非 0 时会得到错误的 RCODE
        var header = new DnsHeader { Flags = 0x0001 };

        header.SetResponseCode(DnsResponseCode.ServFail);

        header.ResponseCode.Should().Be(DnsResponseCode.ServFail);
        (header.Flags & 0x000F).Should().Be(2);
    }

    [Fact]
    public void SetAsResponse_ShouldClearAuthoritativeBit_ForForwardedAnswers()
    {
        // 转发上游的应答不得声称权威
        var header = new DnsHeader { Flags = 0x0400 };

        header.SetAsResponse(isAuthoritative: false);

        header.IsAuthoritative.Should().BeFalse();
        header.IsResponse.Should().BeTrue();
    }

    // ==== 截断 ====

    [Fact]
    public void BuildResponse_ShouldSetTruncatedBit_WhenExceedingUdpLimit()
    {
        // 大量 TXT 记录必然超过 512 字节；修复前会直接超长发出且不置 TC 位
        var answers = Enumerable.Range(0, 60)
            .Select(i => new DnsRecord
            {
                Domain = "big.example.com",
                Type = DnsRecordType.TXT,
                Value = new string('x', 200),
                TTL = 60
            })
            .ToList();

        var response = DnsMessageParser.BuildResponse(new DnsResponseBuildRequest
        {
            Header = QueryHeader(),
            Questions = Question("big.example.com", DnsRecordType.TXT),
            Answers = answers,
            MaxSize = DnsLimits.MaxUdpMessageSize
        });

        var parsed = DnsHeader.FromBytes(response);

        response.Length.Should().BeLessThanOrEqualTo(DnsLimits.MaxUdpMessageSize);
        parsed.IsTruncated.Should().BeTrue();
        // 计数必须与实际写入的记录数一致，而不是请求的记录数
        ((int)parsed.AnswerCount).Should().BeLessThan(answers.Count);
    }

    // ==== RDATA 编码 ====

    [Theory]
    [InlineData(DnsRecordType.MX, "10 mail.example.com")]
    [InlineData(DnsRecordType.SRV, "10 60 5060 sip.example.com")]
    [InlineData(DnsRecordType.SOA, "ns.example.com admin.example.com 2024010101 7200 3600 1209600 3600")]
    [InlineData(DnsRecordType.CAA, "0 issue letsencrypt.org")]
    public void BuildResponse_ShouldEmitNonEmptyRdata_ForPreviouslyUnsupportedTypes(
        DnsRecordType type, string value)
    {
        // 修复前 MX/SOA/SRV 走 default 分支返回空数组，产出 rdlen=0 的无效记录
        var answers = new List<DnsRecord>
        {
            new() { Domain = "example.com", Type = type, Value = value, TTL = 300 }
        };

        var response = DnsMessageParser.BuildResponse(QueryHeader(), Question("example.com", type), answers);
        var parsed = DnsHeader.FromBytes(response);

        parsed.AnswerCount.Should().Be(1);

        var reader = new DnsReader(response) { Position = DnsHeader.Size };
        reader.ReadDomainName();
        reader.ReadUInt16();
        reader.ReadUInt16();
        reader.ReadDomainName();
        reader.ReadUInt16();
        reader.ReadUInt16();
        reader.ReadUInt32();

        reader.ReadUInt16().Should().BeGreaterThan(0, "RDATA 不应为空");
    }

    [Theory]
    [InlineData(DnsRecordType.A, "not-an-ip")]
    [InlineData(DnsRecordType.A, "999.1.1.1")]
    [InlineData(DnsRecordType.AAAA, "192.168.1.1")]
    [InlineData(DnsRecordType.MX, "mail.example.com")]
    [InlineData(DnsRecordType.SRV, "10 60 sip.example.com")]
    public void TryValidate_ShouldRejectInvalidValues(DnsRecordType type, string value)
    {
        DnsRdataWriter.TryValidate(type, value, out _).Should().BeFalse();
    }

    [Fact]
    public void BuildResponse_ShouldSkipUnencodableRecord_NotFailEntireResponse()
    {
        // 单条记录编码失败不应毁掉整个应答
        var answers = new List<DnsRecord>
        {
            new() { Domain = "bad.example.com", Type = DnsRecordType.A, Value = "not-an-ip", TTL = 60 },
            new() { Domain = "good.example.com", Type = DnsRecordType.A, Value = "10.0.0.1", TTL = 60 }
        };

        var response = DnsMessageParser.BuildResponse(QueryHeader(), Question("example.com"), answers);
        var parsed = DnsHeader.FromBytes(response);

        parsed.AnswerCount.Should().Be(1);
    }

    [Fact]
    public void BuildResponse_ShouldChunkLongTxtRecord()
    {
        // TXT 超过 255 字节必须分片；修复前 (byte) 强转会写出错误的长度前缀
        var answers = new List<DnsRecord>
        {
            new() { Domain = "txt.example.com", Type = DnsRecordType.TXT, Value = new string('a', 600), TTL = 60 }
        };

        var response = DnsMessageParser.BuildResponse(new DnsResponseBuildRequest
        {
            Header = QueryHeader(),
            Questions = Question("txt.example.com", DnsRecordType.TXT),
            Answers = answers,
            MaxSize = DnsLimits.MaxMessageSize
        });

        DnsHeader.FromBytes(response).AnswerCount.Should().Be(1);
    }

    // ==== 压缩 ====

    [Fact]
    public void BuildResponse_ShouldCompressRepeatedNames()
    {
        // 同一域名在 question 与多条 answer 中重复出现，应通过压缩指针复用
        var answers = Enumerable.Range(1, 4)
            .Select(i => new DnsRecord
            {
                Domain = "repeated.name.example.com",
                Type = DnsRecordType.A,
                Value = $"10.0.0.{i}",
                TTL = 60
            })
            .ToList();

        var response = DnsMessageParser.BuildResponse(
            QueryHeader(), Question("repeated.name.example.com"), answers);

        // 未压缩时 owner name 需 27 字节 × 5 次 = 135 字节；压缩后应远小于此
        var uncompressedEstimate = 27 * 5;
        response.Length.Should().BeLessThan(DnsHeader.Size + uncompressedEstimate);
        DnsHeader.FromBytes(response).AnswerCount.Should().Be(4);
    }

    [Fact]
    public void ParseAndRebuild_ShouldRoundTrip_RealisticQueryWithEdns()
    {
        // 构造一个带 EDNS0 的真实查询，解析后再建响应，验证端到端一致
        var writer = new DnsWriter(128);
        writer.WriteHeader(new DnsHeader
        {
            TransactionId = 0xABCD,
            Flags = 0x0120, // RD + AD
            QuestionCount = 1,
            AdditionalCount = 1
        });
        writer.WriteDomainName("www.example.com", useCompression: false);
        writer.WriteUInt16((ushort)DnsRecordType.A);
        writer.WriteUInt16(1);
        writer.WriteByte(0);                                  // OPT 根域名
        writer.WriteUInt16((ushort)DnsRecordType.OPT);
        writer.WriteUInt16(1232);                             // UDP payload size
        writer.WriteUInt32(0);
        writer.WriteUInt16(0);

        var query = DnsMessageParser.Parse(writer.ToArray());

        query.Questions.Should().HaveCount(1);
        query.Questions[0].Name.Should().Be("www.example.com");
        query.Edns.Should().NotBeNull();
        query.Edns!.UdpPayloadSize.Should().Be(1232);
        query.MaxUdpResponseSize.Should().Be(1232);
        query.Header.TransactionId.Should().Be(0xABCD);
    }
}
