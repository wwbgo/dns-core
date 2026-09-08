using System.Text.Json;
using DnsCore.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DnsCore.Tests.Services;

public sealed class HostsSourceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "dnscore-hosts-sources",
        Guid.NewGuid().ToString("N"));

    public HostsSourceStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task UpdateSyncErrorAsync_ShouldKeepLastSuccessfulSyncTime()
    {
        var path = Path.Combine(_dir, "hosts-sources.json");
        var store = new HostsSourceStore(new Mock<ILogger<HostsSourceStore>>().Object, path);
        var source = await store.AddAsync(
            "example",
            "https://example.com/hosts",
            syncIntervalMinutes: 60,
            ttl: 3600);

        var successTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await store.UpdateSyncStatusAsync(source.Id, successTime, null);
        await store.UpdateSyncErrorAsync(source.Id, "network timeout");

        var reloaded = await store.GetAsync(source.Id);
        reloaded.Should().NotBeNull();
        reloaded!.LastSyncedAtUtc.Should().Be(successTime);
        reloaded.LastSyncError.Should().Be("network timeout");
    }

    [Fact]
    public async Task LoadAsync_ShouldNormalizeInvalidPersistedValues()
    {
        var path = Path.Combine(_dir, "hosts-sources.json");
        await File.WriteAllTextAsync(path, """
            [
              {
                "id": "1",
                "name": "broken",
                "url": "https://example.com/hosts",
                "syncIntervalMinutes": 0,
                "ttl": -1
              }
            ]
            """);

        var store = new HostsSourceStore(new Mock<ILogger<HostsSourceStore>>().Object, path);
        await store.LoadAsync();

        var source = (await store.GetAllAsync()).Should().ContainSingle().Subject;
        source.SyncIntervalMinutes.Should().Be(60);
        source.Ttl.Should().Be(3600);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 文件句柄可能尚未释放
        }
    }
}
