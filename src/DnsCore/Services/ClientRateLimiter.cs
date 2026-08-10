using System.Collections.Concurrent;
using System.Net;

namespace DnsCore.Services;

/// <summary>
/// 按客户端 IP 的令牌桶限流。
/// 原实现完全没有限流：单个客户端可以无上限地驱动上游查询与 Task 创建。
/// </summary>
public sealed class ClientRateLimiter(int maxQueriesPerSecond)
{
    private readonly ConcurrentDictionary<IPAddress, Bucket> _buckets = new();
    private readonly int _capacity = Math.Max(1, maxQueriesPerSecond);
    private DateTime _lastSweep = DateTime.UtcNow;

    public bool Enabled { get; } = maxQueriesPerSecond > 0;

    /// <summary>尝试为该客户端取一个令牌；返回 false 表示应丢弃该查询</summary>
    public bool TryAcquire(IPAddress? client)
    {
        if (!Enabled || client is null)
            return true;

        var now = DateTime.UtcNow;
        SweepIfNeeded(now);

        var bucket = _buckets.GetOrAdd(client, _ => new Bucket(_capacity, now));

        lock (bucket)
        {
            // 按经过的时间线性补充令牌
            var elapsed = (now - bucket.LastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                bucket.Tokens = Math.Min(_capacity, bucket.Tokens + elapsed * _capacity);
                bucket.LastRefill = now;
            }

            if (bucket.Tokens < 1)
                return false;

            bucket.Tokens -= 1;
            return true;
        }
    }

    /// <summary>定期清理空闲客户端，避免桶字典无界增长（本身也是内存耗尽面）</summary>
    private void SweepIfNeeded(DateTime now)
    {
        if (now - _lastSweep < TimeSpan.FromMinutes(5))
            return;

        _lastSweep = now;

        foreach (var (key, bucket) in _buckets)
        {
            if (now - bucket.LastRefill > TimeSpan.FromMinutes(10))
                _buckets.TryRemove(key, out _);
        }
    }

    private sealed class Bucket(double tokens, DateTime lastRefill)
    {
        public double Tokens { get; set; } = tokens;
        public DateTime LastRefill { get; set; } = lastRefill;
    }
}
