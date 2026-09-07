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
    private const int MaxShardCount = 16;
    private static readonly CacheKeyComparer KeyComparer = new();

    private readonly Shard[] _shards;
    private readonly int _shardCount;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxTtl;
    private readonly TimeSpan _minTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly ILogger<DnsCache> _logger;

    private long _sequence;
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

        // 小容量时保持单分片，确保 LRU 语义与旧实现一致；大容量时拆分锁。
        _shardCount = Math.Clamp(_maxEntries, 1, MaxShardCount);
        var initialShardCapacity = Math.Max(1, _maxEntries / _shardCount);
        _shards = Enumerable.Range(0, _shardCount)
            .Select(_ => new Shard(initialShardCapacity))
            .ToArray();
    }

    /// <summary>
    /// 获取缓存结果。返回的记录 TTL 已按剩余存活时间递减：
    /// 原实现返回原始 TTL，客户端会在服务端缓存之上再缓存一整个 TTL 周期。
    /// </summary>
    public DnsCacheResult? Get(string domain, DnsRecordType type, ushort classValue = 1)
    {
        var key = GetCacheKey(domain, type, classValue);
        var now = DateTime.UtcNow;
        var shard = GetShard(key);
        CacheEntry? entry;

        lock (shard.Gate)
        {
            if (!shard.Index.TryGetValue(key, out var node))
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            entry = node.Value;

            if (entry.ExpiresAt <= now)
            {
                shard.Lru.Remove(node);
                shard.Index.Remove(key);
                Interlocked.Increment(ref _misses);
                _logger.LogDebug("缓存过期: {Domain} {Type}", domain, type);
                return null;
            }

            // LRU：命中后移到链表头
            shard.Lru.Remove(node);
            shard.Lru.AddFirst(node);
            entry.Sequence = Interlocked.Increment(ref _sequence);

            Interlocked.Increment(ref _hits);
        }

        var remaining = (int)Math.Max(1, (entry.ExpiresAt - now).TotalSeconds);

        // 返回副本，避免调用方修改污染缓存内容。
        // 锁内只维护 LRU 状态，记录复制放到锁外，缩短缓存读锁持有时间。
        var records = new List<DnsRecord>(entry.Records.Count);
        foreach (var record in entry.Records)
            records.Add(record with { TTL = remaining });

        return new DnsCacheResult
        {
            Records = records,
            ResponseCode = entry.ResponseCode
        };
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
        var key = GetCacheKey(domain, type, classValue);

        Store(key, new CacheEntry
        {
            Key = key,
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

    private void Store(CacheKey key, CacheEntry entry)
    {
        var shard = GetShard(key);

        lock (shard.Gate)
        {
            if (shard.Index.TryGetValue(key, out var existing))
            {
                shard.Lru.Remove(existing);
                shard.Index.Remove(key);
            }

            entry.Sequence = Interlocked.Increment(ref _sequence);
            shard.Index[key] = shard.Lru.AddFirst(entry);
        }

        EvictOverflow();
    }

    private void EvictOverflow()
    {
        var attempts = 0;

        while (CountEntries() > _maxEntries)
        {
            if (TryEvictOldest())
            {
                attempts = 0;
                continue;
            }

            if (++attempts > MaxShardCount * 2)
                break;

            Thread.Yield();
        }
    }

    private int CountEntries()
    {
        var count = 0;

        foreach (var shard in _shards)
        {
            lock (shard.Gate)
                count += shard.Index.Count;
        }

        return count;
    }

    /// <summary>
    /// 找出所有分片中最旧的条目并淘汰。
    /// 扫描与淘汰分别加锁，避免同时持有多个分片锁造成死锁。
    /// </summary>
    private bool TryEvictOldest()
    {
        var bestShard = -1;
        var bestSequence = long.MaxValue;
        CacheEntry? bestEntry = null;

        for (var i = 0; i < _shards.Length; i++)
        {
            lock (_shards[i].Gate)
            {
                var last = _shards[i].Lru.Last;
                if (last is not null && last.Value.Sequence < bestSequence)
                {
                    bestSequence = last.Value.Sequence;
                    bestShard = i;
                    bestEntry = last.Value;
                }
            }
        }

        if (bestShard < 0 || bestEntry is null)
            return false;

        var shard = _shards[bestShard];
        lock (shard.Gate)
        {
            var last = shard.Lru.Last;
            if (last is null || !ReferenceEquals(last.Value, bestEntry))
                return false;

            shard.Lru.RemoveLast();
            shard.Index.Remove(last.Value.Key);
            _logger.LogDebug("淘汰最旧缓存条目: {Domain} {Type}",
                last.Value.Key.Domain, last.Value.Key.Type);
            return true;
        }
    }

    /// <summary>清空缓存</summary>
    public void Clear()
    {
        var count = 0;

        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                count += shard.Index.Count;
                shard.Index.Clear();
                shard.Lru.Clear();
            }
        }

        _logger.LogInformation("缓存已清空，移除 {Count} 条", count);
    }

    /// <summary>缓存统计</summary>
    public DnsCacheStats GetStats()
    {
        var now = DateTime.UtcNow;
        var total = 0;
        var active = 0;
        var negative = 0;

        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                total += shard.Index.Count;

                foreach (var entry in shard.Lru)
                {
                    if (entry.ExpiresAt <= now)
                        continue;

                    active++;
                    if (entry.Records.Count == 0)
                        negative++;
                }
            }
        }

        return new DnsCacheStats
        {
            TotalEntries = total,
            ActiveEntries = active,
            NegativeEntries = negative,
            MaxEntries = _maxEntries,
            Hits = Interlocked.Read(ref _hits),
            Misses = Interlocked.Read(ref _misses)
        };
    }

    /// <summary>清理过期条目</summary>
    public void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        var removed = 0;

        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                var node = shard.Lru.First;
                while (node is not null)
                {
                    var next = node.Next;

                    if (node.Value.ExpiresAt <= now)
                    {
                        shard.Lru.Remove(node);
                        shard.Index.Remove(node.Value.Key);
                        removed++;
                    }

                    node = next;
                }
            }
        }

        if (removed > 0)
            _logger.LogDebug("已清理 {Count} 条过期缓存", removed);
    }

    private Shard GetShard(CacheKey key)
    {
        var hash = (uint)KeyComparer.GetHashCode(key);
        return _shards[(int)(hash % (uint)_shardCount)];
    }

    private static CacheKey GetCacheKey(string domain, DnsRecordType type, ushort classValue)
        => new(domain, type, classValue);

    private sealed class CacheEntry
    {
        public required CacheKey Key { get; init; }
        public required List<DnsRecord> Records { get; init; }
        public required DnsResponseCode ResponseCode { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public long Sequence { get; set; }
    }

    private sealed class Shard(int initialCapacity)
    {
        public Lock Gate { get; } = new();
        public Dictionary<CacheKey, LinkedListNode<CacheEntry>> Index { get; } =
            new(initialCapacity, KeyComparer);
        public LinkedList<CacheEntry> Lru { get; } = new();
    }

    private readonly record struct CacheKey(
        string Domain,
        DnsRecordType Type,
        ushort Class);

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        public bool Equals(CacheKey x, CacheKey y)
            => x.Type == y.Type
               && x.Class == y.Class
               && x.Domain.AsSpan().TrimEnd('.').Equals(
                   y.Domain.AsSpan().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(CacheKey key)
        {
            var hash = new HashCode();
            var domain = key.Domain.AsSpan().TrimEnd('.');

            foreach (var c in domain)
                hash.Add(char.ToLowerInvariant(c));

            hash.Add((ushort)key.Type);
            hash.Add(key.Class);
            return hash.ToHashCode();
        }
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
