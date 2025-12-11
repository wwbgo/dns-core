using DnsCore.Models;
using DnsCore.Repositories;
using System.Collections.Concurrent;

namespace DnsCore.Services;

/// <summary>
/// Custom DNS record store
/// </summary>
public sealed class CustomRecordStore(
    ILogger<CustomRecordStore> logger,
    IDnsRecordRepository? repository = null)
{
    private readonly ConcurrentDictionary<string, List<DnsRecord>> _records = new();
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    /// <summary>
    /// Load records from persistence storage
    /// </summary>
    public async Task LoadFromPersistenceAsync()
    {
        if (repository is null)
        {
            logger.LogDebug("Persistence storage not configured, skipping load");
            return;
        }

        try
        {
            var records = await repository.LoadAllAsync();

            // Group by key and deduplicate based on Domain + Type + Value
            var deduplicated = records
                .GroupBy(r => GetKey(r.Domain, r.Type))
                .ToDictionary(
                    g => g.Key,
                    g => g.DistinctBy(r => new { r.Domain, r.Type, r.Value, r.TTL }).ToList()
                );

            // Clear and reload with deduplicated records
            _records.Clear();
            foreach (var kvp in deduplicated)
            {
                _records[kvp.Key] = kvp.Value;
            }

            var totalCount = _records.Values.Sum(list => list.Count);
            logger.LogInformation("Loaded {Count} records from persistence storage (deduplicated)", totalCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load records from persistence storage");
        }
    }

    /// <summary>
    /// Save to persistence storage
    /// </summary>
    private async Task SaveToPersistenceAsync()
    {
        if (repository is null)
        {
            return;
        }

        await _persistLock.WaitAsync();
        try
        {
            var allRecords = GetAllRecords().ToList();
            await repository.SaveAllAsync(allRecords);
            logger.LogDebug("Saved {Count} records to persistence storage", allRecords.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save records to persistence storage");
        }
        finally
        {
            _persistLock.Release();
        }
    }

    /// <summary>
    /// Add custom record
    /// </summary>
    public async Task AddRecordAsync(DnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var key = GetKey(record.Domain, record.Type);
        var added = false;

        _records.AddOrUpdate(
            key,
            _ =>
            {
                added = true;
                return [record];
            },
            (_, list) =>
            {
                // Check if record already exists (same domain, type, value, and TTL)
                if (!list.Any(r => r.Domain.Equals(record.Domain, StringComparison.OrdinalIgnoreCase)
                    && r.Type == record.Type
                    && r.Value.Equals(record.Value, StringComparison.OrdinalIgnoreCase)
                    && r.TTL == record.TTL))
                {
                    list.Add(record);
                    added = true;
                }
                return list;
            });

        if (added)
        {
            logger.LogInformation("Added custom record: {Record}", record);
            await SaveToPersistenceAsync();
        }
        else
        {
            logger.LogDebug("Record already exists, skipped: {Record}", record);
        }
    }

    /// <summary>
    /// Add custom record (synchronous version for backward compatibility)
    /// </summary>
    public void AddRecord(DnsRecord record)
    {
        AddRecordAsync(record).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Add multiple records
    /// </summary>
    public async Task AddRecordsAsync(IEnumerable<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var addedCount = 0;

        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);

            var key = GetKey(record.Domain, record.Type);
            var added = false;

            _records.AddOrUpdate(
                key,
                _ =>
                {
                    added = true;
                    return [record];
                },
                (_, list) =>
                {
                    // Check if record already exists (same domain, type, value, and TTL)
                    if (!list.Any(r => r.Domain.Equals(record.Domain, StringComparison.OrdinalIgnoreCase)
                        && r.Type == record.Type
                        && r.Value.Equals(record.Value, StringComparison.OrdinalIgnoreCase)
                        && r.TTL == record.TTL))
                    {
                        list.Add(record);
                        added = true;
                    }
                    return list;
                });

            if (added)
            {
                logger.LogInformation("Added custom record: {Record}", record);
                addedCount++;
            }
            else
            {
                logger.LogDebug("Record already exists, skipped: {Record}", record);
            }
        }

        if (addedCount > 0)
        {
            await SaveToPersistenceAsync();
        }
    }

    /// <summary>
    /// Add multiple records (synchronous version for backward compatibility)
    /// </summary>
    public void AddRecords(IEnumerable<DnsRecord> records)
    {
        AddRecordsAsync(records).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Query custom records (supports wildcard)
    /// </summary>
    public List<DnsRecord>? Query(string domain, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        // 1. Exact match
        var key = GetKey(domain, type);
        if (_records.TryGetValue(key, out var records))
        {
            logger.LogDebug("Found custom record (exact match): {Domain} {Type}", domain, type);
            return [..records];
        }

        // 2. Wildcard match (*.example.com)
        var wildcardRecords = FindWildcardMatch(domain, type);
        if (wildcardRecords is not null)
        {
            logger.LogDebug("Found custom record (wildcard match): {Domain} {Type}", domain, type);
            return wildcardRecords;
        }

        // 3. If querying ANY type, return all records for the domain (including wildcards)
        if (type == DnsRecordType.ANY)
        {
            var prefix = $"{domain.ToLowerInvariant()}:";
            var allRecords = _records
                .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(kvp => kvp.Value)
                .ToList();

            if (allRecords.Count > 0)
            {
                logger.LogDebug("Found custom record (ANY): {Domain}", domain);
                return allRecords;
            }
        }

        logger.LogDebug("Custom record not found: {Domain} {Type}", domain, type);
        return null;
    }

    /// <summary>
    /// Find wildcard matching records
    /// Match wildcards from most specific to least specific
    /// Example: api.dev.example.com will match in order:
    /// 1. *.dev.example.com
    /// 2. *.example.com
    /// 3. *.com
    /// </summary>
    private List<DnsRecord>? FindWildcardMatch(string domain, DnsRecordType type)
    {
        var parts = domain.Split('.');

        // Domain must have at least two parts to match wildcard (e.g. example.com)
        if (parts.Length < 2)
        {
            return null;
        }

        // From most specific to least specific wildcard
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var wildcardDomain = "*." + string.Join('.', parts.Skip(i + 1));
            var key = GetKey(wildcardDomain, type);

            if (_records.TryGetValue(key, out var records))
            {
                logger.LogDebug("Wildcard match: {Domain} -> {WildcardDomain}", domain, wildcardDomain);
                return [..records];
            }
        }

        return null;
    }

    /// <summary>
    /// Remove record
    /// </summary>
    public async Task<bool> RemoveRecordAsync(string domain, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var key = GetKey(domain, type);
        var removed = _records.TryRemove(key, out _);

        if (removed)
        {
            logger.LogInformation("Removed custom record: {Domain} {Type}", domain, type);
            await SaveToPersistenceAsync();
        }

        return removed;
    }

    /// <summary>
    /// Remove record (synchronous version for backward compatibility)
    /// </summary>
    public bool RemoveRecord(string domain, DnsRecordType type)
    {
        return RemoveRecordAsync(domain, type).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Clear all records
    /// </summary>
    public async Task ClearAsync()
    {
        _records.Clear();
        logger.LogInformation("Cleared all custom records");
        await SaveToPersistenceAsync();
    }

    /// <summary>
    /// Clear all records (synchronous version for backward compatibility)
    /// </summary>
    public void Clear()
    {
        ClearAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Get all records
    /// </summary>
    public IEnumerable<DnsRecord> GetAllRecords() =>
        _records.Values.SelectMany(records => records);

    private static string GetKey(string domain, DnsRecordType type) =>
        $"{domain.ToLowerInvariant()}:{type}";
}
