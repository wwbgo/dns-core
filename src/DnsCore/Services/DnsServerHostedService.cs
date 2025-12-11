namespace DnsCore.Services;

/// <summary>
/// DNS server background hosted service
/// </summary>
public sealed class DnsServerHostedService(
    DnsServer dnsServer,
    ILogger<DnsServerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("DNS server background service is starting...");
            await dnsServer.StartAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DNS server failed to run");
            throw;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("DNS server background service is stopping...");
        dnsServer.Stop();
        return base.StopAsync(cancellationToken);
    }
}
