using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using DnsCore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

const int Port = 15399;
const string Domain = "load.test";
const int TotalQueries = 10000;

var concurrency = args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 0
    ? parsed
    : 8;
var perWorker = Math.Max(1, TotalQueries / concurrency);
var total = perWorker * concurrency;

using var cts = new CancellationTokenSource();
var options = new DnsServerOptions
{
    Port = Port,
    ListenAddress = "127.0.0.1",
    EnableUpstreamDnsQuery = false,
    Security = new DnsSecurityOptions
    {
        EnableClientRestriction = false,
        MaxQueriesPerSecondPerClient = 0,
        MaxConcurrentQueries = Math.Max(256, concurrency * 4)
    }
};

var store = new CustomRecordStore(NullLogger<CustomRecordStore>.Instance);
store.AddRecord(new DnsRecord
{
    Domain = Domain,
    Type = DnsRecordType.A,
    Value = "192.0.2.1",
    TTL = 60
});

var cache = new DnsCache(
    NullLogger<DnsCache>.Instance,
    new CacheOptions { Enabled = false });
using var resolver = new UpstreamDnsResolver(
    NullLogger<UpstreamDnsResolver>.Instance,
    cache,
    options);
var server = new DnsServer(
    NullLogger<DnsServer>.Instance,
    store,
    resolver,
    options,
    new DnsQueryStatistics(),
    new DnsLatencyStatistics());

var serverTask = server.StartAsync(cts.Token);

try
{
    var waitStartedAt = Stopwatch.StartNew();
    while (!server.IsListening)
    {
        if (serverTask.IsFaulted)
            await serverTask;

        if (waitStartedAt.Elapsed > TimeSpan.FromSeconds(5))
            throw new TimeoutException("DNS 服务未能在 5 秒内启动");

        await Task.Delay(20);
    }

    var latencies = new ConcurrentBag<double>();
    var errors = 0;
    var workerId = 0;

    var startedAt = Stopwatch.StartNew();

    await Parallel.ForEachAsync(
        Enumerable.Range(0, concurrency),
        new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cts.Token
        },
        async (_, token) =>
        {
            var worker = Interlocked.Increment(ref workerId) - 1;
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Connect(IPAddress.Loopback, Port);

            for (var i = 0; i < perWorker; i++)
            {
                var transactionId = (ushort)((worker * perWorker + i) % 65535 + 1);
                var query = BuildQuery(transactionId, Domain);
                var sw = Stopwatch.StartNew();

                try
                {
                    await client.SendAsync(query, token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(1));

                    var response = await client.ReceiveAsync(timeout.Token);
                    sw.Stop();

                    if (response.Buffer.Length < DnsHeader.Size)
                    {
                        Interlocked.Increment(ref errors);
                        continue;
                    }

                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref errors);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });

    startedAt.Stop();

    var sorted = latencies.OrderBy(x => x).ToArray();
    var qps = total / startedAt.Elapsed.TotalSeconds;

    Console.WriteLine($"Concurrency: {concurrency}");
    Console.WriteLine($"Queries:     {total}");
    Console.WriteLine($"Elapsed:     {startedAt.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"QPS:         {qps:F0}");
    Console.WriteLine($"Errors:      {errors}");
    Console.WriteLine($"P50:         {Percentile(sorted, 0.50):F3} ms");
    Console.WriteLine($"P95:         {Percentile(sorted, 0.95):F3} ms");
    Console.WriteLine($"P99:         {Percentile(sorted, 0.99):F3} ms");
}
finally
{
    cts.Cancel();
    server.Stop();

    try
    {
        await serverTask;
    }
    catch (OperationCanceledException)
    {
        // Expected shutdown path
    }
}

static byte[] BuildQuery(ushort transactionId, string domain)
{
    using var writer = new DnsWriter(128);
    writer.WriteHeader(new DnsHeader
    {
        TransactionId = transactionId,
        Flags = 0x0100,
        QuestionCount = 1
    });
    writer.WriteDomainName(domain, useCompression: false);
    writer.WriteUInt16((ushort)DnsRecordType.A);
    writer.WriteUInt16(1);
    return writer.ToArray();
}

static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 0)
        return 0;

    var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}
