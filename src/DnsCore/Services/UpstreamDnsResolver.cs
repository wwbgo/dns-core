using DnsCore.Models;
using DnsCore.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace DnsCore.Services;

/// <summary>
/// Upstream DNS resolver（性能优化：添加缓存 + 复用 UdpClient）
/// </summary>
public sealed class UpstreamDnsResolver(
    ILogger<UpstreamDnsResolver> logger,
    DnsCache dnsCache) : IDisposable
{
    private readonly List<IPAddress> _upstreamServers = [];
    private readonly SemaphoreSlim _udpClientSemaphore = new(1, 1);
    private UdpClient? _sharedUdpClient;
    private const int Timeout = 5000; // 5 seconds timeout
    private const int DnsPort = 53;

    /// <summary>
    /// Set upstream DNS servers
    /// </summary>
    public void SetUpstreamServers(List<string> servers)
    {
        _upstreamServers.Clear();

        if (servers is not { Count: > 0 })
        {
            LoadSystemDnsServers();
            return;
        }

        foreach (var server in servers.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (IPAddress.TryParse(server, out var ip))
            {
                _upstreamServers.Add(ip);
                logger.LogInformation("Added upstream DNS server: {Server}", server);
            }
            else
            {
                logger.LogWarning("Invalid upstream DNS server address: {Server}", server);
            }
        }

        if (_upstreamServers.Count == 0)
        {
            LoadSystemDnsServers();
        }
    }

    /// <summary>
    /// Query upstream DNS servers（性能优化：先查缓存）
    /// </summary>
    public async Task<List<DnsRecord>?> QueryAsync(string domain, DnsRecordType type, byte[] queryData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(queryData);

        // 1. 先查询缓存
        var cachedResult = dnsCache.Get(domain, type);
        if (cachedResult is not null)
        {
            return cachedResult;
        }

        if (_upstreamServers.Count == 0)
        {
            logger.LogWarning("No available upstream DNS servers");
            return null;
        }

        // 2. 缓存未命中，查询上游服务器
        foreach (var server in _upstreamServers)
        {
            try
            {
                logger.LogDebug("Querying upstream DNS server: {Server} - {Domain} {Type}", server, domain, type);

                var response = await QueryServerAsync(server, queryData);
                if (response is { Count: > 0 })
                {
                    logger.LogInformation("Received response from upstream DNS server: {Server} - {Domain} {Type}", server, domain, type);

                    // 3. 缓存查询结果
                    dnsCache.Set(domain, type, response);

                    return response;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to query upstream DNS server: {Server}", server);
            }
        }

        logger.LogWarning("All upstream DNS server queries failed: {Domain} {Type}", domain, type);
        return null;
    }

    /// <summary>
    /// Query a single DNS server（性能优化：复用 UdpClient）
    /// </summary>
    private async Task<List<DnsRecord>?> QueryServerAsync(IPAddress server, byte[] queryData)
    {
        await _udpClientSemaphore.WaitAsync();
        try
        {
            // 延迟初始化 UdpClient
            _sharedUdpClient ??= new UdpClient();
            _sharedUdpClient.Client.ReceiveTimeout = Timeout;
            _sharedUdpClient.Client.SendTimeout = Timeout;

            var endpoint = new IPEndPoint(server, DnsPort);

            // Send query
            await _sharedUdpClient.SendAsync(queryData, endpoint);

            // Receive response
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                var result = await _sharedUdpClient.ReceiveAsync(cts.Token);
                return ParseUpstreamResponse(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Upstream DNS query timeout: {Server}", server);
                return null;
            }
        }
        finally
        {
            _udpClientSemaphore.Release();
        }
    }

    /// <summary>
    /// Parse upstream DNS response
    /// </summary>
    private List<DnsRecord>? ParseUpstreamResponse(byte[] responseData)
    {
        try
        {
            var header = DnsHeader.FromBytes(responseData, 0);

            if (!header.IsResponse || header.AnswerCount == 0)
            {
                return null;
            }

            var records = new List<DnsRecord>();
            var offset = 12;

            // Skip question section
            for (var i = 0; i < header.QuestionCount; i++)
            {
                offset = SkipQuestion(responseData, offset);
            }

            // Read answer section
            for (var i = 0; i < header.AnswerCount; i++)
            {
                (var record, offset) = ParseResourceRecord(responseData, offset);
                if (record is not null)
                {
                    records.Add(record);
                }
            }

            return records;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse upstream DNS response");
            return null;
        }
    }

    private static int SkipQuestion(byte[] data, int offset)
    {
        // Skip domain name
        offset = SkipDomainName(data, offset);
        // Skip Type and Class (2 bytes each)
        return offset + 4;
    }

    private (DnsRecord? record, int offset) ParseResourceRecord(byte[] data, int offset)
    {
        // Read domain name
        (var name, offset) = ReadDomainName(data, offset);

        // Read Type, Class, TTL, Data Length
        var type = (DnsRecordType)ReadUInt16(data, offset);
        offset += 2;

        var classValue = ReadUInt16(data, offset);
        offset += 2;

        var ttl = ReadUInt32(data, offset);
        offset += 4;

        var dataLength = ReadUInt16(data, offset);
        offset += 2;

        // Read data
        var value = type switch
        {
            DnsRecordType.A => ParseIPv4Data(data, offset),
            DnsRecordType.AAAA => ParseIPv6Data(data, offset),
            DnsRecordType.CNAME or DnsRecordType.NS or DnsRecordType.PTR => ReadDomainName(data, offset).name,
            DnsRecordType.TXT => ParseTxtData(data, offset, dataLength),
            _ => Convert.ToHexString(data, offset, dataLength)
        };

        offset += dataLength;

        var record = new DnsRecord
        {
            Domain = name,
            Type = type,
            Value = value,
            TTL = (int)ttl
        };

        return (record, offset);
    }

    private static int SkipDomainName(byte[] data, int offset)
    {
        while (true)
        {
            var length = data[offset];

            if ((length & 0xC0) == 0xC0)
                return offset + 2;

            if (length == 0)
                return offset + 1;

            offset += length + 1;
        }
    }

    private static (string name, int offset) ReadDomainName(byte[] data, int offset)
    {
        List<string> labels = [];
        var jumped = false;
        var jumpOffset = offset;
        const int maxJumps = 5;
        var jumps = 0;

        while (true)
        {
            var length = data[offset];

            // Compression pointer
            if ((length & 0xC0) == 0xC0)
            {
                if (jumps++ > maxJumps)
                    throw new InvalidDataException("Too many DNS message compressions");

                if (!jumped)
                    jumpOffset = offset + 2;

                var pointer = ((length & 0x3F) << 8) | data[offset + 1];
                offset = pointer;
                jumped = true;
                continue;
            }

            // Domain name end
            if (length == 0)
            {
                offset++;
                break;
            }

            // Read label
            offset++;
            var label = System.Text.Encoding.ASCII.GetString(data, offset, length);
            labels.Add(label);
            offset += length;
        }

        return (string.Join('.', labels), jumped ? jumpOffset : offset);
    }

    private static string ParseIPv4Data(byte[] data, int offset) =>
        $"{data[offset]}.{data[offset + 1]}.{data[offset + 2]}.{data[offset + 3]}";

    private static string ParseIPv6Data(byte[] data, int offset)
    {
        var bytes = new byte[16];
        Array.Copy(data, offset, bytes, 0, 16);
        return new IPAddress(bytes).ToString();
    }

    private static string ParseTxtData(byte[] data, int offset, int length)
    {
        var txtLength = data[offset];
        return System.Text.Encoding.UTF8.GetString(data, offset + 1, Math.Min(txtLength, length - 1));
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    /// <summary>
    /// Load system DNS servers
    /// </summary>
    private void LoadSystemDnsServers()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            var dnsAddresses = interfaces
                .Where(iface => iface.OperationalStatus == OperationalStatus.Up)
                .SelectMany(iface => iface.GetIPProperties().DnsAddresses)
                .Distinct()
                .ToList();

            _upstreamServers.AddRange(dnsAddresses);

            if (_upstreamServers.Count > 0)
            {
                logger.LogInformation("Using system DNS servers: {Servers}",
                    string.Join(", ", _upstreamServers));
                return;
            }

            // If no system DNS, use public DNS
            _upstreamServers.AddRange([
                IPAddress.Parse("8.8.8.8"),    // Google DNS
                IPAddress.Parse("1.1.1.1")     // Cloudflare DNS
            ]);

            logger.LogInformation("Using default public DNS servers: 8.8.8.8, 1.1.1.1");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load system DNS servers");
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _sharedUdpClient?.Dispose();
        _udpClientSemaphore.Dispose();
    }
}
