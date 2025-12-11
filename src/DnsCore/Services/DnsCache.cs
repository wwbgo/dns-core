using DnsCore.Models;
using System.Collections.Concurrent;

namespace DnsCore.Services;

/// <summary>
/// DNS 查询结果缓存（基于 LRU 策略）
/// </summary>
public sealed class DnsCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;
    private readonly ILogger<DnsCache> _logger;

    public DnsCache(ILogger<DnsCache> logger, int maxEntries = 10000, TimeSpan? defaultTtl = null)
    {
        _logger = logger;
        _maxEntries = maxEntries;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// 获取缓存记录
    /// </summary>
    public List<DnsRecord>? Get(string domain, DnsRecordType type)
    {
        var key = GetCacheKey(domain, type);

        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > DateTime.UtcNow)
            {
                entry.LastAccessTime = DateTime.UtcNow;
                _logger.LogDebug("Cache hit: {Domain} {Type}", domain, type);
                return entry.Records;
            }

            // 过期，移除
            _cache.TryRemove(key, out _);
            _logger.LogDebug("Cache expired: {Domain} {Type}", domain, type);
        }

        _logger.LogDebug("Cache miss: {Domain} {Type}", domain, type);
        return null;
    }

    /// <summary>
    /// 添加缓存记录
    /// </summary>
    public void Set(string domain, DnsRecordType type, List<DnsRecord> records)
    {
        var key = GetCacheKey(domain, type);

        // 使用记录中的最小 TTL 或默认 TTL
        var ttl = records.Count > 0
            ? TimeSpan.FromSeconds(Math.Min(records.Min(r => r.TTL), (int)_defaultTtl.TotalSeconds))
            : _defaultTtl;

        var entry = new CacheEntry
        {
            Records = records,
            CreatedAt = DateTime.UtcNow,
            LastAccessTime = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(ttl)
        };

        // 检查缓存大小限制
        if (_cache.Count >= _maxEntries)
        {
            EvictOldest();
        }

        _cache[key] = entry;
        _logger.LogDebug("Cached: {Domain} {Type}, TTL: {TTL}s", domain, type, (int)ttl.TotalSeconds);
    }

    /// <summary>
    /// 清空缓存
    /// </summary>
    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        _logger.LogInformation("Cache cleared, {Count} entries removed", count);
    }

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    public (int TotalEntries, int ActiveEntries) GetStats()
    {
        var now = DateTime.UtcNow;
        var activeEntries = _cache.Values.Count(e => e.ExpiresAt > now);
        return (_cache.Count, activeEntries);
    }

    /// <summary>
    /// 清理过期条目
    /// </summary>
    public void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _cache
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired cache entries", expiredKeys.Count);
        }
    }

    /// <summary>
    /// 淘汰最旧的条目（LRU 策略）
    /// </summary>
    private void EvictOldest()
    {
        var oldestKey = _cache
            .OrderBy(kvp => kvp.Value.LastAccessTime)
            .FirstOrDefault().Key;

        if (oldestKey != null)
        {
            _cache.TryRemove(oldestKey, out _);
            _logger.LogDebug("Evicted oldest cache entry: {Key}", oldestKey);
        }
    }

    private static string GetCacheKey(string domain, DnsRecordType type)
        => $"{domain.ToLowerInvariant()}:{type}";

    private sealed class CacheEntry
    {
        public required List<DnsRecord> Records { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime LastAccessTime { get; set; }
        public required DateTime ExpiresAt { get; init; }
    }
}
