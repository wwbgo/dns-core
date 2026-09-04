using DnsCore.Models;
using DnsCore.Services;
using FluentAssertions;

namespace DnsCore.Tests.Services;

public sealed class DnsRecordOrdererTests
{
    private static DnsRecord Record(string value, int weight = 1) => new()
    {
        Domain = "roundrobin.test",
        Type = DnsRecordType.A,
        Value = value,
        TTL = 60,
        Weight = weight
    };

    [Fact]
    public void Rotate_ShouldKeepOrder_ForSingleRecord()
    {
        var records = new List<DnsRecord> { Record("1.1.1.1") };

        var rotated = DnsRecordOrderer.Rotate(records, 5);

        rotated.Should().ContainSingle();
        rotated[0].Value.Should().Be("1.1.1.1");
    }

    [Fact]
    public void Rotate_ShouldMoveOffsetRecords_ToTheEnd()
    {
        var records = new List<DnsRecord>
        {
            Record("1.1.1.1"),
            Record("1.1.1.2"),
            Record("1.1.1.3")
        };

        var rotated = DnsRecordOrderer.Rotate(records, 1);

        rotated.Select(r => r.Value).Should().Equal("1.1.1.2", "1.1.1.3", "1.1.1.1");
    }

    [Fact]
    public void Rotate_ShouldNormalizeOffset_LargerThanCount()
    {
        var records = new List<DnsRecord>
        {
            Record("1.1.1.1"),
            Record("1.1.1.2")
        };

        var rotated = DnsRecordOrderer.Rotate(records, 3);

        rotated.Select(r => r.Value).Should().Equal("1.1.1.2", "1.1.1.1");
    }

    [Fact]
    public void Rotate_ShouldNotMutateOriginalList()
    {
        var records = new List<DnsRecord>
        {
            Record("1.1.1.1"),
            Record("1.1.1.2")
        };

        _ = DnsRecordOrderer.Rotate(records, 1);

        records.Select(r => r.Value).Should().Equal("1.1.1.1", "1.1.1.2");
    }

    [Fact]
    public void WeightedRoundRobin_ShouldPutHeavierRecordFirst_MoreOften()
    {
        var records = new List<DnsRecord>
        {
            Record("1.1.1.1", weight: 1),
            Record("1.1.1.2", weight: 3)
        };

        var first = Enumerable.Range(1, 4)
            .Select(sequence => DnsRecordOrderer.WeightedRoundRobin(records, sequence)[0].Value)
            .ToList();

        first.Should().Equal("1.1.1.2", "1.1.1.2", "1.1.1.2", "1.1.1.1");
    }

    [Fact]
    public void OrderForQuery_ShouldUseRotation_WhenWeightsAreEqual()
    {
        var records = new List<DnsRecord>
        {
            Record("1.1.1.1"),
            Record("1.1.1.2")
        };

        DnsRecordOrderer.OrderForQuery(records, 1)
            .Select(r => r.Value)
            .Should().Equal("1.1.1.2", "1.1.1.1");
    }
}
