namespace DnsCore.Services;

/// <summary>
/// DNS 缓存清理后台服务
/// </summary>
public sealed class DnsCacheCleanupService(
    ILogger<DnsCacheCleanupService> logger,
    DnsCache dnsCache) : BackgroundService
{
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DNS cache cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);

                dnsCache.CleanupExpired();

                var (total, active) = dnsCache.GetStats();
                logger.LogDebug("Cache stats - Total: {Total}, Active: {Active}", total, active);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in DNS cache cleanup service");
            }
        }

        logger.LogInformation("DNS cache cleanup service stopped");
    }
}
