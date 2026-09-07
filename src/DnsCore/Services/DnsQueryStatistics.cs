namespace DnsCore.Services;

/// <summary>
/// DNS 查询量统计。
///
/// 设计要点：槽位索引由绝对时间戳决定，不维护"当前指针 + 上次滚动时间"。
/// 早先的分层滚动实现有三个叠加缺陷，这里逐一避免：
///   1. 上层桶只在跨界时填充，导致当前小时/当前天的数据永远不被计入；
///   2. 层间聚合取的是"最近 60 个槽之和"而非"刚过去的那一分钟"，
///      滚动间隔不精确时会重复计或漏计；
///   3. 滚动只由请求触发，长时间空闲后再来一次请求会把历史整片清空。
///
/// 用绝对时间戳后，每个槽自带它所代表的时刻；读取时槽内时刻与目标时刻
/// 不符即视为 0。既不需要滚动，空闲也不会丢数据。
/// </summary>
public sealed class DnsQueryStatistics
{
    // 秒级槽覆盖 1 小时，足以回答"最近 1 秒 / 1 分钟 / 1 小时"
    private const int SecondSlots = 3600;
    // 分钟级槽覆盖 1 天。不用秒级槽回答"最近一天"：那要遍历 86400 个槽，
    // 且需要 1MB 数组；按分钟聚合后只需 1440 次迭代。
    private const int MinuteSlots = 1440;
    private const int ShardCount = 4;

    private readonly Shard[] _shards;
    private readonly Func<DateTimeOffset> _clock;
    private readonly DateTimeOffset _startedAt;

    /// <param name="clock">时间源，测试可注入以驱动时间窗口。</param>
    public DnsQueryStatistics(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _startedAt = _clock();

        _shards = Enumerable.Range(0, ShardCount).Select(_ => new Shard()).ToArray();
    }

    /// <summary>记录一次 DNS 查询。</summary>
    public void RecordQuery()
    {
        var second = _clock().ToUnixTimeSeconds();
        var minute = FloorDiv(second, 60);
        var shard = GetShard();

        lock (shard.Gate)
        {
            shard.TotalQueries++;
            Bump(shard.SecondCounts, shard.SecondStamps, SecondSlots, second);
            Bump(shard.MinuteCounts, shard.MinuteStamps, MinuteSlots, minute);
        }
    }

    /// <summary>获取统计快照。</summary>
    public DnsQueryStats GetStats()
    {
        var now = _clock();
        var second = now.ToUnixTimeSeconds();
        var minute = FloorDiv(second, 60);

        var totalQueries = 0L;
        var perSecond = 0;
        var perMinute = 0;
        var perHour = 0;
        var perDay = 0;
        var recentSeconds = new int[60];

        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                totalQueries += shard.TotalQueries;
                perSecond += SumSlots(shard.SecondCounts, shard.SecondStamps, SecondSlots, second, 1);
                perMinute += SumSlots(shard.SecondCounts, shard.SecondStamps, SecondSlots, second, 60);
                perHour += SumSlots(shard.SecondCounts, shard.SecondStamps, SecondSlots, second, SecondSlots);
                perDay += SumSlots(shard.MinuteCounts, shard.MinuteStamps, MinuteSlots, minute, MinuteSlots);

                var series = TailSeries(shard.SecondCounts, shard.SecondStamps, SecondSlots, second, 60);
                for (var i = 0; i < recentSeconds.Length; i++)
                    recentSeconds[i] += series[i];
            }
        }

        return new DnsQueryStats
        {
            TotalQueries = totalQueries,
            PerSecond = perSecond,
            PerMinute = perMinute,
            PerHour = perHour,
            PerDay = perDay,
            // 趋势图数据：按时间升序，最后一项为当前秒
            RecentSeconds = recentSeconds,
            UptimeSeconds = (now - _startedAt).TotalSeconds
        };
    }

    private Shard GetShard()
        => _shards[Environment.CurrentManagedThreadId & (ShardCount - 1)];

    /// <summary>给 tick 对应的槽加一；槽内时刻过期则先归零再计。</summary>
    private static void Bump(int[] counts, long[] stamps, int slots, long tick)
    {
        var i = SlotOf(tick, slots);

        if (stamps[i] != tick)
        {
            stamps[i] = tick;
            counts[i] = 0;
        }

        counts[i]++;
    }

    /// <summary>累加以 endTick 结尾、长度为 window 个 tick 的窗口。</summary>
    private static int SumSlots(int[] counts, long[] stamps, int slots, long endTick, int window)
    {
        var total = 0;
        var start = endTick - window + 1;

        for (var t = start; t <= endTick; t++)
        {
            var i = SlotOf(t, slots);
            // 槽内时刻不符说明该 tick 无数据（或已被后续轮次覆盖）
            if (stamps[i] == t)
                total += counts[i];
        }

        return total;
    }

    /// <summary>导出窗口内每个 tick 的计数，按时间升序。</summary>
    private static int[] TailSeries(int[] counts, long[] stamps, int slots, long endTick, int window)
    {
        var series = new int[window];
        var start = endTick - window + 1;

        for (var k = 0; k < window; k++)
        {
            var t = start + k;
            var i = SlotOf(t, slots);
            series[k] = stamps[i] == t ? counts[i] : 0;
        }

        return series;
    }

    /// <summary>取模到槽位。负 tick（1970 年前的注入时钟）也要落在合法下标上。</summary>
    private static int SlotOf(long tick, int slots)
    {
        var m = (int)(tick % slots);
        return m < 0 ? m + slots : m;
    }

    /// <summary>向下取整除法。C# 的 / 对负数向零取整，会让纪元前的时刻归错桶。</summary>
    private static long FloorDiv(long value, long divisor)
    {
        var q = value / divisor;
        if (value % divisor != 0 && ((value < 0) != (divisor < 0)))
            q--;
        return q;
    }

    private sealed class Shard
    {
        public object Gate { get; } = new();
        public int[] SecondCounts { get; } = new int[SecondSlots];
        public long[] SecondStamps { get; } = new long[SecondSlots];
        public int[] MinuteCounts { get; } = new int[MinuteSlots];
        public long[] MinuteStamps { get; } = new long[MinuteSlots];
        public long TotalQueries { get; set; }

        public Shard()
        {
            // 槽位初值 0 会与"Unix 纪元第 0 秒"混淆，用 -1 表示空槽
            Array.Fill(SecondStamps, -1L);
            Array.Fill(MinuteStamps, -1L);
        }
    }
}

/// <summary>DNS 查询统计快照。</summary>
public sealed record DnsQueryStats
{
    /// <summary>自启动以来的累计查询数。</summary>
    public long TotalQueries { get; init; }

    /// <summary>最近 1 秒的查询数。</summary>
    public int PerSecond { get; init; }

    /// <summary>最近 60 秒的查询数。</summary>
    public int PerMinute { get; init; }

    /// <summary>最近 1 小时的查询数。</summary>
    public int PerHour { get; init; }

    /// <summary>最近 24 小时的查询数。</summary>
    public int PerDay { get; init; }

    /// <summary>最近 60 秒逐秒的查询数，按时间升序。</summary>
    public int[] RecentSeconds { get; init; } = [];

    /// <summary>运行时长（秒）。</summary>
    public double UptimeSeconds { get; init; }
}
