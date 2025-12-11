using DnsCore.Models;
using DnsCore.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DnsCore.Tests.Services;

public class CustomRecordStoreTests
{
    private readonly CustomRecordStore _store;
    private readonly Mock<ILogger<CustomRecordStore>> _loggerMock;

    public CustomRecordStoreTests()
    {
        _loggerMock = new Mock<ILogger<CustomRecordStore>>();
        _store = new CustomRecordStore(_loggerMock.Object);
    }

    [Fact]
    public void AddRecord_ShouldStoreRecord()
    {
        // Arrange
        var record = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.1",
            TTL = 3600
        };

        // Act
        _store.AddRecord(record);

        // Assert
        var result = _store.Query("test.com", DnsRecordType.A);
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result![0].Value.Should().Be("192.168.1.1");
    }

    [Fact]
    public void Query_ShouldReturnNull_WhenRecordNotFound()
    {
        // Act
        var result = _store.Query("nonexistent.com", DnsRecordType.A);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Query_ShouldBeCaseInsensitive()
    {
        // Arrange
        var record = new DnsRecord
        {
            Domain = "Example.COM",
            Type = DnsRecordType.A,
            Value = "10.0.0.1"
        };
        _store.AddRecord(record);

        // Act
        var result1 = _store.Query("example.com", DnsRecordType.A);
        var result2 = _store.Query("EXAMPLE.COM", DnsRecordType.A);
        var result3 = _store.Query("Example.Com", DnsRecordType.A);

        // Assert
        result1.Should().NotBeNull().And.HaveCount(1);
        result2.Should().NotBeNull().And.HaveCount(1);
        result3.Should().NotBeNull().And.HaveCount(1);
    }

    [Fact]
    public void AddRecord_ShouldSupportMultipleRecordsForSameDomain()
    {
        // Arrange
        var record1 = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.1"
        };
        var record2 = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.2"
        };

        // Act
        _store.AddRecord(record1);
        _store.AddRecord(record2);

        // Assert
        var result = _store.Query("test.com", DnsRecordType.A);
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result!.Select(r => r.Value).Should().Contain(new[] { "192.168.1.1", "192.168.1.2" });
    }

    [Fact]
    public void AddRecord_ShouldSupportDifferentRecordTypes()
    {
        // Arrange
        var aRecord = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.1"
        };
        var cnameRecord = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.CNAME,
            Value = "alias.test.com"
        };

        // Act
        _store.AddRecord(aRecord);
        _store.AddRecord(cnameRecord);

        // Assert
        var aResult = _store.Query("test.com", DnsRecordType.A);
        var cnameResult = _store.Query("test.com", DnsRecordType.CNAME);

        aResult.Should().NotBeNull().And.HaveCount(1);
        aResult![0].Type.Should().Be(DnsRecordType.A);

        cnameResult.Should().NotBeNull().And.HaveCount(1);
        cnameResult![0].Type.Should().Be(DnsRecordType.CNAME);
    }

    [Fact]
    public void Query_WithANYType_ShouldReturnAllRecordsForDomain()
    {
        // Arrange
        var aRecord = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.1"
        };
        var txtRecord = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.TXT,
            Value = "v=spf1"
        };

        _store.AddRecord(aRecord);
        _store.AddRecord(txtRecord);

        // Act
        var result = _store.Query("test.com", DnsRecordType.ANY);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result!.Should().Contain(r => r.Type == DnsRecordType.A);
        result!.Should().Contain(r => r.Type == DnsRecordType.TXT);
    }

    [Fact]
    public void RemoveRecord_ShouldRemoveExistingRecord()
    {
        // Arrange
        var record = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.1"
        };
        _store.AddRecord(record);

        // Act
        var removed = _store.RemoveRecord("test.com", DnsRecordType.A);

        // Assert
        removed.Should().BeTrue();
        var result = _store.Query("test.com", DnsRecordType.A);
        result.Should().BeNull();
    }

    [Fact]
    public void RemoveRecord_ShouldReturnFalse_WhenRecordNotFound()
    {
        // Act
        var removed = _store.RemoveRecord("nonexistent.com", DnsRecordType.A);

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public void Clear_ShouldRemoveAllRecords()
    {
        // Arrange
        _store.AddRecord(new DnsRecord { Domain = "test1.com", Type = DnsRecordType.A, Value = "1.1.1.1" });
        _store.AddRecord(new DnsRecord { Domain = "test2.com", Type = DnsRecordType.A, Value = "2.2.2.2" });

        // Act
        _store.Clear();

        // Assert
        _store.Query("test1.com", DnsRecordType.A).Should().BeNull();
        _store.Query("test2.com", DnsRecordType.A).Should().BeNull();
    }

    [Fact]
    public void GetAllRecords_ShouldReturnAllStoredRecords()
    {
        // Arrange
        var record1 = new DnsRecord { Domain = "test1.com", Type = DnsRecordType.A, Value = "1.1.1.1" };
        var record2 = new DnsRecord { Domain = "test2.com", Type = DnsRecordType.A, Value = "2.2.2.2" };
        var record3 = new DnsRecord { Domain = "test3.com", Type = DnsRecordType.AAAA, Value = "::1" };

        _store.AddRecord(record1);
        _store.AddRecord(record2);
        _store.AddRecord(record3);

        // Act
        var allRecords = _store.GetAllRecords().ToList();

        // Assert
        allRecords.Should().HaveCount(3);
        allRecords.Should().Contain(record1);
        allRecords.Should().Contain(record2);
        allRecords.Should().Contain(record3);
    }

    [Fact]
    public void AddRecords_ShouldAddMultipleRecordsAtOnce()
    {
        // Arrange
        var records = new List<DnsRecord>
        {
            new DnsRecord { Domain = "test1.com", Type = DnsRecordType.A, Value = "1.1.1.1" },
            new DnsRecord { Domain = "test2.com", Type = DnsRecordType.A, Value = "2.2.2.2" },
            new DnsRecord { Domain = "test3.com", Type = DnsRecordType.AAAA, Value = "::1" }
        };

        // Act
        _store.AddRecords(records);

        // Assert
        _store.Query("test1.com", DnsRecordType.A).Should().NotBeNull();
        _store.Query("test2.com", DnsRecordType.A).Should().NotBeNull();
        _store.Query("test3.com", DnsRecordType.AAAA).Should().NotBeNull();
    }

    [Fact]
    public void Query_ShouldReturnCopyOfRecords()
    {
        // Arrange
        var record = new DnsRecord
        {
            Domain = "test.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.1"
        };
        _store.AddRecord(record);

        // Act
        var result1 = _store.Query("test.com", DnsRecordType.A);
        var result2 = _store.Query("test.com", DnsRecordType.A);

        // Assert
        result1.Should().NotBeSameAs(result2); // 应该返回不同的列表实例
    }

    // ============ 泛域名测试 ============

    [Fact]
    public void Query_ShouldMatchWildcardDomain_BasicCase()
    {
        // Arrange
        var wildcardRecord = new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.100"
        };
        _store.AddRecord(wildcardRecord);

        // Act
        var result1 = _store.Query("www.example.com", DnsRecordType.A);
        var result2 = _store.Query("api.example.com", DnsRecordType.A);
        var result3 = _store.Query("anything.example.com", DnsRecordType.A);

        // Assert
        result1.Should().NotBeNull().And.HaveCount(1);
        result1![0].Value.Should().Be("192.168.1.100");

        result2.Should().NotBeNull().And.HaveCount(1);
        result2![0].Value.Should().Be("192.168.1.100");

        result3.Should().NotBeNull().And.HaveCount(1);
        result3![0].Value.Should().Be("192.168.1.100");
    }

    [Fact]
    public void Query_ShouldNotMatchWildcardDomain_ForBaseDomain()
    {
        // Arrange
        var wildcardRecord = new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.100"
        };
        _store.AddRecord(wildcardRecord);

        // Act
        var result = _store.Query("example.com", DnsRecordType.A);

        // Assert
        result.Should().BeNull(); // 基础域名不应匹配泛域名
    }

    [Fact]
    public void Query_ShouldPreferExactMatchOverWildcard()
    {
        // Arrange
        var wildcardRecord = new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.100"
        };
        var exactRecord = new DnsRecord
        {
            Domain = "www.example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.200"
        };
        _store.AddRecord(wildcardRecord);
        _store.AddRecord(exactRecord);

        // Act
        var result = _store.Query("www.example.com", DnsRecordType.A);

        // Assert
        result.Should().NotBeNull().And.HaveCount(1);
        result![0].Value.Should().Be("192.168.1.200"); // 精确匹配优先
    }

    [Fact]
    public void Query_ShouldMatchMultiLevelWildcard()
    {
        // Arrange
        var wildcardRecord = new DnsRecord
        {
            Domain = "*.dev.example.com",
            Type = DnsRecordType.A,
            Value = "10.0.0.1"
        };
        _store.AddRecord(wildcardRecord);

        // Act
        var result1 = _store.Query("api.dev.example.com", DnsRecordType.A);
        var result2 = _store.Query("www.dev.example.com", DnsRecordType.A);

        // Assert
        result1.Should().NotBeNull().And.HaveCount(1);
        result1![0].Value.Should().Be("10.0.0.1");

        result2.Should().NotBeNull().And.HaveCount(1);
        result2![0].Value.Should().Be("10.0.0.1");
    }

    [Fact]
    public void Query_ShouldMatchMostSpecificWildcard()
    {
        // Arrange
        var broadWildcard = new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.1"
        };
        var specificWildcard = new DnsRecord
        {
            Domain = "*.dev.example.com",
            Type = DnsRecordType.A,
            Value = "10.0.0.1"
        };
        _store.AddRecord(broadWildcard);
        _store.AddRecord(specificWildcard);

        // Act
        var result1 = _store.Query("api.dev.example.com", DnsRecordType.A);
        var result2 = _store.Query("www.example.com", DnsRecordType.A);

        // Assert
        result1.Should().NotBeNull().And.HaveCount(1);
        result1![0].Value.Should().Be("10.0.0.1"); // 最具体的泛域名

        result2.Should().NotBeNull().And.HaveCount(1);
        result2![0].Value.Should().Be("192.168.1.1"); // 较宽泛的泛域名
    }

    [Fact]
    public void Query_WildcardShouldBeCaseInsensitive()
    {
        // Arrange
        var wildcardRecord = new DnsRecord
        {
            Domain = "*.Example.COM",
            Type = DnsRecordType.A,
            Value = "192.168.1.100"
        };
        _store.AddRecord(wildcardRecord);

        // Act
        var result1 = _store.Query("www.example.com", DnsRecordType.A);
        var result2 = _store.Query("API.EXAMPLE.COM", DnsRecordType.A);
        var result3 = _store.Query("Test.Example.Com", DnsRecordType.A);

        // Assert
        result1.Should().NotBeNull().And.HaveCount(1);
        result2.Should().NotBeNull().And.HaveCount(1);
        result3.Should().NotBeNull().And.HaveCount(1);
    }

    [Fact]
    public void Query_ShouldSupportWildcardForDifferentRecordTypes()
    {
        // Arrange
        var aRecord = new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.100"
        };
        var txtRecord = new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.TXT,
            Value = "v=spf1 ~all"
        };
        _store.AddRecord(aRecord);
        _store.AddRecord(txtRecord);

        // Act
        var aResult = _store.Query("www.example.com", DnsRecordType.A);
        var txtResult = _store.Query("www.example.com", DnsRecordType.TXT);

        // Assert
        aResult.Should().NotBeNull().And.HaveCount(1);
        aResult![0].Type.Should().Be(DnsRecordType.A);

        txtResult.Should().NotBeNull().And.HaveCount(1);
        txtResult![0].Type.Should().Be(DnsRecordType.TXT);
    }

    [Fact]
    public void Query_ShouldMatchDeepSubdomain()
    {
        // Arrange
        var wildcardRecord = new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.100"
        };
        _store.AddRecord(wildcardRecord);

        // Act
        var result = _store.Query("a.b.c.d.example.com", DnsRecordType.A);

        // Assert
        result.Should().NotBeNull().And.HaveCount(1);
        result![0].Value.Should().Be("192.168.1.100");
    }

    [Fact]
    public void Query_ShouldNotMatchWildcardAcrossDifferentBaseDomains()
    {
        // Arrange
        var wildcardRecord = new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.A,
            Value = "192.168.1.100"
        };
        _store.AddRecord(wildcardRecord);

        // Act
        var result = _store.Query("www.different.com", DnsRecordType.A);

        // Assert
        result.Should().BeNull();
    }
}
