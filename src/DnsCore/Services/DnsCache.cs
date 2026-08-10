using DnsCore.Configuration;
using DnsCore.Models;

namespace DnsCore.Services;

/// <summary>
/// 缓存查询结果：命中的记录集，或已知的否定应答（NXDOMAIN / NODATA）
/// </summary>
public sealed record DnsCacheResult
{
    public required List<DnsRecord> Records { get; init; }
    public DnsResponseCode ResponseCode { get; init; } = DnsResponseCode.NoError;
    public bool IsNegative => Records.Count == 0;
}

/// <summary>
/// DNS 查询缓存。真正的 O(1) LRU（双向链表 + 字典）。
/// 原实现每次淘汰都对整个字典做 OrderBy 全量排序（O(n log n)），
/// 满载后几乎每次插入都要排一遍 10000 条。
/// </summary>
public sealed class DnsCache
{
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index;
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Lock _gate = new();

    private readonly int _maxEntries;
    private readonly TimeSpan _maxTtl;
    private readonly TimeSpan _minTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly ILogger<DnsCache> _logger;

    private long _hits;
    private long _misses;

    public DnsCache(ILogger<DnsCache> logger, CacheOptions? options = null)
    {
        _logger = logger;
        options ??= new CacheOptions();

        _maxEntries = Math.Max(1, options.MaxEntries);
        _maxTtl = TimeSpan.FromSeconds(Math.Max(1, options.MaxTtlSeconds));
        _minTtl = TimeSpan.FromSeconds(Math.Max(0, options.MinTtlSeconds));
        _negativeTtl = TimeSpan.FromSeconds(Math.Max(0, options.NegativeTtlSeconds));
        _index = new Dictionary<string, LinkedListNode<CacheEntry>>(
            Math.Min(_maxEntries, 1024), StringComparer.Ordinal);
    }

    /// <summary>
    /// 获取缓存结果。返回的记录 TTL 已按剩余存活时间递减：
    /// 原实现返回原始 TTL，客户端会在服务端缓存之上再缓存一整个 TTL 周期。
    /// </summary>
    public DnsCacheResult? Get(string domain, DnsRecordType type, ushort classValue = 1)
    {
        var key = GetCacheKey(domain, type, classValue);
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var node))
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            var entry = node.Value;

            if (entry.ExpiresAt <= now)
            {
                _lru.Remove(node);
                _index.Remove(key);
                Interlocked.Increment(ref _misses);
                _logger.LogDebug("缓存过期: {Domain} {Type}", domain, type);
                return null;
            }

            // LRU：命中后移到链表头
            _lru.Remove(node);
            _lru.AddFirst(node);

            Interlocked.Increment(ref _hits);

            var remaining = (int)Math.Max(1, (entry.ExpiresAt - now).TotalSeconds);

            // 返回副本，避免调用方修改污染缓存内容
            return new DnsCacheResult
            {
                Records = [.. entry.Records.Select(r => r with { TTL = remaining })],
                ResponseCode = entry.ResponseCode
            };
        }
    }

    /// <summary>写入正向缓存</summary>
    public void Set(string domain, DnsRecordType type, List<DnsRecord> records, ushort classValue = 1)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            SetNegative(domain, type, DnsResponseCode.NoError, classValue);
            return;
        }

        // 取记录中最小 TTL，再夹到 [minTtl, maxTtl]。
        // 原实现用 Math.Min(minTTL, defaultTtl) 且未设下限，
        // 上游返回 TTL<=0 时会算出负 TimeSpan，条目写入即过期。
        var smallest = records.Min(r => r.TTL);
        var ttl = TimeSpan.FromSeconds(Math.Clamp(smallest, _minTtl.TotalSeconds, _maxTtl.TotalSeconds));

        Store(GetCacheKey(domain, type, classValue), new CacheEntry
        {
            Key = GetCacheKey(domain, type, classValue),
            Records = [.. records],
            ResponseCode = DnsResponseCode.NoError,
            ExpiresAt = DateTime.UtcNow.Add(ttl)
        });

        _logger.LogDebug("已缓存: {Domain} {Type}, TTL: {TTL}s", domain, type, (int)ttl.TotalSeconds);
    }

    /// <summary>
    /// 写入否定缓存（NXDOMAIN / NODATA）。
    /// 原实现完全不缓存否定结果，对不存在域名的重复查询每次都打上游，是典型放大面。
    /// </summary>
    public void SetNegative(string domain, DnsRecordType type, DnsResponseCode code, ushort classValue = 1)
    {
        if (_negativeTtl <= TimeSpan.Zero)
            return;

        var key = GetCacheKey(domain, type, classValue);

        Store(key, new CacheEntry
        {
            Key = key,
            Records = [],
            ResponseCode = code,
            ExpiresAt = DateTime.UtcNow.Add(_negativeTtl)
        });

        _logger.LogDebug("已缓存否定应答: {Domain} {Type} {Code}, TTL: {TTL}s",
            domain, type, code, (int)_negativeTtl.TotalSeconds);
    }

    private void Store(string key, CacheEntry entry)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _index.Remove(key);
            }

            // O(1) 淘汰：直接摘链表尾
            while (_index.Count >= _maxEntries && _lru.Last is not null)
            {
                var oldest = _lru.Last;
                _lru.RemoveLast();
                _index.Remove(oldest.Value.Key);
                _logger.LogDebug("淘汰最旧缓存条目: {Key}", oldest.Value.Key);
            }

            _index[key] = _lru.AddFirst(entry);
        }
    }

    /// <summary>清空缓存</summary>
    public void Clear()
    {
        lock (_gate)
        {
            var count = _index.Count;
            _index.Clear();
            _lru.Clear();
            _logger.LogInformation("缓存已清空，移除 {Count} 条", count);
        }
    }

    /// <summary>缓存统计</summary>
    public DnsCacheStats GetStats()
    {
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            var active = 0;
            var negative = 0;

            foreach (var entry in _lru)
            {
                if (entry.ExpiresAt <= now)
                    continue;

                active++;
                if (entry.Records.Count == 0)
                    negative++;
            }

            return new DnsCacheStats
            {
                TotalEntries = _index.Count,
                ActiveEntries = active,
                NegativeEntries = negative,
                MaxEntries = _maxEntries,
                Hits = Interlocked.Read(ref _hits),
                Misses = Interlocked.Read(ref _misses)
            };
        }
    }

    /// <summary>清理过期条目</summary>
    public void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        var removed = 0;

        lock (_gate)
        {
            var node = _lru.First;
            while (node is not null)
            {
                var next = node.Next;

                if (node.Value.ExpiresAt <= now)
                {
                    _lru.Remove(node);
                    _index.Remove(node.Value.Key);
                    removed++;
                }

                node = next;
            }
        }

        if (removed > 0)
            _logger.LogDebug("已清理 {Count} 条过期缓存", removed);
    }

    private static string GetCacheKey(string domain, DnsRecordType type, ushort classValue)
        => $"{domain.TrimEnd('.').ToLowerInvariant()}:{(ushort)type}:{classValue}";

    private sealed class CacheEntry
    {
        public required string Key { get; init; }
        public required List<DnsRecord> Records { get; init; }
        public required DnsResponseCode ResponseCode { get; init; }
        public required DateTime ExpiresAt { get; init; }
    }
}

/// <summary>缓存统计信息</summary>
public sealed record DnsCacheStats
{
    public required int TotalEntries { get; init; }
    public required int ActiveEntries { get; init; }
    public required int NegativeEntries { get; init; }
    public required int MaxEntries { get; init; }
    public required long Hits { get; init; }
    public required long Misses { get; init; }

    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}
