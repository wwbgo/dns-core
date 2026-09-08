using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DnsCore.Services;

/// <summary>上游应答结果</summary>
public sealed record UpstreamResponse
{
    public required DnsResponseCode ResponseCode { get; init; }
    public required List<DnsRecord> Answers { get; init; }
    public bool HasAnswers => Answers.Count > 0;
}

internal sealed record UpstreamAttempt(UpstreamResponse? Response, string? Error);

internal sealed record UpstreamBatchResult(
    UpstreamResponse? Response,
    IReadOnlyList<string> Errors);

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
    private readonly ConcurrentDictionary<IPAddress, UpstreamSocketPool> _socketPools = new();
    private readonly SemaphoreSlim _concurrencyLimit =
        new(Math.Max(1, serverOptions.Upstream.MaxConcurrentQueries));

    private const int DnsPort = 53;
    private const int MaxPooledSocketsPerUpstream = 64;

    // EDNS0 可避免多数应答在传统 512 字节限制下被截断。
    private const int UpstreamUdpPayloadSize = 1232;

    // Docker 内置 DNS 监听在 127.0.0.11。它不是本服务自身，过滤掉会让容器
    // 错误地回落到 8.8.8.8/1.1.1.1，在无法访问公网 DNS 的环境中表现为全部上游失败。
    private static readonly IPAddress DockerEmbeddedDns = IPAddress.Parse("127.0.0.11");
    private static readonly bool RunningInContainer =
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase)
        || (OperatingSystem.IsLinux() && File.Exists("/.dockerenv"));

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
                logger.LogInformation("Added upstream DNS server: {Server}", ip);
            }
            else
            {
                logger.LogWarning("Invalid upstream DNS server address: {Server}", server);
            }
        }

        if (parsed.Count == 0)
            parsed.AddRange(LoadSystemDnsServers());

        IPAddress[] effective = [.. parsed.Distinct()];
        var previousServers = _socketPools.Keys.ToArray();
        _upstreamServers = effective;

        // 关闭已不在生效列表中的 socket 池。Close 只清理空闲 socket，
        // 在飞查询仍持有各自租约，返回或废弃时再释放。
        var active = new HashSet<IPAddress>(effective);
        foreach (var server in previousServers)
        {
            if (!active.Contains(server) && _socketPools.TryRemove(server, out var pool))
                pool.Close();
        }
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
                logger.LogDebug("Cache hit: {Domain} {Type}", domain, type);
                return new UpstreamResponse { ResponseCode = cached.ResponseCode, Answers = cached.Records };
            }
        }

        var servers = _upstreamServers;
        if (servers.Length == 0)
        {
            logger.LogWarning("No upstream DNS servers are available");
            return null;
        }

        await _concurrencyLimit.WaitAsync(cancellationToken);
        try
        {
            var result = serverOptions.Upstream.RaceUpstreams && servers.Length > 1
                ? await RaceAsync(servers, domain, type, classValue, cancellationToken)
                : await SequentialAsync(servers, domain, type, classValue, cancellationToken);

            if (result.Response is null)
            {
                var reasons = result.Errors.Distinct().Take(5);
                logger.LogWarning(
                    "All upstream DNS queries failed: {Domain} {Type}. Reasons: {Reasons}",
                    domain,
                    type,
                    string.Join(" | ", reasons));
                return null;
            }

            CacheResponse(domain, type, classValue, result.Response);
            return result.Response;
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
    private async Task<UpstreamBatchResult> RaceAsync(
        IPAddress[] servers,
        string domain,
        DnsRecordType type,
        ushort classValue,
        CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tasks = servers
            .Select(server => QueryServerAsync(server, domain, type, classValue, raceCts.Token))
            .ToList();

        UpstreamResponse? fallback = null;
        List<string> errors = [];

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            var attempt = await completed;
            if (attempt.Response is null)
            {
                if (!raceCts.IsCancellationRequested && attempt.Error is not null)
                    errors.Add(attempt.Error);
                continue;
            }

            // 有答案或明确的 NXDOMAIN 即可采用，取消其余在飞的查询
            if (attempt.Response.HasAnswers || attempt.Response.ResponseCode == DnsResponseCode.NxDomain)
            {
                await raceCts.CancelAsync();
                return new UpstreamBatchResult(attempt.Response, errors);
            }

            fallback ??= attempt.Response;
        }

        return new UpstreamBatchResult(fallback, errors);
    }

    /// <summary>顺序查询：逐个尝试直到成功</summary>
    private async Task<UpstreamBatchResult> SequentialAsync(
        IPAddress[] servers,
        string domain,
        DnsRecordType type,
        ushort classValue,
        CancellationToken cancellationToken)
    {
        UpstreamResponse? fallback = null;
        List<string> errors = [];

        foreach (var server in servers)
        {
            var attempt = await QueryServerAsync(server, domain, type, classValue, cancellationToken);
            if (attempt.Response is null)
            {
                if (attempt.Error is not null)
                    errors.Add(attempt.Error);
                continue;
            }

            if (attempt.Response.HasAnswers || attempt.Response.ResponseCode == DnsResponseCode.NxDomain)
                return new UpstreamBatchResult(attempt.Response, errors);

            fallback ??= attempt.Response;
        }

        return new UpstreamBatchResult(fallback, errors);
    }

    /// <summary>
    /// 查询单个上游服务器。使用独立且 Connect 过的 socket：
    /// 内核只投递来自该地址的包，源地址伪造在协议栈层就被挡掉。
    /// </summary>
    private async Task<UpstreamAttempt> QueryServerAsync(
        IPAddress server,
        string domain,
        DnsRecordType type,
        ushort classValue,
        CancellationToken cancellationToken)
    {
        // 每次查询使用新的随机 TXID，绝不复用客户端的 TXID
        var transactionId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeoutMs);

        var pool = GetSocketPool(server);
        UpstreamSocketLease lease;

        try
        {
            lease = await pool.RentAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Upstream DNS query timed out: {Server} {Domain} {Type}", server, domain, type);
            return new UpstreamAttempt(null, $"{server}: socket rent timeout");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to rent upstream socket: {Server}", server);
            return new UpstreamAttempt(null, $"{server}: socket rent failed ({ex.Message})");
        }

        var returnToPool = false;
        var includeEdns = true;

        try
        {
            try
            {
                var queryData = BuildQuery(transactionId, domain, type, classValue, includeEdns);
                var udpClient = lease.Client;

                await udpClient.SendAsync(queryData, timeoutCts.Token);

                // 可能收到伪造/迟到的包，需循环直到拿到匹配的应答或超时
                while (!timeoutCts.IsCancellationRequested)
                {
                    var result = await udpClient.ReceiveAsync(timeoutCts.Token);

                    var response = ValidateAndParse(
                        result.Buffer,
                        transactionId,
                        domain,
                        type,
                        classValue,
                        server,
                        out var truncated);

                    if (response is not null
                        && includeEdns
                        && response.ResponseCode is DnsResponseCode.FormErr or DnsResponseCode.NotImp)
                    {
                        includeEdns = false;
                        queryData = BuildQuery(transactionId, domain, type, classValue, includeEdns: false);
                        await udpClient.SendAsync(queryData, timeoutCts.Token);
                        continue;
                    }

                    if (response is not null)
                    {
                        returnToPool = true;
                        return new UpstreamAttempt(response, null);
                    }

                    if (truncated)
                    {
                        lease.Discard();
                        returnToPool = false;
                        return await QueryServerOverTcpAsync(
                            server,
                            transactionId,
                            domain,
                            type,
                            classValue,
                            timeoutCts.Token);
                    }

                    logger.LogDebug("Discarded upstream response that did not match the query: {Server} {Domain} {Type}", server, domain, type);
                }

                return new UpstreamAttempt(null, $"{server}: UDP query timed out");
            }
            finally
            {
                if (returnToPool)
                    lease.Return();
                else
                    lease.Discard();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Upstream DNS query timed out: {Server} {Domain} {Type}", server, domain, type);
            return new UpstreamAttempt(null, $"{server}: UDP query timeout");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query upstream DNS server: {Server}", server);
            return new UpstreamAttempt(null, $"{server}: UDP query failed ({ex.Message})");
        }
    }

    /// <summary>
    /// UDP 应答设置了 TC 位时，按 RFC 1035 回退到 TCP 重新查询。
    /// </summary>
    private async Task<UpstreamAttempt> QueryServerOverTcpAsync(
        IPAddress server,
        ushort transactionId,
        string domain,
        DnsRecordType type,
        ushort classValue,
        CancellationToken cancellationToken)
    {
        using var tcpClient = new TcpClient(server.AddressFamily);

        try
        {
            await tcpClient.ConnectAsync(new IPEndPoint(server, DnsPort), cancellationToken);

            var stream = tcpClient.GetStream();
            var queryData = BuildQuery(transactionId, domain, type, classValue);
            var lengthPrefix = new byte[]
            {
                (byte)(queryData.Length >> 8),
                (byte)(queryData.Length & 0xFF)
            };

            await stream.WriteAsync(lengthPrefix, cancellationToken);
            await stream.WriteAsync(queryData, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var responseLengthBytes = new byte[2];
            await ReadExactlyAsync(stream, responseLengthBytes, cancellationToken);

            var responseLength = (responseLengthBytes[0] << 8) | responseLengthBytes[1];
            if (responseLength < DnsHeader.Size || responseLength > DnsLimits.MaxMessageSize)
            {
                logger.LogDebug("Invalid TCP DNS response length from upstream {Server}: {Length}", server, responseLength);
                return new UpstreamAttempt(null, $"{server}: invalid TCP response length ({responseLength})");
            }

            var responseData = new byte[responseLength];
            await ReadExactlyAsync(stream, responseData, cancellationToken);

            var response = ValidateAndParse(
                responseData,
                transactionId,
                domain,
                type,
                classValue,
                server,
                out _);

            return response is null
                ? new UpstreamAttempt(null, $"{server}: invalid TCP DNS response")
                : new UpstreamAttempt(response, null);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Upstream DNS over TCP timed out: {Server} {Domain} {Type}", server, domain, type);
            return new UpstreamAttempt(null, $"{server}: TCP fallback timeout");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream DNS over TCP query failed: {Server}", server);
            return new UpstreamAttempt(null, $"{server}: TCP fallback failed ({ex.Message})");
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();

            total += read;
        }
    }

    /// <summary>
    /// 构建上游查询报文。不再转发客户端原始报文：
    /// 客户端 TXID 不应外泄，且原报文可能带有不该转发的 EDNS 选项。
    /// </summary>
    internal static byte[] BuildQuery(
        ushort transactionId,
        string domain,
        DnsRecordType type,
        ushort classValue,
        bool includeEdns = true)
    {
        // 上游查询使用栈上缓冲直接编码，不创建 DnsWriter 与压缩字典。
        Span<byte> buffer = stackalloc byte[512];

        var header = new DnsHeader
        {
            TransactionId = transactionId,
            Flags = 0x0100, // 标准查询 + RD
            QuestionCount = 1,
            AdditionalCount = includeEdns ? (ushort)1 : (ushort)0
        };

        header.WriteTo(buffer);
        var position = DnsHeader.Size;
        WriteQueryDomain(buffer, ref position, domain);

        buffer[position++] = (byte)((ushort)type >> 8);
        buffer[position++] = (byte)((ushort)type & 0xFF);
        buffer[position++] = (byte)(classValue >> 8);
        buffer[position++] = (byte)(classValue & 0xFF);

        if (includeEdns)
        {
            // OPT pseudo-record: NAME=0, TYPE=41, CLASS=UDP payload size, TTL=0, RDLEN=0
            buffer[position++] = 0;
            buffer[position++] = 0;
            buffer[position++] = 41;
            buffer[position++] = (byte)(UpstreamUdpPayloadSize >> 8);
            buffer[position++] = (byte)(UpstreamUdpPayloadSize & 0xFF);
            buffer[position++] = 0;
            buffer[position++] = 0;
            buffer[position++] = 0;
            buffer[position++] = 0;
            buffer[position++] = 0;
            buffer[position++] = 0;
        }

        return buffer[..position].ToArray();
    }

    private static void WriteQueryDomain(Span<byte> buffer, ref int position, string domain)
    {
        var remaining = domain.TrimEnd('.').AsSpan();
        Span<byte> labelBytes = stackalloc byte[DnsLimits.MaxLabelLength];

        while (!remaining.IsEmpty)
        {
            var dot = remaining.IndexOf('.');
            var label = dot < 0 ? remaining : remaining[..dot];
            var labelLength = Encoding.ASCII.GetBytes(label, labelBytes);

            buffer[position++] = (byte)labelLength;
            labelBytes[..labelLength].CopyTo(buffer[position..]);
            position += labelLength;

            if (dot < 0)
                break;

            remaining = remaining[(dot + 1)..];
        }

        buffer[position++] = 0;
    }

    /// <summary>
    /// 校验应答是否真正对应本次查询，再解析。
    /// 原实现完全不做这些校验，构成标准的缓存投毒面。
    /// </summary>
    private UpstreamResponse? ValidateAndParse(
        byte[] responseData,
        ushort expectedId,
        string expectedDomain,
        DnsRecordType expectedType,
        ushort expectedClass,
        IPAddress server,
        out bool truncated)
    {
        truncated = false;

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

            // UDP 截断应答由调用方回退到 TCP 查询。
            if (header.IsTruncated)
            {
                truncated = true;
                logger.LogDebug("Upstream response was truncated: {Server} {Domain}", server, expectedDomain);
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
            logger.LogDebug(ex, "Invalid upstream DNS response format: {Server}", server);
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
            {
                var ipv4 = reader.ReadBytes(4);
                value = $"{ipv4[0]}.{ipv4[1]}.{ipv4[2]}.{ipv4[3]}";
                break;
            }

            case DnsRecordType.AAAA when rdLength == 16:
                value = new IPAddress(reader.ReadBytes(16)).ToString();
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

    internal static bool IsDockerEmbeddedDns(IPAddress ip, bool runningInContainer)
        => runningInContainer && ip.Equals(DockerEmbeddedDns);

    /// <summary>加载系统 DNS 服务器</summary>
    private List<IPAddress> LoadSystemDnsServers()
    {
        List<IPAddress> result = [];

        try
        {
            result.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(iface => iface.OperationalStatus == OperationalStatus.Up)
                .SelectMany(iface => iface.GetIPProperties().DnsAddresses)
                // 排除会把查询转回自己的回环地址；容器内的 Docker 内置 DNS 是例外。
                .Where(ip => !IPAddress.IsLoopback(ip) || IsDockerEmbeddedDns(ip, RunningInContainer))
                .Distinct());

            if (result.Count > 0)
            {
                logger.LogInformation("Using system DNS servers: {Servers}", string.Join(", ", result));
                return result;
            }

            result.AddRange([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")]);
            logger.LogInformation("Using default public DNS servers: 8.8.8.8, 1.1.1.1");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load system DNS servers");
        }

        return result;
    }

    private UpstreamSocketPool GetSocketPool(IPAddress server)
        => _socketPools.GetOrAdd(
            server,
            address => new UpstreamSocketPool(
                address,
                Math.Clamp(serverOptions.Upstream.MaxConcurrentQueries, 1, MaxPooledSocketsPerUpstream)));

    public void Dispose()
    {
        foreach (var pool in _socketPools.Values)
            pool.Close();

        _socketPools.Clear();
        _concurrencyLimit.Dispose();
    }

    internal sealed class UpstreamSocketLease(UpstreamSocketPool pool, UdpClient client)
    {
        private readonly UdpClient _client = client;
        private int _returned;

        public UdpClient Client => _client;

        public void Return()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                pool.Return(_client);
        }

        public void Discard()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                pool.Discard(_client);
        }
    }

    internal sealed class UpstreamSocketPool(IPAddress server, int capacity)
    {
        private readonly ConcurrentQueue<UdpClient> _idle = new();
        private readonly SemaphoreSlim _slots = new(Math.Max(1, capacity), Math.Max(1, capacity));
        private int _closed;
        private int _activeLeases;
        private int _gateDisposed;

        public async Task<UpstreamSocketLease> RentAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _closed) != 0)
                throw new OperationCanceledException();

            try
            {
                await _slots.WaitAsync(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException();
            }

            Interlocked.Increment(ref _activeLeases);

            try
            {
                if (Volatile.Read(ref _closed) != 0)
                    throw new OperationCanceledException();

                if (_idle.TryDequeue(out var client))
                {
                    if (Volatile.Read(ref _closed) != 0)
                    {
                        client.Dispose();
                        throw new OperationCanceledException();
                    }

                    return new UpstreamSocketLease(this, client);
                }

                var created = CreateConnectedClient();
                if (Volatile.Read(ref _closed) != 0)
                {
                    created.Dispose();
                    throw new OperationCanceledException();
                }

                return new UpstreamSocketLease(this, created);
            }
            catch
            {
                _slots.Release();
                LeaseReturned();
                throw;
            }
        }

        public void Return(UdpClient client)
        {
            if (Volatile.Read(ref _closed) == 0)
            {
                _idle.Enqueue(client);
                _slots.Release();
            }
            else
            {
                client.Dispose();
                _slots.Release();
            }

            LeaseReturned();
        }

        public void Discard(UdpClient client)
        {
            client.Dispose();
            _slots.Release();
            LeaseReturned();
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            while (_idle.TryDequeue(out var client))
            {
                // 每个 idle socket 在 Return 时已经释放过对应的 _slots 许可。
                client.Dispose();
            }

            TryDisposeGate();
        }

        private void LeaseReturned()
        {
            if (Interlocked.Decrement(ref _activeLeases) == 0)
                TryDisposeGate();
        }

        private void TryDisposeGate()
        {
            if (Volatile.Read(ref _closed) != 0
                && Volatile.Read(ref _activeLeases) == 0
                && Interlocked.Exchange(ref _gateDisposed, 1) == 0)
            {
                _slots.Dispose();
            }
        }

        private UdpClient CreateConnectedClient()
        {
            var client = new UdpClient(server.AddressFamily);
            client.Connect(new IPEndPoint(server, DnsPort));
            return client;
        }
    }
}
