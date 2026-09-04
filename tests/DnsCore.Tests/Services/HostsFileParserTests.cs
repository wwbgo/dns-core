using DnsCore.Models;
using DnsCore.Services;
using FluentAssertions;

namespace DnsCore.Tests.Services;

public sealed class HostsFileParserTests
{
    [Fact]
    public void Parse_ShouldCreateARecords_ForIPv4Hosts()
    {
        const string hosts = """
            # comment

            192.168.1.10 app.local alias.local
            """;

        var result = HostsFileParser.Parse(hosts);

        result.Records.Should().HaveCount(2);
        result.Records.Should().OnlyContain(r => r.Type == DnsRecordType.A);
        result.Records.Select(r => r.Value).Should().Contain("192.168.1.10");
        result.Records.Select(r => r.Domain).Should().Contain("app.local", "alias.local");
    }

    [Fact]
    public void Parse_ShouldCreateAaaaRecords_ForIPv6Hosts()
    {
        const string hosts = "2001:db8::1 app.local\n";

        var result = HostsFileParser.Parse(hosts);

        result.Records.Should().ContainSingle();
        result.Records[0].Type.Should().Be(DnsRecordType.AAAA);
        result.Records[0].Value.Should().Be("2001:db8::1");
        result.Records[0].Domain.Should().Be("app.local");
    }

    [Fact]
    public void Parse_ShouldNormalizeIPv4Value()
    {
        var result = HostsFileParser.Parse("192.168.001.001 app.local\n");

        result.Records.Should().ContainSingle();
        result.Records[0].Value.Should().Be("192.168.1.1");
    }

    [Fact]
    public void Parse_ShouldSkipInvalidLines_AndReturnErrors()
    {
        const string hosts = """
            not-an-ip app.local
            192.168.1.10 good.local
            """;

        var result = HostsFileParser.Parse(hosts);

        result.Records.Should().ContainSingle();
        result.Records[0].Domain.Should().Be("good.local");
        result.Errors.Should().NotBeEmpty();
    }
}
