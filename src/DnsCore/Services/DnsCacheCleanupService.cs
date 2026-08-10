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
            logger.LogInformation("缓存已禁用，清理服务不启动");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Cache.CleanupIntervalSeconds));
        logger.LogInformation("DNS 缓存清理服务已启动，间隔 {Interval}s", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    dnsCache.CleanupExpired();

                    var stats = dnsCache.GetStats();
                    logger.LogDebug("缓存统计 - 总计: {Total}, 有效: {Active}, 命中率: {HitRate:P1}",
                        stats.TotalEntries, stats.ActiveEntries, stats.HitRate);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DNS 缓存清理出错");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }

        logger.LogInformation("DNS 缓存清理服务已停止");
    }
}
