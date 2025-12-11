namespace DnsCore.Services;

/// <summary>
/// DNS 服务器后台托管服务
/// </summary>
public sealed class DnsServerHostedService(
    DnsServer dnsServer,
    ILogger<DnsServerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("DNS 服务器后台服务正在启动...");
            await dnsServer.StartAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DNS 服务器运行失败");
            throw;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("DNS 服务器后台服务正在停止...");
        dnsServer.Stop();
        return base.StopAsync(cancellationToken);
    }
}
