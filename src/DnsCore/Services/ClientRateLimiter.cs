using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace DnsCore.Services;

/// <summary>
/// 按客户端 IP 的令牌桶限流。
/// 原实现完全没有限流：单个客户端可以无上限地驱动上游查询与 Task 创建。
/// </summary>
public sealed class ClientRateLimiter(int maxQueriesPerSecond)
{
    private static readonly double TickFrequency = Stopwatch.Frequency;
    private static readonly long SweepIntervalTicks = (long)(TickFrequency * TimeSpan.FromMinutes(5).TotalSeconds);
    private static readonly long IdleExpiryTicks = (long)(TickFrequency * TimeSpan.FromMinutes(10).TotalSeconds);

    private readonly ConcurrentDictionary<IPAddress, Bucket> _buckets = new();
    private readonly int _capacity = Math.Max(1, maxQueriesPerSecond);
    private long _lastSweepTicks = Stopwatch.GetTimestamp();

    public bool Enabled { get; } = maxQueriesPerSecond > 0;

    /// <summary>尝试为该客户端取一个令牌；返回 false 表示应丢弃该查询</summary>
    public bool TryAcquire(IPAddress? client)
    {
        if (!Enabled || client is null)
            return true;

        var now = Stopwatch.GetTimestamp();
        SweepIfNeeded(now);

        var bucket = _buckets.GetOrAdd(client, _ => new Bucket(_capacity, now));

        lock (bucket)
        {
            // 按经过的时间线性补充令牌
            var elapsedSeconds = (now - bucket.LastRefillTicks) / TickFrequency;
            if (elapsedSeconds > 0)
            {
                bucket.Tokens = Math.Min(_capacity, bucket.Tokens + elapsedSeconds * _capacity);
                bucket.LastRefillTicks = now;
            }

            if (bucket.Tokens < 1)
                return false;

            bucket.Tokens -= 1;
            return true;
        }
    }

    /// <summary>定期清理空闲客户端，避免桶字典无界增长（本身也是内存耗尽面）</summary>
    private void SweepIfNeeded(long now)
    {
        var lastSweep = Interlocked.Read(ref _lastSweepTicks);
        if (now - lastSweep < SweepIntervalTicks)
            return;

        Interlocked.Exchange(ref _lastSweepTicks, now);

        foreach (var (key, bucket) in _buckets)
        {
            if (now - bucket.LastRefillTicks > IdleExpiryTicks)
                _buckets.TryRemove(key, out _);
        }
    }

    private sealed class Bucket(double tokens, long lastRefillTicks)
    {
        public double Tokens { get; set; } = tokens;
        public long LastRefillTicks { get; set; } = lastRefillTicks;
    }
}
