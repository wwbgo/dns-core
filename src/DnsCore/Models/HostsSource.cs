namespace DnsCore.Models;

/// <summary>
/// 可保存的 hosts URL 来源。
/// </summary>
public sealed class HostsSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public required string Url { get; set; }
    public int SyncIntervalMinutes { get; set; } = 60;
    public int Ttl { get; set; } = 3600;
    public DateTime? LastSyncedAtUtc { get; set; }
    public string? LastSyncError { get; set; }
}
