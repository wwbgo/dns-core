using DnsCore.Services;

namespace DnsCore.Tests.Services;

/// <summary>
/// DNS 查询统计的时间窗口回归测试。
///
/// 这些用例针对早先分层滚动实现的三个真实缺陷：
///   1. 当前小时/当前天的数据不被计入（"最近一天"长期显示 0）；
///   2. 层间聚合取"最近 N 个槽之和"而非"刚过去的那一段"，导致重复计数；
///   3. 长时间空闲后一次请求把历史整片清空。
/// </summary>
public class DnsQueryStatisticsTests
{
    /// <summary>可控时钟，让时间窗口可被精确驱动。</summary>
    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public Func<DateTimeOffset> Func => () => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static (DnsQueryStatistics Stats, FakeClock Clock) Create()
    {
        var clock = new FakeClock();
        return (new DnsQueryStatistics(clock.Func), clock);
    }

    [Fact]
    public void PerDay_ShouldCountCurrentHour_NotOnlyCompletedHours()
    {
        // 早先实现的核心 bug：小时桶只在跨小时时填充，
        // 于是服务启动后的第一个小时内"最近一天"恒为 0。
        var (stats, _) = Create();

        for (var i = 0; i < 7; i++)
            stats.RecordQuery();

        var snapshot = stats.GetStats();

        Assert.Equal(7, snapshot.PerDay);
        Assert.Equal(7, snapshot.TotalQueries);
    }

    [Fact]
    public void AllWindows_ShouldReflectQueriesImmediately()
    {
        var (stats, _) = Create();

        stats.RecordQuery();
        stats.RecordQuery();

        var s = stats.GetStats();

        Assert.Equal(2, s.PerSecond);
        Assert.Equal(2, s.PerMinute);
        Assert.Equal(2, s.PerHour);
        Assert.Equal(2, s.PerDay);
    }

    [Fact]
    public void PerSecond_ShouldOnlyCountCurrentSecond()
    {
        var (stats, clock) = Create();

        stats.RecordQuery();
        clock.Advance(TimeSpan.FromSeconds(1));
        stats.RecordQuery();
        stats.RecordQuery();

        var s = stats.GetStats();

        // 当前秒有 2 次，上一秒的 1 次已滑出 1 秒窗口
        Assert.Equal(2, s.PerSecond);
        // 但仍在分钟窗口内
        Assert.Equal(3, s.PerMinute);
    }

    [Fact]
    public void Windows_ShouldExpireOnceQueriesFallOutside()
    {
        var (stats, clock) = Create();

        stats.RecordQuery();

        // 越过 1 分钟窗口，但仍在 1 小时内
        clock.Advance(TimeSpan.FromSeconds(61));

        var s = stats.GetStats();

        Assert.Equal(0, s.PerSecond);
        Assert.Equal(0, s.PerMinute);
        Assert.Equal(1, s.PerHour);
        Assert.Equal(1, s.PerDay);
        // 累计值不受窗口影响
        Assert.Equal(1, s.TotalQueries);
    }

    [Fact]
    public void PerHour_ShouldExpireAfterOneHour_ButPerDayShouldNot()
    {
        var (stats, clock) = Create();

        stats.RecordQuery();
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        var s = stats.GetStats();

        Assert.Equal(0, s.PerHour);
        Assert.Equal(1, s.PerDay);
    }

    [Fact]
    public void PerDay_ShouldExpireAfterOneDay()
    {
        var (stats, clock) = Create();

        stats.RecordQuery();
        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        var s = stats.GetStats();

        Assert.Equal(0, s.PerDay);
        // 累计值永久保留
        Assert.Equal(1, s.TotalQueries);
    }

    [Fact]
    public void IdleGap_ShouldNotWipeHistory()
    {
        // 早先实现里，滚动只由请求触发；空闲超过 60 秒后再来一次请求
        // 会走 Array.Clear 分支，把仍在窗口内的历史一并清空。
        var (stats, clock) = Create();

        for (var i = 0; i < 5; i++)
            stats.RecordQuery();

        // 空闲远超秒级数组长度
        clock.Advance(TimeSpan.FromMinutes(30));
        stats.RecordQuery();

        var s = stats.GetStats();

        // 30 分钟前的 5 次仍在小时窗口内，不该被抹掉
        Assert.Equal(6, s.PerHour);
        Assert.Equal(6, s.PerDay);
        Assert.Equal(6, s.TotalQueries);
    }

    [Fact]
    public void Counts_ShouldNotBeDoubleCounted_AcrossMinuteBoundary()
    {
        // 早先实现按"最近 60 槽之和"向上聚合，跨分钟时会把同一批请求
        // 重复计入分钟桶。这里逐分钟各记 1 次，总和必须精确等于分钟数。
        var (stats, clock) = Create();

        const int minutes = 10;
        for (var i = 0; i < minutes; i++)
        {
            stats.RecordQuery();
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var s = stats.GetStats();

        Assert.Equal(minutes, s.PerHour);
        Assert.Equal(minutes, s.PerDay);
        Assert.Equal(minutes, s.TotalQueries);
    }

    [Fact]
    public void RecentSeconds_ShouldBeChronological_WithCurrentSecondLast()
    {
        var (stats, clock) = Create();

        // 第一秒 1 次
        stats.RecordQuery();
        clock.Advance(TimeSpan.FromSeconds(1));
        // 第二秒 3 次
        stats.RecordQuery();
        stats.RecordQuery();
        stats.RecordQuery();

        var series = stats.GetStats().RecentSeconds;

        Assert.Equal(60, series.Length);
        // 末位是当前秒
        Assert.Equal(3, series[^1]);
        Assert.Equal(1, series[^2]);
        // 更早的槽应为空
        Assert.Equal(0, series[^3]);
    }

    [Fact]
    public void RecentSeconds_ShouldSumToPerMinute()
    {
        var (stats, clock) = Create();

        for (var i = 0; i < 5; i++)
        {
            stats.RecordQuery();
            stats.RecordQuery();
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var s = stats.GetStats();

        Assert.Equal(s.PerMinute, s.RecentSeconds.Sum());
    }

    [Fact]
    public void EmptyStatistics_ShouldReportZeros()
    {
        var (stats, _) = Create();

        var s = stats.GetStats();

        Assert.Equal(0, s.TotalQueries);
        Assert.Equal(0, s.PerSecond);
        Assert.Equal(0, s.PerMinute);
        Assert.Equal(0, s.PerHour);
        Assert.Equal(0, s.PerDay);
        Assert.Equal(60, s.RecentSeconds.Length);
        Assert.All(s.RecentSeconds, v => Assert.Equal(0, v));
    }

    [Fact]
    public void SlotReuse_ShouldNotLeakStaleCounts()
    {
        // 秒级数组长 3600；正好前进 3600 秒会落回同一个槽，
        // 若不校验槽内时刻，会读到一小时前的陈旧计数。
        var (stats, clock) = Create();

        stats.RecordQuery();
        stats.RecordQuery();

        clock.Advance(TimeSpan.FromSeconds(3600));

        var s = stats.GetStats();

        // 同一槽位，但时刻已不同，必须视为 0
        Assert.Equal(0, s.PerSecond);
        Assert.Equal(0, s.PerMinute);
    }

    [Fact]
    public void UptimeSeconds_ShouldTrackClock()
    {
        var (stats, clock) = Create();

        clock.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(90, stats.GetStats().UptimeSeconds, precision: 3);
    }

    [Fact]
    public void ConcurrentRecording_ShouldNotLoseCounts()
    {
        var stats = new DnsQueryStatistics();
        const int threads = 8;
        const int perThread = 500;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                stats.RecordQuery();
        });

        Assert.Equal(threads * perThread, stats.GetStats().TotalQueries);
    }
}
