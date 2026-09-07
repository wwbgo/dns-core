using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using DnsCore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

BenchmarkSwitcher.FromAssembly(typeof(DnsHotPathBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
[RankColumn]
public class DnsHotPathBenchmarks
{
    private byte[] _queryData = [];
    private DnsHeader _header = null!;
    private List<DnsQuestion> _questions = null!;
    private List<DnsRecord> _answers = null!;

    private DnsCache _cache = null!;
    private CustomRecordStore _store = null!;
    private NetworkAcl _acl = null!;
    private IPAddress _client = null!;

    [GlobalSetup]
    public void Setup()
    {
        _queryData = BuildQuery("bench.example.com", DnsRecordType.A);
        var query = DnsMessageParser.Parse(_queryData);
        _header = query.Header;
        _questions = query.Questions;
        _answers =
        [
            new DnsRecord
            {
                Domain = "bench.example.com",
                Type = DnsRecordType.A,
                Value = "10.0.0.10",
                TTL = 3600
            }
        ];

        _cache = new DnsCache(
            NullLogger<DnsCache>.Instance,
            new CacheOptions
            {
                Enabled = true,
                MaxEntries = 10000,
                MinTtlSeconds = 60,
                MaxTtlSeconds = 86400,
                NegativeTtlSeconds = 60
            });
        _cache.Set("bench.example.com", DnsRecordType.A, _answers);

        _store = new CustomRecordStore(NullLogger<CustomRecordStore>.Instance);
        _store.AddRecord(_answers[0]);
        _store.AddRecord(new DnsRecord
        {
            Domain = "*.example.com",
            Type = DnsRecordType.A,
            Value = "10.0.0.20",
            TTL = 3600
        });

        _acl = new NetworkAcl(["10.0.0.0/8", "192.168.0.0/16", "::1/128"]);
        _client = IPAddress.Parse("192.168.1.20");
    }

    [Benchmark]
    public DnsQuery ParseQuery() => DnsMessageParser.Parse(_queryData);

    [Benchmark]
    public byte[] BuildResponse() => DnsMessageParser.BuildResponse(
        _header,
        _questions,
        _answers,
        DnsResponseCode.NoError,
        isAuthoritative: true,
        maxSize: DnsLimits.MaxEdnsPayloadSize,
        includeEdnsOpt: true,
        ednsPayloadSize: DnsLimits.MaxEdnsPayloadSize);

    [Benchmark]
    public DnsCacheResult? CacheHit() => _cache.Get("bench.example.com", DnsRecordType.A);

    [Benchmark]
    public DnsCacheResult? CacheMiss() => _cache.Get("miss.example.com", DnsRecordType.A);

    [Benchmark]
    public List<DnsRecord>? CustomRecordExact() => _store.Query("bench.example.com", DnsRecordType.A);

    [Benchmark]
    public List<DnsRecord>? CustomRecordWildcard() => _store.Query("api.example.com", DnsRecordType.A);

    [Benchmark]
    public bool AclIsAllowed() => _acl.IsAllowed(_client);

    private static byte[] BuildQuery(string domain, DnsRecordType type)
    {
        using var writer = new DnsWriter(128);
        writer.WriteHeader(new DnsHeader
        {
            TransactionId = 0x1234,
            Flags = 0x0100,
            QuestionCount = 1
        });
        writer.WriteDomainName(domain, useCompression: false);
        writer.WriteUInt16((ushort)type);
        writer.WriteUInt16(1);
        return writer.ToArray();
    }
}
