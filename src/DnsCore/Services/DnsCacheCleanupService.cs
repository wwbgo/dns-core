using DnsCore.Configuration;

namespace DnsCore.Services;

/// <summary>
/// DNS 缓存过期清理后台服务
/// </summary>
public sealed class DnsCacheCleanupService(
    ILogger<DnsCacheCleanupService> logger,
    DnsCache dnsCache,
    DnsServerOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Cache.Enabled)
        {
            logger.LogInformation("Cache is disabled; cleanup service will not start");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Cache.CleanupIntervalSeconds));
        logger.LogInformation("DNS cache cleanup service started; interval: {Interval}s", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    dnsCache.CleanupExpired();

                    var stats = dnsCache.GetStats();
                    logger.LogDebug("Cache statistics - total: {Total}, active: {Active}, hit rate: {HitRate:P1}",
                        stats.TotalEntries, stats.ActiveEntries, stats.HitRate);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DNS cache cleanup failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }

        logger.LogInformation("DNS cache cleanup service stopped");
    }
}
