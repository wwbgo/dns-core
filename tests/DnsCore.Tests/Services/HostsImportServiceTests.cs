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

    [Fact]
    public async Task ImportSourceTextAsync_ShouldUpdateAndCleanupOwnedRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dnscore-hosts-own-{Guid.NewGuid():N}.json");

        try
        {
            var storeLogger = new Mock<ILogger<CustomRecordStore>>();
            var importLogger = new Mock<ILogger<HostsImportService>>();
            var ownershipLogger = new Mock<ILogger<HostsSourceRecordStore>>();
            var store = new CustomRecordStore(storeLogger.Object);
            var ownership = new HostsSourceRecordStore(ownershipLogger.Object, path);
            var service = new HostsImportService(importLogger.Object, store, null, false, ownership);

            const string first = """
                10.0.0.1 app.local
                10.0.0.2 old.local
                """;
            const string second = """
                10.0.0.1 app.local
                10.0.0.3 new.local
                """;

            var firstResult = await service.ImportSourceTextAsync("source-1", first, ttl: 60);
            firstResult.Imported.Should().Be(2);
            store.Query("app.local", DnsRecordType.A)![0].TTL.Should().Be(60);
            store.Query("old.local", DnsRecordType.A).Should().NotBeNull();

            store.Clear();
            var reimportResult = await service.ImportSourceTextAsync("source-1", first, ttl: 60);
            reimportResult.Imported.Should().Be(2);
            store.Query("app.local", DnsRecordType.A).Should().NotBeNull();
            store.Query("old.local", DnsRecordType.A).Should().NotBeNull();

            var secondResult = await service.ImportSourceTextAsync("source-1", second, ttl: 120);

            secondResult.Imported.Should().Be(1);
            store.Query("app.local", DnsRecordType.A)![0].TTL.Should().Be(120);
            store.Query("old.local", DnsRecordType.A).Should().BeNull();
            store.Query("new.local", DnsRecordType.A).Should().NotBeNull();

            var reloadedOwnership = new HostsSourceRecordStore(ownershipLogger.Object, path);
            await reloadedOwnership.LoadAsync();
            var owners = await reloadedOwnership.GetSourceIdsForRecordAsync(
                "app.local", DnsRecordType.A, "10.0.0.1");
            owners.Should().Contain("source-1");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
