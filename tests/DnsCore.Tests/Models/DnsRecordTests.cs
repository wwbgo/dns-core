using DnsCore.Models;
using FluentAssertions;

namespace DnsCore.Tests.Models;

public class DnsRecordTests
{
    [Fact]
    public void DnsRecord_ShouldInitializeWithDefaultValues()
    {
        // Act
        var record = new DnsRecord
        {
            Domain = string.Empty,
            Type = DnsRecordType.A,
            Value = string.Empty
        };

        // Assert
        record.Domain.Should().BeEmpty();
        record.Value.Should().BeEmpty();
        record.TTL.Should().Be(3600);
    }

    [Fact]
    public void DnsRecord_ShouldStoreAllProperties()
    {
        // Arrange & Act
        var record = new DnsRecord
        {
            Domain = "example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.1",
            TTL = 7200
        };

        // Assert
        record.Domain.Should().Be("example.com");
        record.Type.Should().Be(DnsRecordType.A);
        record.Value.Should().Be("192.168.1.1");
        record.TTL.Should().Be(7200);
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var record = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.A,
            Value = "10.0.0.1",
            TTL = 300
        };

        // Act
        var result = record.ToString();

        // Assert
        result.Should().Be("test.com A 10.0.0.1 (TTL: 300)");
    }

    [Theory]
    [InlineData(DnsRecordType.A)]
    [InlineData(DnsRecordType.AAAA)]
    [InlineData(DnsRecordType.CNAME)]
    [InlineData(DnsRecordType.TXT)]
    [InlineData(DnsRecordType.MX)]
    public void DnsRecord_ShouldSupportDifferentRecordTypes(DnsRecordType type)
    {
        // Arrange & Act
        var record = new DnsRecord
        {
            Domain = "test.com",
            Type = type,
            Value = "test"
        };

        // Assert
        record.Type.Should().Be(type);
    }
}
