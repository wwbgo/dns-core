using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DnsCore.Services;

/// <summary>上游应答结果</summary>
public sealed record UpstreamResponse
{
    public required DnsResponseCode ResponseCode { get; init; }
    public required List<DnsRecord> Answers { get; init; }
    public bool HasAnswers => Answers.Count > 0;
}

/// <summary>
/// 上游 DNS 解析器。
///
/// 相比原实现修复了两个要害：
/// 1) 原来所有上游查询共享一个 UdpClient 且被信号量串行化，一个慢上游会把整机吞吐拖到个位数 QPS；
///    现在每次查询用独立的已 Connect 的 socket，可并行竞速。
/// 2) 原来 socket 未 Connect 且不校验 TXID/源地址/question，任何人都能投毒缓存，
///    高并发下还会把 A 的应答错配给 B；现在随机 TXID + 内核层源地址过滤 + 逐项校验。
/// </summary>
public sealed class UpstreamDnsResolver(
    ILogger<UpstreamDnsResolver> logger,
    DnsCache dnsCache,
    DnsServerOptions serverOptions) : IDisposable
{
    private volatile IPAddress[] _upstreamServers = [];
    private readonly SemaphoreSlim _concurrencyLimit =
        new(Math.Max(1, serverOptions.Upstream.MaxConcurrentQueries));

    private const int DnsPort = 53;

    private int TimeoutMs => Math.Max(200, serverOptions.Upstream.TimeoutMilliseconds);

    /// <summary>设置上游 DNS 服务器</summary>
    public void SetUpstreamServers(List<string> servers)
    {
        List<IPAddress> parsed = [];

        foreach (var server in servers?.Where(s => !string.IsNullOrWhiteSpace(s)) ?? [])
        {
            if (IPAddress.TryParse(server.Trim(), out var ip))
            {
                parsed.Add(ip);
                logger.LogInformation("已添加上游 DNS 服务器: {Server}", ip);
            }
            else
            {
                logger.LogWarning("无效的上游 DNS 服务器地址: {Server}", server);
            }
        }

        if (parsed.Count == 0)
            parsed.AddRange(LoadSystemDnsServers());

        _upstreamServers = [.. parsed.Distinct()];
    }

    /// <summary>
    /// 当前实际生效的上游地址。配置为空时这里是自动探测到的系统 DNS，
    /// 供管理界面显示"当前正在用哪些上游"。
    /// </summary>
    public IReadOnlyList<IPAddress> GetEffectiveServers() => _upstreamServers;

    /// <summary>
    /// 查询上游。缓存命中（含否定缓存）直接返回。
    /// </summary>
    public async Task<UpstreamResponse?> QueryAsync(
        string domain, DnsRecordType type, ushort classValue = 1, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        if (serverOptions.Cache.Enabled)
        {
            var cached = dnsCache.Get(domain, type, classValue);
            if (cached is not null)
            {
                logger.LogDebug("缓存命中: {Domain} {Type}", domain, type);
                return new UpstreamResponse { ResponseCode = cached.ResponseCode, Answers = cached.Records };
            }
        }

        var servers = _upstreamServers;
        if (servers.Length == 0)
        {
            logger.LogWarning("没有可用的上游 DNS 服务器");
            return null;
        }

        await _concurrencyLimit.WaitAsync(cancellationToken);
        try
        {
            var response = serverOptions.Upstream.RaceUpstreams && servers.Length > 1
                ? await RaceAsync(servers, domain, type, classValue, cancellationToken)
                : await SequentialAsync(servers, domain, type, classValue, cancellationToken);

            if (response is null)
            {
                logger.LogWarning("全部上游 DNS 查询失败: {Domain} {Type}", domain, type);
                return null;
            }

            CacheResponse(domain, type, classValue, response);
            return response;
        }
        finally
        {
            _concurrencyLimit.Release();
        }
    }

    private void CacheResponse(string domain, DnsRecordType type, ushort classValue, UpstreamResponse response)
    {
        if (!serverOptions.Cache.Enabled)
            return;

        if (response.HasAnswers)
        {
            dnsCache.Set(domain, type, response.Answers, classValue);
            return;
        }

        // NXDOMAIN / NODATA 也要缓存，否则不存在的域名每次都打上游
        if (response.ResponseCode is DnsResponseCode.NxDomain or DnsResponseCode.NoError)
            dnsCache.SetNegative(domain, type, response.ResponseCode, classValue);
    }

    /// <summary>并行竞速：取最先返回的成功应答</summary>
    private async Task<UpstreamResponse?> RaceAsync(
        IPAddress[] servers, string domain, DnsRecordType type, ushort classValue,
        CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tasks = servers
            .Select(server => QueryServerAsync(server, domain, type, classValue, raceCts.Token))
            .ToList();

        UpstreamResponse? fallback = null;

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            var result = await completed;
            if (result is null)
                continue;

            // 有答案或明确的 NXDOMAIN 即可采用，取消其余在飞的查询
            if (result.HasAnswers || result.ResponseCode == DnsResponseCode.NxDomain)
            {
                await raceCts.CancelAsync();
                return result;
            }

            fallback ??= result;
        }

        return fallback;
    }

    /// <summary>顺序查询：逐个尝试直到成功</summary>
    private async Task<UpstreamResponse?> SequentialAsync(
        IPAddress[] servers, string domain, DnsRecordType type, ushort classValue,
        CancellationToken cancellationToken)
    {
        UpstreamResponse? fallback = null;

        foreach (var server in servers)
        {
            var result = await QueryServerAsync(server, domain, type, classValue, cancellationToken);
            if (result is null)
                continue;

            if (result.HasAnswers || result.ResponseCode == DnsResponseCode.NxDomain)
                return result;

            fallback ??= result;
        }

        return fallback;
    }

    /// <summary>
    /// 查询单个上游服务器。使用独立且 Connect 过的 socket：
    /// 内核只投递来自该地址的包，源地址伪造在协议栈层就被挡掉。
    /// </summary>
    private async Task<UpstreamResponse?> QueryServerAsync(
        IPAddress server, string domain, DnsRecordType type, ushort classValue,
        CancellationToken cancellationToken)
    {
        // 每次查询使用新的随机 TXID，绝不复用客户端的 TXID
        var transactionId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);

        try
        {
            var queryData = BuildQuery(transactionId, domain, type, classValue);

            using var udpClient = new UdpClient(server.AddressFamily);
            udpClient.Connect(new IPEndPoint(server, DnsPort));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeoutMs);

            await udpClient.SendAsync(queryData, timeoutCts.Token);

            // 可能收到伪造/迟到的包，需循环直到拿到匹配的应答或超时
            while (!timeoutCts.IsCancellationRequested)
            {
                var result = await udpClient.ReceiveAsync(timeoutCts.Token);

                var response = ValidateAndParse(
                    result.Buffer, transactionId, domain, type, classValue, server);

                if (response is not null)
                    return response;

                logger.LogDebug("丢弃与查询不匹配的上游应答: {Server} {Domain} {Type}", server, domain, type);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("上游 DNS 查询超时: {Server} {Domain} {Type}", server, domain, type);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询上游 DNS 服务器失败: {Server}", server);
            return null;
        }
    }

    /// <summary>
    /// 构建上游查询报文。不再转发客户端原始报文：
    /// 客户端 TXID 不应外泄，且原报文可能带有不该转发的 EDNS 选项。
    /// </summary>
    private static byte[] BuildQuery(ushort transactionId, string domain, DnsRecordType type, ushort classValue)
    {
        var header = new DnsHeader
        {
            TransactionId = transactionId,
            Flags = 0x0100, // 标准查询 + RD
            QuestionCount = 1
        };

        var writer = new DnsWriter(64);
        writer.WriteHeader(header);
        writer.WriteDomainName(domain, useCompression: false);
        writer.WriteUInt16((ushort)type);
        writer.WriteUInt16(classValue);

        return writer.ToArray();
    }

    /// <summary>
    /// 校验应答是否真正对应本次查询，再解析。
    /// 原实现完全不做这些校验，构成标准的缓存投毒面。
    /// </summary>
    private UpstreamResponse? ValidateAndParse(
        byte[] responseData, ushort expectedId, string expectedDomain,
        DnsRecordType expectedType, ushort expectedClass, IPAddress server)
    {
        try
        {
            var header = DnsHeader.FromBytes(responseData);

            if (header.TransactionId != expectedId || !header.IsResponse)
                return null;

            var reader = new DnsReader(responseData) { Position = DnsHeader.Size };

            // question 必须与我们发出的一致
            if (header.QuestionCount != 1)
                return null;

            var questionName = reader.ReadDomainName();
            var questionType = (DnsRecordType)reader.ReadUInt16();
            var questionClass = reader.ReadUInt16();

            if (questionType != expectedType
                || questionClass != expectedClass
                || !questionName.Equals(expectedDomain.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                return null;

            // 被截断的 UDP 应答不可信，交由上层按失败处理
            if (header.IsTruncated)
            {
                logger.LogDebug("上游应答被截断: {Server} {Domain}", server, expectedDomain);
                return null;
            }

            List<DnsRecord> records = [];

            for (var i = 0; i < header.AnswerCount; i++)
            {
                var record = ReadResourceRecord(ref reader);
                if (record is not null)
                    records.Add(record);
            }

            return new UpstreamResponse
            {
                ResponseCode = header.ResponseCode,
                Answers = records
            };
        }
        catch (InvalidDataException ex)
        {
            logger.LogDebug(ex, "上游 DNS 应答格式非法: {Server}", server);
            return null;
        }
    }

    /// <summary>读取一条资源记录，未知类型返回 null 并正确跳过</summary>
    private static DnsRecord? ReadResourceRecord(ref DnsReader reader)
    {
        var name = reader.ReadDomainName();
        var type = (DnsRecordType)reader.ReadUInt16();
        var classValue = reader.ReadUInt16();
        var ttl = reader.ReadUInt32();
        var rdLength = reader.ReadUInt16();

        var rdataEnd = reader.Position + rdLength;

        string? value = null;

        switch (type)
        {
            case DnsRecordType.A when rdLength == 4:
                value = new IPAddress(reader.ReadBytes(4).ToArray()).ToString();
                break;

            case DnsRecordType.AAAA when rdLength == 16:
                value = new IPAddress(reader.ReadBytes(16).ToArray()).ToString();
                break;

            case DnsRecordType.CNAME:
            case DnsRecordType.NS:
            case DnsRecordType.PTR:
                value = reader.ReadDomainName();
                break;

            case DnsRecordType.TXT:
                value = ReadTxt(ref reader, rdLength);
                break;

            case DnsRecordType.MX:
            {
                var preference = reader.ReadUInt16();
                value = $"{preference} {reader.ReadDomainName()}";
                break;
            }

            case DnsRecordType.SRV:
            {
                var priority = reader.ReadUInt16();
                var weight = reader.ReadUInt16();
                var port = reader.ReadUInt16();
                value = $"{priority} {weight} {port} {reader.ReadDomainName()}";
                break;
            }
        }

        // 无论上面是否读取，都以 RDLENGTH 为准定位到下一条记录，
        // 避免某类型解析长度与声明不一致时整个应答错位
        reader.Position = rdataEnd;

        if (value is null)
            return null;

        return new DnsRecord
        {
            Domain = name,
            Type = type,
            Value = value,
            // TTL 上限夹到 int 范围，防止 uint 转 int 溢出成负数
            TTL = (int)Math.Min(ttl, int.MaxValue)
        };
    }

    /// <summary>TXT 由多个长度前缀的分片组成，需全部拼接</summary>
    private static string ReadTxt(ref DnsReader reader, int rdLength)
    {
        var end = reader.Position + rdLength;
        var parts = new List<string>();

        while (reader.Position < end)
        {
            var chunkLength = reader.ReadByte();
            if (chunkLength == 0 || reader.Position + chunkLength > end)
                break;

            parts.Add(System.Text.Encoding.UTF8.GetString(reader.ReadBytes(chunkLength)));
        }

        return string.Concat(parts);
    }

    /// <summary>加载系统 DNS 服务器</summary>
    private List<IPAddress> LoadSystemDnsServers()
    {
        List<IPAddress> result = [];

        try
        {
            result.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(iface => iface.OperationalStatus == OperationalStatus.Up)
                .SelectMany(iface => iface.GetIPProperties().DnsAddresses)
                // 排除本机地址，否则会把查询转回自己形成环
                .Where(ip => !IPAddress.IsLoopback(ip))
                .Distinct());

            if (result.Count > 0)
            {
                logger.LogInformation("使用系统 DNS 服务器: {Servers}", string.Join(", ", result));
                return result;
            }

            result.AddRange([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")]);
            logger.LogInformation("使用默认公共 DNS 服务器: 8.8.8.8, 1.1.1.1");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载系统 DNS 服务器失败");
        }

        return result;
    }

    public void Dispose() => _concurrencyLimit.Dispose();
}
