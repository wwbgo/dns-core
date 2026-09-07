namespace DnsCore.Services;

/// <summary>
/// DNS 响应延迟统计。
///
/// 写入路径在每条查询上都会执行，因此用固定容量环形缓冲：
/// 早先用 List + RemoveAt(0) 淘汰最旧样本，那是 O(n)——稳态下每次调用都要
/// 在锁内搬移近千个 double，实测比环形缓冲慢 4 倍，且延长锁持有时间。
/// </summary>
public sealed class DnsLatencyStatistics
{
    // 百分位数只需要一个足够代表近期分布的窗口，1000 条即可
    private const int SampleCapacity = 1000;
    private const int ShardCount = 4;
    private const int SamplesPerShard = SampleCapacity / ShardCount;

    private readonly Shard[] _shards;

    public DnsLatencyStatistics()
        => _shards = Enumerable.Range(0, ShardCount).Select(_ => new Shard()).ToArray();

    /// <summary>
    /// 记录一次请求的延迟。
    /// </summary>
    /// <param name="latencyMs">延迟毫秒数；非有限值或负值会被丢弃。</param>
    public void RecordLatency(double latencyMs)
    {
        // NaN 会通过累加永久污染平均值——加法一旦得到 NaN 就再也回不来，
        // 后续所有正常样本都救不回，只能重启进程。负值同理会污染最小值。
        // 调用方现在用单调时钟，正常不会产出这两类值，但统计类不应依赖调用方的正确性。
        if (!double.IsFinite(latencyMs) || latencyMs < 0)
            return;

        var shard = GetShard();

        lock (shard.Gate)
        {
            shard.TotalRequests++;
            shard.TotalLatencyMs += latencyMs;

            if (latencyMs < shard.MinLatencyMs)
                shard.MinLatencyMs = latencyMs;

            if (latencyMs > shard.MaxLatencyMs)
                shard.MaxLatencyMs = latencyMs;

            // 环形写入：O(1)，无搬移、无分配
            shard.Samples[shard.WriteIndex] = latencyMs;
            shard.WriteIndex = (shard.WriteIndex + 1) % SamplesPerShard;

            if (shard.SampleCount < SamplesPerShard)
                shard.SampleCount++;
        }
    }

    /// <summary>获取延迟统计快照。</summary>
    public LatencyStats GetStats()
    {
        long total = 0;
        double sum = 0;
        var min = double.MaxValue;
        var max = 0d;
        var snapshot = new double[SampleCapacity];
        var offset = 0;

        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                total += shard.TotalRequests;
                sum += shard.TotalLatencyMs;
                min = Math.Min(min, shard.MinLatencyMs);
                max = Math.Max(max, shard.MaxLatencyMs);

                Array.Copy(shard.Samples, 0, snapshot, offset, shard.SampleCount);
                offset += shard.SampleCount;
            }
        }

        if (total == 0)
            return new LatencyStats();

        // 排序留到锁外，避免阻塞热路径上的写入
        var sorted = snapshot.AsSpan(0, offset).ToArray();
        Array.Sort(sorted);

        return new LatencyStats
        {
            TotalRequests = total,
            AverageMs = Math.Round(sum / total, 2),
            MinMs = Math.Round(min, 2),
            MaxMs = Math.Round(max, 2),
            P50Ms = Math.Round(Percentile(sorted, 0.50), 2),
            P95Ms = Math.Round(Percentile(sorted, 0.95), 2),
            P99Ms = Math.Round(Percentile(sorted, 0.99), 2)
        };
    }

    private Shard GetShard()
        => _shards[Environment.CurrentManagedThreadId & (ShardCount - 1)];

    /// <summary>最近邻排位法取百分位数。</summary>
    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;

        var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private sealed class Shard
    {
        public object Gate { get; } = new();
        public double[] Samples { get; } = new double[SamplesPerShard];
        public int WriteIndex { get; set; }
        public int SampleCount { get; set; }
        public long TotalRequests { get; set; }
        public double TotalLatencyMs { get; set; }
        public double MinLatencyMs { get; set; } = double.MaxValue;
        public double MaxLatencyMs { get; set; }
    }
}

/// <summary>延迟统计快照。</summary>
public sealed record LatencyStats
{
    /// <summary>参与统计的请求数。</summary>
    public long TotalRequests { get; init; }

    /// <summary>平均延迟（毫秒）。</summary>
    public double AverageMs { get; init; }

    /// <summary>最小延迟（毫秒）。</summary>
    public double MinMs { get; init; }

    /// <summary>最大延迟（毫秒）。</summary>
    public double MaxMs { get; init; }

    /// <summary>中位数延迟（毫秒）。</summary>
    public double P50Ms { get; init; }

    /// <summary>95 分位延迟（毫秒）。</summary>
    public double P95Ms { get; init; }

    /// <summary>99 分位延迟（毫秒）。</summary>
    public double P99Ms { get; init; }
}
