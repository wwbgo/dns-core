using DnsCore.Models;
using DnsCore.Protocol;
using FluentAssertions;

namespace DnsCore.Tests.Protocol;

public class DnsMessageParserTests
{
    [Fact]
    public void ParseQuery_ShouldParseSimpleQuery()
    {
        // Arrange - 一个查询 "example.com" A 记录的 DNS 请求
        var queryData = new byte[]
        {
            0x12, 0x34, // Transaction ID
            0x01, 0x00, // Flags (standard query)
            0x00, 0x01, // Questions: 1
            0x00, 0x00, // Answers: 0
            0x00, 0x00, // Authority: 0
            0x00, 0x00, // Additional: 0
            // Question: example.com
            0x07, 0x65, 0x78, 0x61, 0x6d, 0x70, 0x6c, 0x65, // "example"
            0x03, 0x63, 0x6f, 0x6d, // "com"
            0x00, // End of domain name
            0x00, 0x01, // Type: A
            0x00, 0x01  // Class: IN
        };

        // Act
        var (header, questions) = DnsMessageParser.ParseQuery(queryData);

        // Assert
        header.TransactionId.Should().Be(0x1234);
        header.QuestionCount.Should().Be(1);
        questions.Should().HaveCount(1);
        questions[0].Name.Should().Be("example.com");
        questions[0].Type.Should().Be(DnsRecordType.A);
        questions[0].Class.Should().Be(1);
    }

    [Fact]
    public void BuildResponse_ShouldCreateValidResponse()
    {
        // Arrange
        var header = new DnsHeader
        {
            TransactionId = 0x1234,
            Flags = 0x0100,
            QuestionCount = 1
        };

        var questions = new List<DnsQuestion>
        {
            new DnsQuestion
            {
                Name = "test.com",
                Type = DnsRecordType.A,
                Class = 1
            }
        };

        var answers = new List<DnsRecord>
        {
            new DnsRecord
            {
                Domain = "test.com",
                Type = DnsRecordType.A,
                Value = "192.168.1.1",
                TTL = 3600
            }
        };

        // Act
        var response = DnsMessageParser.BuildResponse(header, questions, answers);

        // Assert
        response.Should().NotBeNull();
        response.Length.Should().BeGreaterThan(12); // 至少包含 header

        // 验证响应头
        var responseHeader = DnsHeader.FromBytes(response);
        responseHeader.IsResponse.Should().BeTrue();
        responseHeader.AnswerCount.Should().Be(1);
    }

    [Fact]
    public void BuildResponse_ShouldHandleIPv4Address()
    {
        // Arrange
        var header = new DnsHeader { TransactionId = 1 };
        var questions = new List<DnsQuestion>
        {
            new DnsQuestion { Name = "test.com", Type = DnsRecordType.A }
        };
        var answers = new List<DnsRecord>
        {
            new DnsRecord
            {
                Domain = "test.com",
                Type = DnsRecordType.A,
                Value = "10.20.30.40",
                TTL = 300
            }
        };

        // Act
        var response = DnsMessageParser.BuildResponse(header, questions, answers);

        // Assert
        response.Should().NotBeNull();
        response.Should().Contain((byte)10);
        response.Should().Contain((byte)20);
        response.Should().Contain((byte)30);
        response.Should().Contain((byte)40);
    }

    [Fact]
    public void BuildResponse_ShouldHandleCNAMERecord()
    {
        // Arrange
        var header = new DnsHeader { TransactionId = 1 };
        var questions = new List<DnsQuestion>
        {
            new DnsQuestion { Name = "www.test.com", Type = DnsRecordType.CNAME }
        };
        var answers = new List<DnsRecord>
        {
            new DnsRecord
            {
                Domain = "www.test.com",
                Type = DnsRecordType.CNAME,
                Value = "test.com",
                TTL = 3600
            }
        };

        // Act
        var response = DnsMessageParser.BuildResponse(header, questions, answers);

        // Assert
        response.Should().NotBeNull();
        response.Length.Should().BeGreaterThan(12);
    }

    [Fact]
    public void BuildResponse_ShouldHandleTXTRecord()
    {
        // Arrange
        var header = new DnsHeader { TransactionId = 1 };
        var questions = new List<DnsQuestion>
        {
            new DnsQuestion { Name = "test.com", Type = DnsRecordType.TXT }
        };
        var answers = new List<DnsRecord>
        {
            new DnsRecord
            {
                Domain = "test.com",
                Type = DnsRecordType.TXT,
                Value = "v=spf1 include:_spf.google.com ~all",
                TTL = 3600
            }
        };

        // Act
        var response = DnsMessageParser.BuildResponse(header, questions, answers);

        // Assert
        response.Should().NotBeNull();
        response.Length.Should().BeGreaterThan(12);
    }

    [Fact]
    public void BuildResponse_ShouldHandleMultipleAnswers()
    {
        // Arrange
        var header = new DnsHeader { TransactionId = 1 };
        var questions = new List<DnsQuestion>
        {
            new DnsQuestion { Name = "test.com", Type = DnsRecordType.A }
        };
        var answers = new List<DnsRecord>
        {
            new DnsRecord
            {
                Domain = "test.com",
                Type = DnsRecordType.A,
                Value = "192.168.1.1",
                TTL = 3600
            },
            new DnsRecord
            {
                Domain = "test.com",
                Type = DnsRecordType.A,
                Value = "192.168.1.2",
                TTL = 3600
            }
        };

        // Act
        var response = DnsMessageParser.BuildResponse(header, questions, answers);

        // Assert
        response.Should().NotBeNull();
        var responseHeader = DnsHeader.FromBytes(response);
        responseHeader.AnswerCount.Should().Be(2);
    }

    [Fact]
    public void BuildResponse_ShouldSetResponseFlags()
    {
        // Arrange
        var header = new DnsHeader
        {
            TransactionId = 0x5678,
            Flags = 0x0100 // Query flag
        };
        var questions = new List<DnsQuestion>();
        var answers = new List<DnsRecord>();

        // Act
        var response = DnsMessageParser.BuildResponse(header, questions, answers);

        // Assert
        var responseHeader = DnsHeader.FromBytes(response);
        responseHeader.IsResponse.Should().BeTrue();
        (responseHeader.Flags & 0x8000).Should().Be(0x8000); // QR bit
        (responseHeader.Flags & 0x0400).Should().Be(0x0400); // AA bit
        (responseHeader.Flags & 0x0080).Should().Be(0x0080); // RA bit
    }

    [Fact]
    public void ParseQuery_ShouldHandleMultipleDomainLabels()
    {
        // Arrange - 查询 "sub.domain.example.com"
        var queryData = new byte[]
        {
            0x00, 0x01, // Transaction ID
            0x01, 0x00, // Flags
            0x00, 0x01, // Questions: 1
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Answers, Authority, Additional
            // Question: sub.domain.example.com
            0x03, 0x73, 0x75, 0x62, // "sub"
            0x06, 0x64, 0x6f, 0x6d, 0x61, 0x69, 0x6e, // "domain"
            0x07, 0x65, 0x78, 0x61, 0x6d, 0x70, 0x6c, 0x65, // "example"
            0x03, 0x63, 0x6f, 0x6d, // "com"
            0x00, // End
            0x00, 0x01, // Type: A
            0x00, 0x01  // Class: IN
        };

        // Act
        var (header, questions) = DnsMessageParser.ParseQuery(queryData);

        // Assert
        questions[0].Name.Should().Be("sub.domain.example.com");
    }
}
