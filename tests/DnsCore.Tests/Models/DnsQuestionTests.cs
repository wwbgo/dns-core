using DnsCore.Models;
using FluentAssertions;

namespace DnsCore.Tests.Models;

public class DnsQuestionTests
{
    [Fact]
    public void DnsQuestion_ShouldInitializeWithDefaultValues()
    {
        // Act
        var question = new DnsQuestion
        {
            Name = string.Empty,
            Type = DnsRecordType.A
        };

        // Assert
        question.Name.Should().BeEmpty();
        question.Class.Should().Be(1); // IN (Internet)
    }

    [Fact]
    public void DnsQuestion_ShouldStoreAllProperties()
    {
        // Arrange & Act
        var question = new DnsQuestion
        {
            Name = "example.com",
            Type = DnsRecordType.A,
            Class = 1
        };

        // Assert
        question.Name.Should().Be("example.com");
        question.Type.Should().Be(DnsRecordType.A);
        question.Class.Should().Be(1);
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var question = new DnsQuestion
        {
            Name = "test.com",
            Type = DnsRecordType.AAAA,
            Class = 1
        };

        // Act
        var result = question.ToString();

        // Assert
        result.Should().Be("test.com AAAA 1");
    }
}
