using DnsCore.Configuration;
using DnsCore.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DnsCore.Tests.Services;

/// <summary>
/// 上游设置的校验与运行时生效测试。
/// </summary>
public class UpstreamSettingsTests
{
    private static (UpstreamSettingsStore store, DnsServerOptions options) Create(string? dataDir = null)
    {
        var options = new DnsServerOptions
        {
            Persistence = new PersistenceOptions
            {
                FilePath = Path.Combine(dataDir ?? Path.GetTempPath(), "records.json")
            }
        };

        var cache = new DnsCache(new Mock<ILogger<DnsCache>>().Object);
        var resolver = new UpstreamDnsResolver(
            new Mock<ILogger<UpstreamDnsResolver>>().Object, cache, options);

        var store = new UpstreamSettingsStore(
            new Mock<ILogger<UpstreamSettingsStore>>().Object, options, resolver, cache);

        return (store, options);
    }

    private static UpstreamSettings Valid() => new()
    {
        EnableUpstreamDnsQuery = true,
        UpstreamDnsServers = ["223.5.5.5"],
        TimeoutMilliseconds = 3000,
        RaceUpstreams = false
    };

    // ==== 默认值 ====

    [Fact]
    public void RaceUpstreams_ShouldDefaultToSequential()
    {
        // 顺序模式下列表次序即优先级，适合"首选内网 DNS，失败才走公网"
        new UpstreamOptions().RaceUpstreams.Should().BeFalse();
        new UpstreamSettings().RaceUpstreams.Should().BeFalse();
    }

    // ==== 校验 ====

    [Fact]
    public void Validate_ShouldAcceptValidSettings()
    {
        var (store, _) = Create();
        store.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    public void Validate_ShouldRejectLoopbackUpstream(string address)
    {
        // 上游指向本机会形成查询环：未命中 -> 转发给自己 -> 再次未命中
        var (store, _) = Create();
        var settings = Valid();
        settings.UpstreamDnsServers = [address];

        var result = store.Validate(settings);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("环路");
    }

    [Theory]
    [InlineData("dns.alidns.com")]
    [InlineData("999.1.1.1")]
    [InlineData("not-an-ip")]
    [InlineData("223.5.5.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldRejectNonIpAddress(string address)
    {
        var (store, _) = Create();
        var settings = Valid();
        settings.UpstreamDnsServers = [address];

        store.Validate(settings).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("223.5.5")]     // IPAddress.TryParse 会解析成 223.5.0.5
    [InlineData("10.1")]        // 会解析成 10.0.0.1
    [InlineData("1")]           // 会解析成 0.0.0.1
    [InlineData("192.168.1")]   // 会解析成 192.168.0.1
    public void Validate_ShouldRejectInetAtonShorthand(string address)
    {
        // IPAddress.TryParse 接受 inet_aton 简写且不报错：
        // 上游地址少打一位会静默指向另一台服务器，必须要求四段完整写法。
        var (store, _) = Create();
        var settings = Valid();
        settings.UpstreamDnsServers = [address];

        var result = store.Validate(settings);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("无效的 IP");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("223.5.5.5")]
    [InlineData("0.0.0.1")]
    [InlineData("255.255.255.254")]
    public void Validate_ShouldAcceptFullIPv4(string address)
    {
        var (store, _) = Create();
        var settings = Valid();
        settings.UpstreamDnsServers = [address];

        store.Validate(settings).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectDuplicateServers()
    {
        var (store, _) = Create();
        var settings = Valid();
        settings.UpstreamDnsServers = ["223.5.5.5", "223.5.5.5"];

        var result = store.Validate(settings);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("重复");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(199)]
    [InlineData(30001)]
    [InlineData(-1)]
    public void Validate_ShouldRejectTimeoutOutOfRange(int timeout)
    {
        var (store, _) = Create();
        var settings = Valid();
        settings.TimeoutMilliseconds = timeout;

        store.Validate(settings).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ShouldRejectTooManyServers()
    {
        var (store, _) = Create();
        var settings = Valid();
        settings.UpstreamDnsServers = [.. Enumerable.Range(1, 17).Select(i => $"10.0.0.{i}")];

        store.Validate(settings).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ShouldAcceptEmptyServerList()
    {
        // 空列表是合法的：表示回落到系统 DNS
        var (store, _) = Create();
        var settings = Valid();
        settings.UpstreamDnsServers = [];

        store.Validate(settings).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldAcceptIPv6Upstream()
    {
        var (store, _) = Create();
        var settings = Valid();
        settings.UpstreamDnsServers = ["2400:3200::1"];

        store.Validate(settings).IsValid.Should().BeTrue();
    }

    // ==== 运行时生效 ====

    [Fact]
    public async Task SaveAsync_ShouldApplyToLiveOptions()
    {
        // DnsServer/UpstreamDnsResolver 每次查询都读这些属性，
        // 因此改动必须立即体现在选项上，无需重启
        var dir = Path.Combine(Path.GetTempPath(), $"dnscore-up-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var (store, options) = Create(dir);

            var settings = new UpstreamSettings
            {
                EnableUpstreamDnsQuery = false,
                UpstreamDnsServers = ["1.1.1.1", "8.8.8.8"],
                TimeoutMilliseconds = 1500,
                RaceUpstreams = true
            };

            var result = await store.SaveAsync(settings);

            result.IsValid.Should().BeTrue();
            options.EnableUpstreamDnsQuery.Should().BeFalse();
            options.UpstreamDnsServers.Should().BeEquivalentTo(["1.1.1.1", "8.8.8.8"]);
            options.Upstream.TimeoutMilliseconds.Should().Be(1500);
            options.Upstream.RaceUpstreams.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistAndReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dnscore-up-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var (store1, _) = Create(dir);

            await store1.SaveAsync(new UpstreamSettings
            {
                EnableUpstreamDnsQuery = false,
                UpstreamDnsServers = ["9.9.9.9"],
                TimeoutMilliseconds = 2200,
                RaceUpstreams = true
            });

            File.Exists(Path.Combine(dir, "upstream-settings.json")).Should().BeTrue();

            // 模拟重启：新实例从磁盘加载
            var (store2, options2) = Create(dir);
            await store2.LoadAsync();

            options2.EnableUpstreamDnsQuery.Should().BeFalse();
            options2.UpstreamDnsServers.Should().BeEquivalentTo(["9.9.9.9"]);
            options2.Upstream.TimeoutMilliseconds.Should().Be(2200);
            options2.Upstream.RaceUpstreams.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SaveAsync_ShouldRejectInvalidWithoutMutatingOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dnscore-up-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var (store, options) = Create(dir);
            var originalTimeout = options.Upstream.TimeoutMilliseconds;

            var result = await store.SaveAsync(new UpstreamSettings
            {
                EnableUpstreamDnsQuery = true,
                UpstreamDnsServers = ["127.0.0.1"],
                TimeoutMilliseconds = 5000,
                RaceUpstreams = false
            });

            result.IsValid.Should().BeFalse();
            // 校验失败不应产生任何副作用
            options.Upstream.TimeoutMilliseconds.Should().Be(originalTimeout);
            File.Exists(Path.Combine(dir, "upstream-settings.json")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldIgnoreCorruptFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dnscore-up-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "upstream-settings.json"), "{not valid json");

            var (store, options) = Create(dir);
            var act = async () => await store.LoadAsync();

            await act.Should().NotThrowAsync();
            // 损坏文件应被忽略，保留配置文件中的默认值
            options.EnableUpstreamDnsQuery.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldIgnorePersistedInvalidSettings()
    {
        // 历史遗留的非法数据（例如早期版本写入的本机地址）不应被应用
        var dir = Path.Combine(Path.GetTempPath(), $"dnscore-up-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "upstream-settings.json"),
                """{"EnableUpstreamDnsQuery":true,"UpstreamDnsServers":["127.0.0.1"],"TimeoutMilliseconds":3000,"RaceUpstreams":false}""");

            var (store, options) = Create(dir);
            await store.LoadAsync();

            options.UpstreamDnsServers.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ==== 状态查询 ====

    [Fact]
    public void GetStatus_ShouldReportSystemDnsWhenListEmpty()
    {
        var (store, options) = Create();
        options.UpstreamDnsServers = [];

        var status = store.GetStatus();

        status.UsingSystemDns.Should().BeTrue();
        status.UpstreamDnsServers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatus_ShouldReportConfiguredServers()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dnscore-up-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var (store, _) = Create(dir);
            await store.SaveAsync(Valid());

            var status = store.GetStatus();

            status.UsingSystemDns.Should().BeFalse();
            status.UpstreamDnsServers.Should().BeEquivalentTo(["223.5.5.5"]);
            status.EffectiveServers.Should().BeEquivalentTo(["223.5.5.5"]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
