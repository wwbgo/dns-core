using DnsCore.Models;
using DnsCore.Repositories;

namespace DnsCore.Tests.Repositories;

/// <summary>
/// 三种持久化实现（JSON / SQLite / LiteDB）的契约测试。
///
/// 此前这三种实现只有一个手动运行的控制台程序（tests/PersistenceTest）做验证，
/// 不在解决方案与 CI 内。这里用同一套用例跑遍全部实现，确保它们对
/// IDnsRecordRepository 的语义一致——差异往往出在往返保真、覆盖语义和
/// 大小写处理上，而这些正是切换 Provider 时最容易踩的坑。
/// </summary>
public sealed class DnsRecordRepositoryTests : IDisposable
{
    private readonly string _dir;

    public DnsRecordRepositoryTests()
    {
        // 每个测试类实例独立目录，避免并行执行时互相干扰
        _dir = Path.Combine(Path.GetTempPath(), "dnscore-repo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 数据库文件句柄可能尚未释放，临时目录留待系统清理
        }
    }

    public static TheoryData<string> Providers => new() { "json", "sqlite", "litedb" };

    private IDnsRecordRepository Create(string provider)
    {
        var path = Path.Combine(_dir, $"{provider}-{Guid.NewGuid():N}");
        return provider switch
        {
            "json" => new JsonFileRepository(path + ".json"),
            "sqlite" => new SqliteRepository(path + ".db"),
            "litedb" => new LiteDbRepository(path + ".litedb"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "未知的持久化实现")
        };
    }

    private static DnsRecord Rec(string domain, DnsRecordType type = DnsRecordType.A,
        string value = "192.168.1.1", int ttl = 3600) =>
        new() { Domain = domain, Type = type, Value = value, TTL = ttl };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LoadAll_ShouldReturnEmpty_WhenNeverWritten(string provider)
    {
        var repo = Create(provider);

        var records = await repo.LoadAllAsync();

        Assert.Empty(records);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AddAsync_ShouldPersistRecord(string provider)
    {
        var repo = Create(provider);

        await repo.AddAsync(Rec("a.local"));

        var records = (await repo.LoadAllAsync()).ToList();

        Assert.Single(records);
        Assert.Equal("a.local", records[0].Domain);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RoundTrip_ShouldPreserveAllFields(string provider)
    {
        var repo = Create(provider);
        var original = Rec("full.local", DnsRecordType.TXT, "v=spf1 include:_spf.example.com ~all", 7200);

        await repo.AddAsync(original);
        var loaded = (await repo.LoadAllAsync()).Single();

        // 逐字段比对：TTL 与 Type 曾是最易在序列化中丢失或错位的两项
        Assert.Equal(original.Domain, loaded.Domain);
        Assert.Equal(original.Type, loaded.Type);
        Assert.Equal(original.Value, loaded.Value);
        Assert.Equal(original.TTL, loaded.TTL);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RoundTrip_ShouldPreserveWildcardDomain(string provider)
    {
        var repo = Create(provider);

        await repo.AddAsync(Rec("*.wild.local"));
        var loaded = (await repo.LoadAllAsync()).Single();

        Assert.Equal("*.wild.local", loaded.Domain);
        Assert.True(loaded.IsWildcard);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SaveAll_ShouldReplaceEntireSet_NotAppend(string provider)
    {
        var repo = Create(provider);

        await repo.SaveAllAsync([Rec("one.local"), Rec("two.local")]);
        await repo.SaveAllAsync([Rec("three.local")]);

        var records = (await repo.LoadAllAsync()).ToList();

        // 整组替换语义：第二次保存后不应残留第一次的记录
        Assert.Single(records);
        Assert.Equal("three.local", records[0].Domain);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SaveAll_ShouldPersistMultipleRecords(string provider)
    {
        var repo = Create(provider);

        DnsRecord[] batch =
        [
            Rec("a.local", DnsRecordType.A, "1.1.1.1"),
            Rec("b.local", DnsRecordType.AAAA, "2001:db8::1"),
            Rec("c.local", DnsRecordType.CNAME, "target.local")
        ];

        await repo.SaveAllAsync(batch);
        var loaded = (await repo.LoadAllAsync()).ToList();

        Assert.Equal(3, loaded.Count);
        Assert.Equal(
            batch.Select(r => r.Domain).OrderBy(d => d),
            loaded.Select(r => r.Domain).OrderBy(d => d));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_ShouldRemoveOnlyMatchingRecord(string provider)
    {
        var repo = Create(provider);

        await repo.SaveAllAsync([
            Rec("keep.local", DnsRecordType.A),
            Rec("drop.local", DnsRecordType.A)
        ]);

        await repo.DeleteAsync("drop.local", DnsRecordType.A);
        var records = (await repo.LoadAllAsync()).ToList();

        Assert.Single(records);
        Assert.Equal("keep.local", records[0].Domain);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_ShouldDistinguishByType(string provider)
    {
        var repo = Create(provider);

        // 同名不同类型：删 A 不应波及 AAAA
        await repo.SaveAllAsync([
            Rec("dual.local", DnsRecordType.A, "1.1.1.1"),
            Rec("dual.local", DnsRecordType.AAAA, "2001:db8::1")
        ]);

        await repo.DeleteAsync("dual.local", DnsRecordType.A);
        var records = (await repo.LoadAllAsync()).ToList();

        Assert.Single(records);
        Assert.Equal(DnsRecordType.AAAA, records[0].Type);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_ShouldBeNoOp_WhenRecordAbsent(string provider)
    {
        var repo = Create(provider);

        await repo.AddAsync(Rec("present.local"));
        await repo.DeleteAsync("absent.local", DnsRecordType.A);

        Assert.Single(await repo.LoadAllAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Clear_ShouldRemoveEverything(string provider)
    {
        var repo = Create(provider);

        await repo.SaveAllAsync([Rec("a.local"), Rec("b.local")]);
        await repo.ClearAsync();

        Assert.Empty(await repo.LoadAllAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Clear_ShouldBeIdempotent(string provider)
    {
        var repo = Create(provider);

        await repo.ClearAsync();
        await repo.ClearAsync();

        Assert.Empty(await repo.LoadAllAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Data_ShouldSurviveNewRepositoryInstance(string provider)
    {
        // 真正验证"持久化"：换一个实例读同一份文件，而不是读进程内缓存。
        // 必须先写完并释放再打开第二个实例——LiteDB/SQLite 对数据库文件持独占锁，
        // 同时构造两个实例会在第二个的构造函数里就抛 IOException。
        var path = Path.Combine(_dir, $"reopen-{provider}-{Guid.NewGuid():N}");

        IDnsRecordRepository Open() => provider switch
        {
            "json" => new JsonFileRepository(path + ".json"),
            "sqlite" => new SqliteRepository(path + ".db"),
            "litedb" => new LiteDbRepository(path + ".litedb"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        var first = Open();
        await first.SaveAllAsync([Rec("persist.local", DnsRecordType.MX, "10 mail.local", 600)]);
        (first as IDisposable)?.Dispose();

        var second = Open();
        var loaded = (await second.LoadAllAsync()).Single();
        (second as IDisposable)?.Dispose();

        Assert.Equal("persist.local", loaded.Domain);
        Assert.Equal(DnsRecordType.MX, loaded.Type);
        Assert.Equal("10 mail.local", loaded.Value);
        Assert.Equal(600, loaded.TTL);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SaveAll_ShouldAcceptEmptySet(string provider)
    {
        var repo = Create(provider);

        await repo.SaveAllAsync([Rec("temp.local")]);
        await repo.SaveAllAsync([]);

        Assert.Empty(await repo.LoadAllAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Value_ShouldSurviveSpecialCharacters(string provider)
    {
        var repo = Create(provider);
        // TXT 值常含引号、分号、反斜杠，这些在 JSON 与 SQL 里都需正确转义
        const string tricky = @"v=spf1 ""quoted"" a:b\c; include:_spf.example.com";

        await repo.AddAsync(Rec("txt.local", DnsRecordType.TXT, tricky));
        var loaded = (await repo.LoadAllAsync()).Single();

        Assert.Equal(tricky, loaded.Value);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Value_ShouldSurviveUnicode(string provider)
    {
        var repo = Create(provider);
        const string unicode = "中文记录值-テスト-🌐";

        await repo.AddAsync(Rec("unicode.local", DnsRecordType.TXT, unicode));
        var loaded = (await repo.LoadAllAsync()).Single();

        Assert.Equal(unicode, loaded.Value);
    }
}
