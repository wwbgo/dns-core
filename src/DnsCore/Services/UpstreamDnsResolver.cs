using DnsCore.Models;
using DnsCore.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace DnsCore.Services;

/// <summary>
/// 上游 DNS 解析器
/// </summary>
public sealed class UpstreamDnsResolver(ILogger<UpstreamDnsResolver> logger)
{
    private readonly List<IPAddress> _upstreamServers = [];
    private const int Timeout = 5000; // 5秒超时
    private const int DnsPort = 53;

    /// <summary>
    /// 设置上游 DNS 服务器
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
                logger.LogInformation("添加上游 DNS 服务器: {Server}", server);
            }
            else
            {
                logger.LogWarning("无效的上游 DNS 服务器地址: {Server}", server);
            }
        }

        if (_upstreamServers.Count == 0)
        {
            LoadSystemDnsServers();
        }
    }

    /// <summary>
    /// 查询上游 DNS 服务器
    /// </summary>
    public async Task<List<DnsRecord>?> QueryAsync(string domain, DnsRecordType type, byte[] queryData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(queryData);

        if (_upstreamServers.Count == 0)
        {
            logger.LogWarning("没有可用的上游 DNS 服务器");
            return null;
        }

        // 尝试每个上游 DNS 服务器
        foreach (var server in _upstreamServers)
        {
            try
            {
                logger.LogDebug("查询上游 DNS 服务器: {Server} - {Domain} {Type}", server, domain, type);

                var response = await QueryServerAsync(server, queryData);
                if (response is { Count: > 0 })
                {
                    logger.LogInformation("从上游 DNS 服务器获取到响应: {Server} - {Domain} {Type}", server, domain, type);
                    return response;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "查询上游 DNS 服务器失败: {Server}", server);
            }
        }

        logger.LogWarning("所有上游 DNS 服务器查询失败: {Domain} {Type}", domain, type);
        return null;
    }

    /// <summary>
    /// 查询单个 DNS 服务器
    /// </summary>
    private async Task<List<DnsRecord>?> QueryServerAsync(IPAddress server, byte[] queryData)
    {
        using var udpClient = new UdpClient();
        udpClient.Client.ReceiveTimeout = Timeout;
        udpClient.Client.SendTimeout = Timeout;

        var endpoint = new IPEndPoint(server, DnsPort);

        // 发送查询
        await udpClient.SendAsync(queryData, endpoint);

        // 接收响应
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            var result = await udpClient.ReceiveAsync(cts.Token);
            return ParseUpstreamResponse(result.Buffer);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("查询上游 DNS 超时: {Server}", server);
            return null;
        }
    }

    /// <summary>
    /// 解析上游 DNS 响应
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

            // 跳过问题部分
            for (var i = 0; i < header.QuestionCount; i++)
            {
                offset = SkipQuestion(responseData, offset);
            }

            // 读取答案部分
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
            logger.LogError(ex, "解析上游 DNS 响应失败");
            return null;
        }
    }

    private static int SkipQuestion(byte[] data, int offset)
    {
        // 跳过域名
        offset = SkipDomainName(data, offset);
        // 跳过 Type 和 Class（各 2 字节）
        return offset + 4;
    }

    private (DnsRecord? record, int offset) ParseResourceRecord(byte[] data, int offset)
    {
        // 读取域名
        (var name, offset) = ReadDomainName(data, offset);

        // 读取 Type, Class, TTL, Data Length
        var type = (DnsRecordType)ReadUInt16(data, offset);
        offset += 2;

        var classValue = ReadUInt16(data, offset);
        offset += 2;

        var ttl = ReadUInt32(data, offset);
        offset += 4;

        var dataLength = ReadUInt16(data, offset);
        offset += 2;

        // 读取数据
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
            if (jumps++ > maxJumps)
                throw new InvalidDataException("DNS 消息压缩过多");

            var length = data[offset];

            // 压缩指针
            if ((length & 0xC0) == 0xC0)
            {
                if (!jumped)
                    jumpOffset = offset + 2;

                var pointer = ((length & 0x3F) << 8) | data[offset + 1];
                offset = pointer;
                jumped = true;
                continue;
            }

            // 域名结束
            if (length == 0)
            {
                offset++;
                break;
            }

            // 读取标签
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
    /// 加载系统 DNS 服务器
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
                logger.LogInformation("使用系统 DNS 服务器: {Servers}",
                    string.Join(", ", _upstreamServers));
                return;
            }

            // 如果没有系统 DNS，使用公共 DNS
            _upstreamServers.AddRange([
                IPAddress.Parse("8.8.8.8"),    // Google DNS
                IPAddress.Parse("1.1.1.1")     // Cloudflare DNS
            ]);

            logger.LogInformation("使用默认公共 DNS 服务器: 8.8.8.8, 1.1.1.1");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载系统 DNS 服务器失败");
        }
    }
}
