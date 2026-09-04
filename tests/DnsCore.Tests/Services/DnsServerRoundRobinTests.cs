using System.Net;
using System.Net.Sockets;
using System.Text;
using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using DnsCore.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DnsCore.Tests.Services;

public sealed class DnsServerRoundRobinTests
{
    [Fact]
    public async Task DnsServer_ShouldRotateLocalARecordOrder()
    {
        var serverLogger = new Mock<ILogger<DnsServer>>();
        var storeLogger = new Mock<ILogger<CustomRecordStore>>();
        var resolverLogger = new Mock<ILogger<UpstreamDnsResolver>>();
        var cacheLogger = new Mock<ILogger<DnsCache>>();

        var store = new CustomRecordStore(storeLogger.Object);
        store.AddRecord(ARecord("1.1.1.1"));
        store.AddRecord(ARecord("1.1.1.2"));

        var options = new DnsServerOptions
        {
            Port = 15355,
            CustomRecords = [],
            UpstreamDnsServers = [],
            EnableUpstreamDnsQuery = false
        };

        var cache = new DnsCache(cacheLogger.Object);
        var resolver = new UpstreamDnsResolver(resolverLogger.Object, cache, options);
        var statistics = new DnsQueryStatistics();
        var latencyStatistics = new DnsLatencyStatistics();
        var server = new DnsServer(
            serverLogger.Object,
            store,
            resolver,
            options,
            statistics,
            latencyStatistics);

        using var cts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.StartAsync(cts.Token));

        try
        {
            await WaitUntilListeningAsync(server);

            using var client = new UdpClient();
            client.Connect(IPAddress.Loopback, options.Port);

            var first = await QueryAsync(client, 0x1234);
            var second = await QueryAsync(client, 0x5678);

            first.Should().Equal("1.1.1.2", "1.1.1.1");
            second.Should().Equal("1.1.1.1", "1.1.1.2");
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
                // Expected when cancelling
            }
        }
    }

    private static DnsRecord ARecord(string value, int weight = 1) => new()
    {
        Domain = "roundrobin.test",
        Type = DnsRecordType.A,
        Value = value,
        TTL = 60,
        Weight = weight
    };

    [Fact]
    public async Task DnsServer_ShouldHonorRecordWeights()
    {
        var serverLogger = new Mock<ILogger<DnsServer>>();
        var storeLogger = new Mock<ILogger<CustomRecordStore>>();
        var resolverLogger = new Mock<ILogger<UpstreamDnsResolver>>();
        var cacheLogger = new Mock<ILogger<DnsCache>>();

        var store = new CustomRecordStore(storeLogger.Object);
        store.AddRecord(ARecord("1.1.1.1", weight: 1));
        store.AddRecord(ARecord("1.1.1.2", weight: 3));

        var options = new DnsServerOptions
        {
            Port = 15356,
            CustomRecords = [],
            UpstreamDnsServers = [],
            EnableUpstreamDnsQuery = false
        };

        var cache = new DnsCache(cacheLogger.Object);
        var resolver = new UpstreamDnsResolver(resolverLogger.Object, cache, options);
        var server = new DnsServer(
            serverLogger.Object,
            store,
            resolver,
            options,
            new DnsQueryStatistics(),
            new DnsLatencyStatistics());

        using var cts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.StartAsync(cts.Token));

        try
        {
            await WaitUntilListeningAsync(server);

            using var client = new UdpClient();
            client.Connect(IPAddress.Loopback, options.Port);

            var firstRecords = new List<string>();
            for (ushort id = 0x2000; id < 0x2004; id++)
                firstRecords.Add((await QueryAsync(client, id))[0]);

            firstRecords.Should().Equal("1.1.1.2", "1.1.1.2", "1.1.1.2", "1.1.1.1");
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
                // Expected when cancelling
            }
        }
    }

    private static async Task WaitUntilListeningAsync(DnsServer server)
    {
        for (var i = 0; i < 50 && !server.IsListening; i++)
            await Task.Delay(50);

        server.IsListening.Should().BeTrue("DNS server should start listening");
    }

    private static async Task<List<string>> QueryAsync(UdpClient client, ushort transactionId)
    {
        var query = BuildQuery(transactionId, "roundrobin.test");
        await client.SendAsync(query, query.Length);

        var response = (await client.ReceiveAsync()).Buffer;
        var header = DnsHeader.FromBytes(response);
        var reader = new DnsReader(response) { Position = DnsHeader.Size };

        for (var i = 0; i < header.QuestionCount; i++)
        {
            reader.ReadDomainName();
            reader.Skip(4);
        }

        var addresses = new List<string>();
        for (var i = 0; i < header.AnswerCount; i++)
        {
            reader.ReadDomainName();
            var type = (DnsRecordType)reader.ReadUInt16();
            reader.Skip(2 + 4);
            var rdLength = reader.ReadUInt16();
            var rdata = reader.ReadBytes(rdLength);

            if (type == DnsRecordType.A && rdLength == 4)
                addresses.Add(new IPAddress(rdata).ToString());
        }

        return addresses;
    }

    private static byte[] BuildQuery(ushort transactionId, string domain)
    {
        var message = new List<byte>
        {
            (byte)(transactionId >> 8),
            (byte)transactionId,
            0x01, 0x00,
            0x00, 0x01,
            0x00, 0x00,
            0x00, 0x00,
            0x00, 0x00
        };

        foreach (var label in domain.Split('.'))
        {
            message.Add((byte)label.Length);
            message.AddRange(Encoding.ASCII.GetBytes(label));
        }

        message.Add(0x00);
        message.Add(0x00);
        message.Add(0x01);
        message.Add(0x00);
        message.Add(0x01);

        return [.. message];
    }
}
