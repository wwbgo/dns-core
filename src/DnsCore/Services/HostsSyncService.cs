namespace DnsCore.Services;

/// <summary>
/// 按 hosts URL 来源配置的同步周期，定期拉取并导入 hosts 内容。
/// </summary>
public sealed class HostsSyncService(
    ILogger<HostsSyncService> logger,
    HostsSourceStore sourceStore,
    HostsImportService importService) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);

        do
        {
            await SyncDueSourcesAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncDueSourcesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var sources = await sourceStore.GetAllAsync();

        await Parallel.ForEachAsync(
            sources,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            },
            async (source, token) =>
            {
                token.ThrowIfCancellationRequested();

                if (source.LastSyncedAtUtc is { } lastSynced
                    && now - lastSynced < TimeSpan.FromMinutes(source.SyncIntervalMinutes))
                {
                    return;
                }

                try
                {
                    var result = await importService.ImportUrlAsync(source.Url, source.Ttl);
                    await sourceStore.UpdateSyncStatusAsync(source.Id, DateTime.UtcNow, null);

                    logger.LogInformation(
                        "hosts URL 来源同步成功: {Name}，导入 {Imported} 条，跳过重复 {Skipped} 条",
                        source.Name,
                        result.Imported,
                        result.SkippedDuplicates);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "hosts URL 来源同步失败: {Name} {Url}", source.Name, source.Url);
                    await sourceStore.UpdateSyncStatusAsync(source.Id, DateTime.UtcNow, ex.Message);
                }
            });
    }
}
