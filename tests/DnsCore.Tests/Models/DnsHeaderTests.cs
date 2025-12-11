using DnsCore.Models;
using FluentAssertions;

namespace DnsCore.Tests.Models;

public class DnsHeaderTests
{
    [Fact]
    public void ToBytes_ShouldSerializeHeaderCorrectly()
    {
        // Arrange
        var header = new DnsHeader
        {
            TransactionId = 0x1234,
            Flags = 0x0100,
            QuestionCount = 1,
            AnswerCount = 0,
            AuthorityCount = 0,
            AdditionalCount = 0
        };

        // Act
        var bytes = header.ToBytes();

        // Assert
        bytes.Should().HaveCount(12);
        bytes[0].Should().Be(0x12);
        bytes[1].Should().Be(0x34);
        bytes[2].Should().Be(0x01);
        bytes[3].Should().Be(0x00);
    }

    [Fact]
    public void FromBytes_ShouldDeserializeHeaderCorrectly()
    {
        // Arrange
        var bytes = new byte[]
        {
            0x12, 0x34, // Transaction ID
            0x01, 0x00, // Flags
            0x00, 0x01, // Question Count
            0x00, 0x02, // Answer Count
            0x00, 0x00, // Authority Count
            0x00, 0x00  // Additional Count
        };

        // Act
        var header = DnsHeader.FromBytes(bytes);

        // Assert
        header.TransactionId.Should().Be(0x1234);
        header.Flags.Should().Be(0x0100);
        header.QuestionCount.Should().Be(1);
        header.AnswerCount.Should().Be(2);
        header.AuthorityCount.Should().Be(0);
        header.AdditionalCount.Should().Be(0);
    }

    [Fact]
    public void IsQuery_ShouldReturnTrue_WhenQRBitIsZero()
    {
        // Arrange
        var header = new DnsHeader { Flags = 0x0100 }; // QR = 0

        // Act & Assert
        header.IsQuery.Should().BeTrue();
        header.IsResponse.Should().BeFalse();
    }

    [Fact]
    public void IsResponse_ShouldReturnTrue_WhenQRBitIsOne()
    {
        // Arrange
        var header = new DnsHeader { Flags = 0x8100 }; // QR = 1

        // Act & Assert
        header.IsResponse.Should().BeTrue();
        header.IsQuery.Should().BeFalse();
    }

    [Fact]
    public void SetAsResponse_ShouldSetQRAndAABits()
    {
        // Arrange
        var header = new DnsHeader { Flags = 0x0100 };

        // Act
        header.SetAsResponse();

        // Assert
        header.IsResponse.Should().BeTrue();
        (header.Flags & 0x8000).Should().Be(0x8000); // QR bit
        (header.Flags & 0x0400).Should().Be(0x0400); // AA bit
    }

    [Fact]
    public void SetRecursionAvailable_ShouldSetRABit()
    {
        // Arrange
        var header = new DnsHeader { Flags = 0x0000 };

        // Act
        header.SetRecursionAvailable();

        // Assert
        (header.Flags & 0x0080).Should().Be(0x0080); // RA bit
    }

    [Fact]
    public void RoundTrip_ShouldPreserveAllValues()
    {
        // Arrange
        var original = new DnsHeader
        {
            TransactionId = 0xABCD,
            Flags = 0x8180,
            QuestionCount = 5,
            AnswerCount = 10,
            AuthorityCount = 3,
            AdditionalCount = 2
        };

        // Act
        var bytes = original.ToBytes();
        var deserialized = DnsHeader.FromBytes(bytes);

        // Assert
        deserialized.TransactionId.Should().Be(original.TransactionId);
        deserialized.Flags.Should().Be(original.Flags);
        deserialized.QuestionCount.Should().Be(original.QuestionCount);
        deserialized.AnswerCount.Should().Be(original.AnswerCount);
        deserialized.AuthorityCount.Should().Be(original.AuthorityCount);
        deserialized.AdditionalCount.Should().Be(original.AdditionalCount);
    }
}
