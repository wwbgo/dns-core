using System.Net;
using System.Net.Sockets;
using DnsCore.Models;

namespace DnsCore.Services;

/// <summary>
/// hosts 导入结果。
/// </summary>
public sealed record HostsImportResult(
    int Imported,
    int SkippedDuplicates,
    IReadOnlyList<string> Errors);

/// <summary>
/// 将 hosts 文本或 URL 内容导入到自定义 DNS 记录。
/// </summary>
public sealed class HostsImportService(
    ILogger<HostsImportService> logger,
    CustomRecordStore recordStore,
    IHttpClientFactory? httpClientFactory = null,
    bool allowLoopback = false,
    HostsSourceRecordStore? recordOwnershipStore = null)
{
    public const string HostsFileImportSourceId = "__hosts_file_import__";
    public const string HostsUrlImportSourceId = "__hosts_url_import__";

    private static readonly TimeSpan UrlTimeout = TimeSpan.FromSeconds(10);
    private const int MaxUrlBytes = 1024 * 1024;

    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    public async Task<HostsImportResult> ImportTextAsync(
        string text,
        int ttl,
        string? ownershipSourceId = HostsFileImportSourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ttl = NormalizeTtl(ttl);

        var parsed = HostsFileParser.Parse(text);
        var (imported, added) = await AddUniqueRecordsAsync(parsed.Records, ttl);

        if (recordOwnershipStore is not null && ownershipSourceId is not null)
        {
            foreach (var record in added)
                await recordOwnershipStore.AddOwnershipAsync(ownershipSourceId, ToOwned(record));

            await recordOwnershipStore.SaveAsync();
        }

        logger.LogInformation(
            "hosts text import completed: parsed {Parsed}, imported {Imported}, skipped duplicates {Skipped}",
            parsed.Records.Count,
            imported.Imported,
            imported.SkippedDuplicates);

        return new HostsImportResult(
            imported.Imported,
            imported.SkippedDuplicates,
            [.. parsed.Errors, .. imported.Errors]);
    }

    public Task<HostsImportResult> ImportUrlAsync(string url, int ttl)
        => ImportUrlCoreAsync(sourceId: null, url, ttl);

    /// <summary>
    /// URL 来源自动同步入口：拉取远程 hosts 后与来源历史归属做差异合并。
    /// </summary>
    public Task<HostsImportResult> ImportUrlForSourceAsync(string sourceId, string url, int ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return ImportUrlCoreAsync(sourceId, url, ttl);
    }

    /// <summary>
    /// 来源文本差异同步入口，主要用于测试和本地上游文本场景。
    /// </summary>
    public async Task<HostsImportResult> ImportSourceTextAsync(string sourceId, string text, int ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var parsed = HostsFileParser.Parse(text);
        var result = await ReconcileSourceAsync(sourceId, parsed.Records, ttl);
        return new HostsImportResult(result.Imported, result.SkippedDuplicates, [.. parsed.Errors, .. result.Errors]);
    }

    private async Task<HostsImportResult> ImportUrlCoreAsync(string? sourceId, string url, int ttl)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("Please enter a valid URL", nameof(url));

        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only http/https hosts URLs are supported", nameof(url));

        ValidateRemoteUrl(uri, allowLoopback);

        if (httpClientFactory is null)
            throw new InvalidOperationException("HttpClientFactory is not configured; URL import is unavailable");

        using var timeoutCts = new CancellationTokenSource(UrlTimeout);
        var text = await FetchUrlTextAsync(httpClientFactory.CreateClient(), uri, timeoutCts.Token);
        var parsed = HostsFileParser.Parse(text);

        if (sourceId is not null)
            return await ImportSourceTextAsync(sourceId, text, ttl);

        return await ImportTextAsync(text, ttl, HostsUrlImportSourceId);
    }

    private async Task<HostsImportResult> ReconcileSourceAsync(
        string sourceId,
        IReadOnlyList<DnsRecord> parsedRecords,
        int ttl)
    {
        if (recordOwnershipStore is null)
            throw new InvalidOperationException("HostsSourceRecordStore is not configured; source diff sync is unavailable");

        await _reconcileLock.WaitAsync();
        try
        {
            var desired = parsedRecords
                .GroupBy(GetDnsKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First() with { TTL = NormalizeTtl(ttl), Weight = 1 })
                .ToArray();
            var desiredByKey = desired.ToDictionary(GetDnsKey, StringComparer.OrdinalIgnoreCase);
            var previous = await recordOwnershipStore.GetSourceRecordsAsync(sourceId);
            var previousByKey = previous.ToDictionary(GetOwnedKey, StringComparer.OrdinalIgnoreCase);

            var imported = 0;
            var updated = 0;
            var removed = 0;
            var skipped = 0;
            var errors = new List<string>();

            foreach (var old in previous)
            {
                var key = GetOwnedKey(old);
                if (desiredByKey.ContainsKey(key))
                    continue;

                var remainingOwners = await recordOwnershipStore.RemoveOwnershipAsync(sourceId, key);
                if (remainingOwners == 0)
                {
                    try
                    {
                        if (await recordStore.RemoveRecordAsync(old.Domain, old.Type, old.Value))
                            removed++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{old.Domain} {old.Type} {old.Value}: {ex.Message}");
                    }
                }
            }

            foreach (var record in desired)
            {
                var key = GetDnsKey(record);

                if (previousByKey.TryGetValue(key, out var old))
                {
                    if (old.TTL == record.TTL && old.Weight == record.Weight)
                    {
                        try
                        {
                            var exists = recordStore.Query(record.Domain, record.Type)?
                                .Any(existing => string.Equals(existing.Value, record.Value, StringComparison.Ordinal)) == true;

                            if (exists)
                            {
                                skipped++;
                            }
                            else
                            {
                                await recordStore.AddRecordAsync(record);
                                imported++;
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{record.Domain} {record.Type} {record.Value}: {ex.Message}");
                        }

                        continue;
                    }

                    try
                    {
                        await recordStore.AddRecordAsync(record);
                        await recordStore.RemoveRecordAsync(old.Domain, old.Type, old.Value);
                        await recordOwnershipStore.AddOwnershipAsync(sourceId, ToOwned(record));
                        updated++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{record.Domain} {record.Type} {record.Value}: {ex.Message}");
                    }

                    continue;
                }

                try
                {
                    await recordStore.AddRecordAsync(record);
                    await recordOwnershipStore.AddOwnershipAsync(sourceId, ToOwned(record));
                    imported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{record.Domain} {record.Type} {record.Value}: {ex.Message}");
                }
            }

            await recordOwnershipStore.SaveAsync();

            logger.LogInformation(
                "hosts source sync completed: {SourceId}, added {Imported}, updated {Updated}, removed {Removed}, skipped {Skipped}",
                sourceId,
                imported,
                updated,
                removed,
                skipped);

            return new HostsImportResult(imported, skipped, errors);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async Task<(HostsImportResult Result, List<DnsRecord> AddedRecords)> AddUniqueRecordsAsync(
        IReadOnlyList<DnsRecord> parsedRecords,
        int ttl)
    {
        var existing = recordStore.GetAllRecords().ToList();
        HashSet<string> existingKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (var record in existing)
            existingKeys.Add(GetDnsKey(record));

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<DnsRecord> toAdd = [];
        List<string> errors = [];
        var skipped = 0;

        foreach (var source in parsedRecords)
        {
            var key = GetDnsKey(source);
            if (!seen.Add(key))
            {
                skipped++;
                continue;
            }

            if (existingKeys.Contains(key))
            {
                skipped++;
                continue;
            }

            try
            {
                var record = source with { TTL = ttl, Weight = 1 };
                toAdd.Add(record);
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Domain} {source.Type} {source.Value}: {ex.Message}");
            }
        }

        if (toAdd.Count > 0)
            await recordStore.AddRecordsAsync(toAdd);

        return (new HostsImportResult(toAdd.Count, skipped, errors), toAdd);
    }

    private static async Task<string> FetchUrlTextAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxUrlBytes)
            throw new InvalidOperationException("hosts URL content exceeds the 1MB limit");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxUrlBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
                break;

            total += read;
        }

        if (total >= buffer.Length)
        {
            var extra = new char[1];
            if (await reader.ReadAsync(extra.AsMemory(), cancellationToken) > 0)
                throw new InvalidOperationException("hosts URL content exceeds the 1MB limit");
        }

        return new string(buffer, 0, total);
    }

    private static void ValidateRemoteUrl(Uri uri, bool allowLoopback)
    {
        if (allowLoopback)
            return;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("hosts URLs pointing to localhost are not allowed", nameof(uri));

        if (!IPAddress.TryParse(host, out var ip))
            return;

        if (IPAddress.IsLoopback(ip)
            || ip.Equals(IPAddress.Any)
            || ip.Equals(IPAddress.IPv6Any)
            || ip.IsIPv6LinkLocal)
        {
            throw new ArgumentException("hosts URLs pointing to loopback or link-local addresses are not allowed", nameof(uri));
        }

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork
            && bytes.Length == 4
            && bytes[0] == 169
            && bytes[1] == 254)
        {
            throw new ArgumentException("hosts URLs pointing to link-local addresses are not allowed", nameof(uri));
        }
    }

    private static int NormalizeTtl(int ttl)
        => ttl is > 0 and <= int.MaxValue / 2 ? ttl : 3600;

    private static string GetDnsKey(DnsRecord record)
        => $"{record.Domain.TrimEnd('.').ToLowerInvariant()}|{(ushort)record.Type}|{record.Value}";

    private static string GetOwnedKey(HostsOwnedRecord record)
        => $"{record.Domain.TrimEnd('.').ToLowerInvariant()}|{(ushort)record.Type}|{record.Value}";

    private static HostsOwnedRecord ToOwned(DnsRecord record)
        => new(record.Domain, record.Type, record.Value, record.TTL, record.Weight);
}
