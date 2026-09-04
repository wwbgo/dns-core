using DnsCore.Models;
using DnsCore.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DnsCore.Tests.Services;

public sealed class HostsImportServiceTests
{
    [Fact]
    public async Task ImportTextAsync_ShouldSkipExistingAndDuplicateRecords()
    {
        var storeLogger = new Mock<ILogger<CustomRecordStore>>();
        var importLogger = new Mock<ILogger<HostsImportService>>();
        var store = new CustomRecordStore(storeLogger.Object);
        store.AddRecord(new DnsRecord
        {
            Domain = "app.local",
            Type = DnsRecordType.A,
            Value = "10.0.0.1",
            TTL = 60
        });

        var service = new HostsImportService(importLogger.Object, store);

        const string hosts = """
            10.0.0.1 app.local
            10.0.0.2 app.local
            10.0.0.2 app.local
            10.0.0.3 other.local
            """;

        var result = await service.ImportTextAsync(hosts, ttl: 60);

        result.Imported.Should().Be(2);
        result.SkippedDuplicates.Should().Be(2);
        store.Query("app.local", DnsRecordType.A)!.Should().HaveCount(2);
        store.Query("other.local", DnsRecordType.A)!.Should().ContainSingle();
    }
}
