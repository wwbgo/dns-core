using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;

namespace DnsCore.Tests.Services;

/// <summary>
/// 服务层回归测试：缓存 TTL/LRU/否定缓存、记录存储并发、网段 ACL、限流。
/// </summary>
public class ServiceRegressionTests
{
    private static DnsCache CreateCache(CacheOptions? options = null)
        => new(new Mock<ILogger<DnsCache>>().Object, options);

    private static CustomRecordStore CreateStore()
        => new(new Mock<ILogger<CustomRecordStore>>().Object);

    private static DnsRecord Record(string domain, string value, int ttl = 300, DnsRecordType type = DnsRecordType.A)
        => new() { Domain = domain, Type = type, Value = value, TTL = ttl };

    // ==== 缓存 ====

    [Fact]
    public async Task Cache_ShouldDecrementTtl_AsEntryAges()
    {
        // 修复前返回原始 TTL，客户端会在服务端缓存之上再缓存一整个 TTL 周期
        var cache = CreateCache();
        cache.Set("example.com", DnsRecordType.A, [Record("example.com", "1.2.3.4", ttl: 100)]);

        await Task.Delay(1100);

        var result = cache.Get("example.com", DnsRecordType.A);

        result.Should().NotBeNull();
        result!.Records[0].TTL.Should().BeLessThan(100);
    }

    [Fact]
    public void Cache_ShouldNotLeakMutableState()
    {
        // 修复前直接返回内部 List，调用方修改会污染缓存
        var cache = CreateCache();
        cache.Set("example.com", DnsRecordType.A, [Record("example.com", "1.2.3.4")]);

        var first = cache.Get("example.com", DnsRecordType.A)!;
        first.Records.Clear();

        var second = cache.Get("example.com", DnsRecordType.A);
        second!.Records.Should().HaveCount(1);
    }

    [Fact]
    public void Cache_ShouldStoreNegativeAnswers()
    {
        // 修复前完全不缓存否定结果，不存在的域名每次都打上游
        var cache = CreateCache(new CacheOptions { NegativeTtlSeconds = 60 });

        cache.SetNegative("nx.example.com", DnsRecordType.A, DnsResponseCode.NxDomain);
        var result = cache.Get("nx.example.com", DnsRecordType.A);

        result.Should().NotBeNull();
        result!.IsNegative.Should().BeTrue();
        result.ResponseCode.Should().Be(DnsResponseCode.NxDomain);
    }

    [Fact]
    public void Cache_ShouldEvictLeastRecentlyUsed_WhenAtCapacity()
    {
        var cache = CreateCache(new CacheOptions { MaxEntries = 3 });

        cache.Set("a.com", DnsRecordType.A, [Record("a.com", "1.1.1.1")]);
        cache.Set("b.com", DnsRecordType.A, [Record("b.com", "2.2.2.2")]);
        cache.Set("c.com", DnsRecordType.A, [Record("c.com", "3.3.3.3")]);

        // 触碰 a 使其成为最近使用，随后插入应淘汰 b
        cache.Get("a.com", DnsRecordType.A).Should().NotBeNull();
        cache.Set("d.com", DnsRecordType.A, [Record("d.com", "4.4.4.4")]);

        cache.Get("a.com", DnsRecordType.A).Should().NotBeNull();
        cache.Get("b.com", DnsRecordType.A).Should().BeNull();
        cache.GetStats().TotalEntries.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void Cache_ShouldClampNonPositiveTtl()
    {
        // 修复前 TTL<=0 会算出负 TimeSpan，条目写入即过期
        var cache = CreateCache(new CacheOptions { MinTtlSeconds = 5 });

        cache.Set("zero.com", DnsRecordType.A, [Record("zero.com", "1.1.1.1", ttl: 0)]);

        cache.Get("zero.com", DnsRecordType.A).Should().NotBeNull();
    }

    [Fact]
    public void Cache_ShouldClampExcessiveTtl()
    {
        var cache = CreateCache(new CacheOptions { MaxTtlSeconds = 60 });

        cache.Set("long.com", DnsRecordType.A, [Record("long.com", "1.1.1.1", ttl: int.MaxValue)]);

        cache.Get("long.com", DnsRecordType.A)!.Records[0].TTL.Should().BeLessThanOrEqualTo(60);
    }

    [Fact]
    public void Cache_ShouldSeparateEntriesByRecordType()
    {
        var cache = CreateCache();

        cache.Set("example.com", DnsRecordType.A, [Record("example.com", "1.1.1.1")]);
        cache.Set("example.com", DnsRecordType.AAAA,
            [Record("example.com", "::1", type: DnsRecordType.AAAA)]);

        cache.Get("example.com", DnsRecordType.A)!.Records[0].Value.Should().Be("1.1.1.1");
        cache.Get("example.com", DnsRecordType.AAAA)!.Records[0].Value.Should().Be("::1");
    }

    // ==== 记录存储 ====

    [Fact]
    public void Store_ShouldRewriteWildcardOwnerName_ToQueriedName()
    {
        // 修复前返回 owner name 为 "*.example.com" 的记录，客户端会丢弃应答
        var store = CreateStore();
        store.AddRecord(Record("*.example.com", "192.168.1.100"));

        var results = store.Query("api.example.com", DnsRecordType.A);

        results.Should().NotBeNull();
        results![0].Domain.Should().Be("api.example.com");
        results[0].Value.Should().Be("192.168.1.100");
    }

    // ContainsDomain 用于区分 NXDOMAIN 与 NODATA：
    // 域名在本地存在但没有被查询的类型时，服务器就地返回 NODATA 而不转发上游。
    // 修复前 test.cc（只配了 A）的 AAAA 查询会打到上游并等满超时，
    // 因 nslookup 每次并发查 A+AAAA，表现为固定 2 秒卡顿。
    [Fact]
    public void Store_ContainsDomain_ShouldMatchAnyRecordType()
    {
        var store = CreateStore();
        store.AddRecord(Record("test.cc", "192.168.1.1"));

        // 只配了 A 记录，但域名本身应被认为存在
        store.ContainsDomain("test.cc").Should().BeTrue();
        store.Query("test.cc", DnsRecordType.AAAA).Should().BeNull();
    }

    [Fact]
    public void Store_ContainsDomain_ShouldMatchWildcard()
    {
        var store = CreateStore();
        store.AddRecord(Record("*.wild.test", "10.0.0.9"));

        store.ContainsDomain("a.wild.test").Should().BeTrue();
        store.ContainsDomain("deep.nested.wild.test").Should().BeTrue();
    }

    [Fact]
    public void Store_ContainsDomain_ShouldBeFalseForUnknownDomain()
    {
        var store = CreateStore();
        store.AddRecord(Record("test.cc", "192.168.1.1"));

        // 未知域名必须返回 false，否则会把本该转发上游的查询错误地答成 NODATA
        store.ContainsDomain("baidu.com").Should().BeFalse();
        store.ContainsDomain("other.test").Should().BeFalse();
    }

    [Fact]
    public void Store_ContainsDomain_ShouldBeCaseInsensitive()
    {
        var store = CreateStore();
        store.AddRecord(Record("Test.CC", "192.168.1.1"));

        store.ContainsDomain("test.cc").Should().BeTrue();
        store.ContainsDomain("TEST.CC").Should().BeTrue();
    }

    [Fact]
    public void Store_ContainsDomain_ShouldNotMatchBySubstring()
    {
        var store = CreateStore();
        store.AddRecord(Record("test.cc", "192.168.1.1"));

        // "test.cc" 不应让 "notest.cc" 或 "test.ccx" 被误判为存在
        store.ContainsDomain("notest.cc").Should().BeFalse();
        store.ContainsDomain("test.ccx").Should().BeFalse();
    }

    [Fact]
    public void Store_ShouldPreferExactMatch_OverWildcard()
    {
        var store = CreateStore();
        store.AddRecord(Record("*.example.com", "10.0.0.1"));
        store.AddRecord(Record("www.example.com", "10.0.0.2"));

        store.Query("www.example.com", DnsRecordType.A)![0].Value.Should().Be("10.0.0.2");
        store.Query("other.example.com", DnsRecordType.A)![0].Value.Should().Be("10.0.0.1");
    }

    [Fact]
    public void Store_ShouldPreferMostSpecificWildcard()
    {
        var store = CreateStore();
        store.AddRecord(Record("*.example.com", "10.0.0.1"));
        store.AddRecord(Record("*.dev.example.com", "10.0.0.2"));

        store.Query("api.dev.example.com", DnsRecordType.A)![0].Value.Should().Be("10.0.0.2");
    }

    [Fact]
    public async Task Store_ShouldRemainConsistent_UnderConcurrentWrites()
    {
        // 修复前把可变 List 放进 ConcurrentDictionary，
        // 写入线程 list.Add 与查询线程复制会并发访问同一个非线程安全的 List
        var store = CreateStore();

        var writers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                store.AddRecord(Record("concurrent.example.com", $"10.0.{worker}.{i}"));
        }));

        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                store.Query("concurrent.example.com", DnsRecordType.A);
        }));

        var act = async () => await Task.WhenAll(writers.Concat(readers));

        await act.Should().NotThrowAsync();
        store.Query("concurrent.example.com", DnsRecordType.A)!.Should().HaveCount(800);
    }

    [Fact]
    public void Store_ShouldNotStoreDuplicates()
    {
        var store = CreateStore();

        store.AddRecord(Record("dup.example.com", "1.1.1.1"));
        store.AddRecord(Record("dup.example.com", "1.1.1.1"));

        store.Query("dup.example.com", DnsRecordType.A).Should().HaveCount(1);
    }

    [Fact]
    public async Task Store_ShouldRemoveOnlyRequestedValue()
    {
        var store = CreateStore();
        store.AddRecord(Record("multi.example.com", "1.1.1.1"));
        store.AddRecord(Record("multi.example.com", "1.1.1.2"));

        var removed = await store.RemoveRecordAsync("multi.example.com", DnsRecordType.A, "1.1.1.1");

        removed.Should().BeTrue();
        var remaining = store.Query("multi.example.com", DnsRecordType.A);
        remaining.Should().ContainSingle();
        remaining![0].Value.Should().Be("1.1.1.2");
    }

    [Fact]
    public async Task Store_ShouldReturnFalse_WhenRequestedValueDoesNotExist()
    {
        var store = CreateStore();
        store.AddRecord(Record("multi.example.com", "1.1.1.1"));

        var removed = await store.RemoveRecordAsync("multi.example.com", DnsRecordType.A, "1.1.1.2");

        removed.Should().BeFalse();
        store.Query("multi.example.com", DnsRecordType.A).Should().ContainSingle();
    }

    // ==== 网段 ACL ====

    [Theory]
    [InlineData("192.168.1.50", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void Acl_ShouldMatchPrivateNetworks(string address, bool expected)
    {
        var acl = new NetworkAcl(["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]);

        acl.IsAllowed(IPAddress.Parse(address)).Should().Be(expected);
    }

    [Fact]
    public void Acl_ShouldHandleIPv4MappedIPv6()
    {
        // 双栈监听下 IPv4 客户端以 ::ffff:a.b.c.d 形式到达，必须能匹配 IPv4 规则
        var acl = new NetworkAcl(["192.168.0.0/16"]);

        acl.IsAllowed(IPAddress.Parse("192.168.1.1").MapToIPv6()).Should().BeTrue();
    }

    [Fact]
    public void Acl_ShouldAllowAll_WhenEmpty()
    {
        new NetworkAcl(null).IsAllowed(IPAddress.Parse("8.8.8.8")).Should().BeTrue();
    }

    [Fact]
    public void Acl_ShouldRejectMalformedConfig()
    {
        // 配置写错应当暴露，而不是静默放行
        var act = () => new NetworkAcl(["not-a-cidr"]);
        act.Should().Throw<FormatException>();
    }

    // ==== 限流 ====

    [Fact]
    public void RateLimiter_ShouldThrottleBurstFromSingleClient()
    {
        var limiter = new ClientRateLimiter(maxQueriesPerSecond: 10);
        var client = IPAddress.Parse("192.168.1.10");

        var allowed = Enumerable.Range(0, 50).Count(_ => limiter.TryAcquire(client));

        allowed.Should().BeLessThan(50);
        allowed.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RateLimiter_ShouldTrackClientsIndependently()
    {
        var limiter = new ClientRateLimiter(maxQueriesPerSecond: 5);

        for (var i = 0; i < 5; i++)
            limiter.TryAcquire(IPAddress.Parse("192.168.1.10"));

        // 第一个客户端已耗尽，另一个客户端不应受影响
        limiter.TryAcquire(IPAddress.Parse("192.168.1.11")).Should().BeTrue();
    }

    [Fact]
    public void RateLimiter_ShouldBeDisabled_WhenLimitIsZero()
    {
        var limiter = new ClientRateLimiter(maxQueriesPerSecond: 0);
        var client = IPAddress.Parse("192.168.1.10");

        Enumerable.Range(0, 1000).All(_ => limiter.TryAcquire(client)).Should().BeTrue();
    }
}
