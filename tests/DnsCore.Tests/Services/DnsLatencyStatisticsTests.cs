using DnsCore.Services;

namespace DnsCore.Tests.Services;

/// <summary>
/// DNS 延迟统计回归测试。
///
/// 覆盖两个真实缺陷：
///   1. 单个 NaN 样本会通过累加永久污染平均值，后续正常样本无法恢复；
///   2. 负延迟（墙上时钟被 NTP 回拨）会成为最小值。
/// 以及环形缓冲的淘汰行为与百分位数计算。
/// </summary>
public class DnsLatencyStatisticsTests
{
    [Fact]
    public void EmptyStatistics_ShouldReportZeros()
    {
        var stats = new DnsLatencyStatistics().GetStats();

        Assert.Equal(0, stats.TotalRequests);
        Assert.Equal(0, stats.AverageMs);
        Assert.Equal(0, stats.MinMs);
        Assert.Equal(0, stats.MaxMs);
        Assert.Equal(0, stats.P50Ms);
    }

    [Fact]
    public void BasicAggregates_ShouldBeComputedCorrectly()
    {
        var stats = new DnsLatencyStatistics();

        stats.RecordLatency(10);
        stats.RecordLatency(20);
        stats.RecordLatency(30);

        var s = stats.GetStats();

        Assert.Equal(3, s.TotalRequests);
        Assert.Equal(20, s.AverageMs);
        Assert.Equal(10, s.MinMs);
        Assert.Equal(30, s.MaxMs);
    }

    [Fact]
    public void NaN_ShouldBeRejected_NotPoisonAverage()
    {
        var stats = new DnsLatencyStatistics();

        stats.RecordLatency(10);
        stats.RecordLatency(double.NaN);
        stats.RecordLatency(20);

        var s = stats.GetStats();

        // NaN 被丢弃，不计入请求数，也不污染平均值
        Assert.Equal(2, s.TotalRequests);
        Assert.False(double.IsNaN(s.AverageMs));
        Assert.Equal(15, s.AverageMs);
    }

    [Fact]
    public void Infinity_ShouldBeRejected()
    {
        var stats = new DnsLatencyStatistics();

        stats.RecordLatency(10);
        stats.RecordLatency(double.PositiveInfinity);
        stats.RecordLatency(double.NegativeInfinity);

        var s = stats.GetStats();

        Assert.Equal(1, s.TotalRequests);
        Assert.Equal(10, s.MaxMs);
    }

    [Fact]
    public void NegativeLatency_ShouldBeRejected_NotBecomeMinimum()
    {
        var stats = new DnsLatencyStatistics();

        stats.RecordLatency(10);
        // 墙上时钟被回拨时可能算出负延迟
        stats.RecordLatency(-5);

        var s = stats.GetStats();

        Assert.Equal(1, s.TotalRequests);
        Assert.Equal(10, s.MinMs);
        Assert.True(s.MinMs >= 0, "最小延迟不应为负");
    }

    [Fact]
    public void ZeroLatency_ShouldBeAccepted()
    {
        // 缓存命中可能快到测不出耗时，0 是合法值，不能与负值一起被丢
        var stats = new DnsLatencyStatistics();

        stats.RecordLatency(0);

        var s = stats.GetStats();

        Assert.Equal(1, s.TotalRequests);
        Assert.Equal(0, s.MinMs);
    }

    [Fact]
    public void Percentiles_ShouldReflectDistribution()
    {
        var stats = new DnsLatencyStatistics();

        // 1..100 均匀分布
        for (var i = 1; i <= 100; i++)
            stats.RecordLatency(i);

        var s = stats.GetStats();

        Assert.Equal(50, s.P50Ms);
        Assert.Equal(95, s.P95Ms);
        Assert.Equal(99, s.P99Ms);
    }

    [Fact]
    public void Percentiles_ShouldTolerateSingleSample()
    {
        var stats = new DnsLatencyStatistics();

        stats.RecordLatency(42);

        var s = stats.GetStats();

        Assert.Equal(42, s.P50Ms);
        Assert.Equal(42, s.P95Ms);
        Assert.Equal(42, s.P99Ms);
    }

    [Fact]
    public void RingBuffer_ShouldEvictOldestSamples()
    {
        var stats = new DnsLatencyStatistics();

        // 先灌 1000 个极小值填满窗口
        for (var i = 0; i < 1000; i++)
            stats.RecordLatency(1);

        // 再灌 1000 个大值，应把小值全部挤出
        for (var i = 0; i < 1000; i++)
            stats.RecordLatency(500);

        var s = stats.GetStats();

        // 百分位只看窗口内样本，此时窗口全是 500
        Assert.Equal(500, s.P50Ms);
        // 但累计的最小值与请求总数不受窗口淘汰影响
        Assert.Equal(1, s.MinMs);
        Assert.Equal(2000, s.TotalRequests);
    }

    [Fact]
    public void MinMax_ShouldSurviveWindowEviction()
    {
        var stats = new DnsLatencyStatistics();

        stats.RecordLatency(9999);

        // 用足量样本把那个峰值挤出环形窗口
        for (var i = 0; i < 1500; i++)
            stats.RecordLatency(10);

        var s = stats.GetStats();

        // 最大值是累计量，不应随窗口淘汰而丢失
        Assert.Equal(9999, s.MaxMs);
        Assert.Equal(10, s.P50Ms);
    }

    [Fact]
    public void Average_ShouldUseAllRequests_NotJustWindow()
    {
        var stats = new DnsLatencyStatistics();

        for (var i = 0; i < 1500; i++)
            stats.RecordLatency(10);

        var s = stats.GetStats();

        Assert.Equal(1500, s.TotalRequests);
        Assert.Equal(10, s.AverageMs);
    }

    [Fact]
    public void ConcurrentRecording_ShouldNotLoseCounts()
    {
        var stats = new DnsLatencyStatistics();
        const int threads = 8;
        const int perThread = 500;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                stats.RecordLatency(5);
        });

        var s = stats.GetStats();

        Assert.Equal(threads * perThread, s.TotalRequests);
        Assert.Equal(5, s.AverageMs);
    }

    [Fact]
    public async Task ConcurrentReadWrite_ShouldNotThrow()
    {
        var stats = new DnsLatencyStatistics();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
                stats.RecordLatency(i++ % 100);
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var s = stats.GetStats();
                // 读到的快照必须自洽：百分位不能超出已观测的极值
                Assert.True(s.P99Ms <= s.MaxMs);
                Assert.True(s.MinMs <= s.MaxMs);
            }
        });

        // 不应抛异常（如排序期间数组被并发改写）
        await Task.WhenAll(writer, reader);
    }
}
