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

                if (source.Paused)
                    return;

                if (source.LastSyncedAtUtc is { } lastSynced
                    && now - lastSynced < TimeSpan.FromMinutes(source.SyncIntervalMinutes))
                {
                    return;
                }

                try
                {
                    var result = await importService.ImportUrlForSourceAsync(source.Id, source.Url, source.Ttl);
                    await sourceStore.UpdateSyncStatusAsync(source.Id, DateTime.UtcNow, null);

                    logger.LogInformation(
                        "hosts URL source sync succeeded: {Name}, imported {Imported}, skipped duplicates {Skipped}",
                        source.Name,
                        result.Imported,
                        result.SkippedDuplicates);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "hosts URL source sync timed out: {Name} {Url}",
                        source.Name,
                        source.Url);

                    await sourceStore.UpdateSyncErrorAsync(source.Id, "Request timed out");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "hosts URL source sync failed: {Name} {Url}", source.Name, source.Url);

                    // 失败时不推进 LastSyncedAtUtc，下一轮检查会继续重试；
                    // 否则一次瞬时网络错误会导致整个同步周期内本地记录缺失。
                    await sourceStore.UpdateSyncErrorAsync(source.Id, ex.Message);
                }
            });
    }
}
