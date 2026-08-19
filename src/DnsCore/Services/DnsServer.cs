using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DnsCore.Services;

/// <summary>
/// DNS 服务器（UDP + TCP）
/// </summary>
public sealed class DnsServer(
    ILogger<DnsServer> logger,
    CustomRecordStore customRecordStore,
    UpstreamDnsResolver upstreamResolver,
    DnsServerOptions options,
    DnsQueryStatistics statistics,
    DnsLatencyStatistics latencyStatistics)
{
    private UdpClient? _udpServer;
    private TcpListener? _tcpServer;
    private CancellationTokenSource? _cts;

    private readonly NetworkAcl _clientAcl = new(
        options.Security.EnableClientRestriction ? options.Security.AllowedClientNetworks : null);

    private readonly ClientRateLimiter _rateLimiter = new(options.Security.MaxQueriesPerSecondPerClient);

    // 限制在飞的查询数：原实现对每个包无条件 Task.Run，小包洪泛即可耗尽线程池与内存
    private readonly SemaphoreSlim _queryLimit = new(Math.Max(1, options.Security.MaxConcurrentQueries));
    private readonly SemaphoreSlim _tcpConnectionLimit = new(Math.Max(1, options.Security.MaxConcurrentTcpConnections));

    /// <summary>服务是否正在监听（供健康检查使用）</summary>
    public bool IsListening { get; private set; }

    /// <summary>启动 DNS 服务器</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 上游服务器列表；自定义记录已在应用启动阶段统一加载，此处不再重复添加
            upstreamResolver.SetUpstreamServers(options.UpstreamDnsServers);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var listenAddress = ParseListenAddress(options.ListenAddress);

            _udpServer = CreateUdpServer(listenAddress, options.Port);
            logger.LogInformation("DNS UDP 监听已启动: {Address}:{Port}", listenAddress, options.Port);

            _tcpServer = new TcpListener(listenAddress, options.Port);
            if (listenAddress.Equals(IPAddress.IPv6Any))
                _tcpServer.Server.DualMode = true;
            _tcpServer.Start();
            logger.LogInformation("DNS TCP 监听已启动: {Address}:{Port}", listenAddress, options.Port);

            logger.LogInformation("自定义记录数: {Count}", customRecordStore.Count);
            IsListening = true;

            await Task.WhenAll(ListenUdpAsync(_cts.Token), ListenTcpAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            IsListening = false;
            logger.LogError(ex, "DNS 服务器启动失败");
            throw;
        }
    }

    private static IPAddress ParseListenAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return IPAddress.IPv6Any;

        return IPAddress.TryParse(address.Trim(), out var parsed) ? parsed : IPAddress.IPv6Any;
    }

    /// <summary>
    /// 创建 UDP 监听。IPv6Any + DualMode 可同时服务 IPv4/IPv6：
    /// 原实现 new UdpClient(port) 只绑 IPv4。
    /// </summary>
    private UdpClient CreateUdpServer(IPAddress listenAddress, int port)
    {
        var udpClient = new UdpClient(listenAddress.AddressFamily);

        if (listenAddress.Equals(IPAddress.IPv6Any))
            udpClient.Client.DualMode = true;

        // Windows 上客户端提前关闭会触发 ICMP port unreachable，
        // 若不禁用 SIO_UDP_CONNRESET，ReceiveAsync 会抛异常打断接收循环
        if (OperatingSystem.IsWindows())
        {
            try
            {
                udpClient.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "设置 SIO_UDP_CONNRESET 失败（可忽略）");
            }
        }

        // 放大 socket 缓冲：突发流量下默认缓冲（通常 64KB）很快溢出，
        // 内核会直接丢包，表现为客户端超时而非服务端报错
        try
        {
            udpClient.Client.ReceiveBufferSize = Math.Max(
                udpClient.Client.ReceiveBufferSize, options.Security.SocketReceiveBufferBytes);
            udpClient.Client.SendBufferSize = Math.Max(
                udpClient.Client.SendBufferSize, options.Security.SocketSendBufferBytes);
        }
        catch (SocketException ex)
        {
            logger.LogDebug(ex, "设置 UDP socket 缓冲大小失败，使用系统默认值");
        }

        udpClient.Client.Bind(new IPEndPoint(listenAddress, port));
        return udpClient;
    }

    /// <summary>停止 DNS 服务器</summary>
    public void Stop()
    {
        logger.LogInformation("正在停止 DNS 服务器...");
        IsListening = false;

        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }

        _udpServer?.Dispose();
        _tcpServer?.Stop();
        _tcpServer?.Dispose();

        _cts?.Dispose();
        _cts = null;

        logger.LogInformation("DNS 服务器已停止");
    }

    /// <summary>UDP 接收循环</summary>
    private async Task ListenUdpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpServer!.ReceiveAsync(cancellationToken);

                if (!ShouldAccept(result.RemoteEndPoint))
                    continue;

                // 有并发上限地处理，超限直接丢包（DNS 客户端本身会重试）
                if (!await _queryLimit.WaitAsync(0, cancellationToken))
                {
                    logger.LogWarning("并发查询数达上限，丢弃来自 {Client} 的 UDP 查询", result.RemoteEndPoint);
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessUdpRequestAsync(result.Buffer, result.RemoteEndPoint, cancellationToken);
                    }
                    finally
                    {
                        _queryLimit.Release();
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "UDP 接收出现 socket 错误，继续监听");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "接收 UDP DNS 请求时出错");
            }
        }
    }

    /// <summary>TCP 接受循环</summary>
    private async Task ListenTcpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpServer!.AcceptTcpClientAsync(cancellationToken);

                if (!ShouldAccept(client.Client.RemoteEndPoint as IPEndPoint))
                {
                    client.Dispose();
                    continue;
                }

                if (!await _tcpConnectionLimit.WaitAsync(0, cancellationToken))
                {
                    logger.LogWarning("TCP 连接数达上限，拒绝来自 {Client} 的连接", client.Client.RemoteEndPoint);
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessTcpClientAsync(client, cancellationToken);
                    }
                    finally
                    {
                        _tcpConnectionLimit.Release();
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "接受 TCP DNS 连接时出错");
            }
        }
    }

    /// <summary>来源网段与限流检查</summary>
    private bool ShouldAccept(IPEndPoint? endpoint)
    {
        if (endpoint is null)
            return false;

        if (!_clientAcl.IsAllowed(endpoint.Address))
        {
            logger.LogDebug("拒绝不在允许网段内的客户端: {Client}", endpoint.Address);
            return false;
        }

        if (!_rateLimiter.TryAcquire(endpoint.Address))
        {
            logger.LogDebug("客户端触发限流: {Client}", endpoint.Address);
            return false;
        }

        return true;
    }

    /// <summary>处理 TCP 连接（支持连接复用 RFC 7766）</summary>
    private async Task ProcessTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        byte[]? requestBuffer = null;
        var clientEndpoint = client.Client.RemoteEndPoint as IPEndPoint;

        try
        {
            using (client)
            {
                // 读写超时是 slowloris 的基本防御：原实现无任何超时，
                // 攻击者可以只发一个字节然后永久占住连接
                client.ReceiveTimeout = options.Security.TcpTimeoutMilliseconds;
                client.SendTimeout = options.Security.TcpTimeoutMilliseconds;

                // 连接级超时：空闲超时（每次成功查询后重置）
                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                var stream = client.GetStream();
                var lengthBuffer = new byte[2];
                var queriesServed = 0;
                const int MaxQueriesPerConnection = 10000;  // 单连接最大查询数

                // 循环处理多个查询，直到客户端关闭连接或超时
                while (!connectionCts.Token.IsCancellationRequested && queriesServed < MaxQueriesPerConnection)
                {
                    // 每次查询开始前重置连接空闲超时
                    connectionCts.CancelAfter(options.Security.TcpTimeoutMilliseconds);
                    // 每次查询重置超时（空闲超时而非连接总时长）
                    using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token);
                    queryCts.CancelAfter(options.Security.TcpTimeoutMilliseconds);
                    var token = queryCts.Token;

                    // 读取长度前缀（2 字节）
                    int lenRead = 0;
                    while (lenRead < 2)
                    {
                        var n = await stream.ReadAsync(lengthBuffer.AsMemory(lenRead, 2 - lenRead), token);
                        if (n == 0)
                        {
                            // 客户端正常关闭连接（在查询边界）
                            if (lenRead == 0)
                            {
                                logger.LogDebug("TCP 客户端 {Client} 正常关闭连接，已服务 {Count} 个查询",
                                    clientEndpoint, queriesServed);
                                return;
                            }
                            // 读到一半就断开，协议违规
                            logger.LogWarning("TCP 客户端 {Client} 在长度字段读取中途断开", clientEndpoint);
                            return;
                        }
                        lenRead += n;
                    }

                    var messageLength = (lengthBuffer[0] << 8) | lengthBuffer[1];

                    // 长度完全由客户端控制，必须校验：
                    // 0 会让后续逻辑空转，超大值会让每连接白占缓冲
                    if (messageLength < DnsHeader.Size || messageLength > DnsLimits.MaxMessageSize)
                    {
                        logger.LogWarning("TCP DNS 报文长度非法({Length})，来自 {Client}，断开连接",
                            messageLength, clientEndpoint);
                        return;
                    }

                    // 租用或扩容缓冲区
                    if (requestBuffer is null || requestBuffer.Length < messageLength)
                    {
                        if (requestBuffer is not null)
                            ArrayPool<byte>.Shared.Return(requestBuffer);
                        requestBuffer = ArrayPool<byte>.Shared.Rent(messageLength);
                    }

                    // 读取完整 DNS 报文
                    int bodyRead = 0;
                    while (bodyRead < messageLength)
                    {
                        var n = await stream.ReadAsync(
                            requestBuffer.AsMemory(bodyRead, messageLength - bodyRead), token);
                        if (n == 0)
                        {
                            logger.LogWarning("TCP 客户端 {Client} 在报文读取中途断开", clientEndpoint);
                            return;
                        }
                        bodyRead += n;
                    }

                    logger.LogDebug("收到 TCP DNS 查询，长度 {Length} 字节，来自 {Client}",
                        messageLength, clientEndpoint);

                    var responseData = await ProcessDnsQueryAsync(
                        requestBuffer.AsMemory(0, messageLength), clientEndpoint, "TCP",
                        DnsLimits.MaxMessageSize, token);

                    if (responseData is null)
                    {
                        // 查询处理失败但连接可继续（客户端可能发下一个查询）
                        queriesServed++;
                        continue;
                    }

                    var framed = new byte[responseData.Length + 2];
                    framed[0] = (byte)(responseData.Length >> 8);
                    framed[1] = (byte)(responseData.Length & 0xFF);
                    responseData.CopyTo(framed.AsSpan(2));

                    await stream.WriteAsync(framed, token);
                    await stream.FlushAsync(token);

                    queriesServed++;
                }

                if (queriesServed >= MaxQueriesPerConnection)
                {
                    logger.LogDebug("TCP 连接 {Client} 达到最大查询数 {Max}，主动关闭",
                        clientEndpoint, MaxQueriesPerConnection);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("TCP DNS 连接 {Client} 超时或被取消", clientEndpoint);
        }
        catch (EndOfStreamException)
        {
            logger.LogDebug("TCP DNS 连接 {Client} 在读取完整报文前关闭", clientEndpoint);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "TCP DNS 连接 {Client} IO 错误", clientEndpoint);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 TCP DNS 请求时出错，客户端 {Client}", clientEndpoint);
        }
        finally
        {
            if (requestBuffer is not null)
                ArrayPool<byte>.Shared.Return(requestBuffer);
        }
    }

    /// <summary>处理 UDP 请求</summary>
    private async Task ProcessUdpRequestAsync(
        byte[] requestData, IPEndPoint clientEndpoint, CancellationToken cancellationToken)
    {
        try
        {
            var responseData = await ProcessDnsQueryAsync(
                requestData, clientEndpoint, "UDP", maxResponseSize: null, cancellationToken);

            if (responseData is not null)
                await _udpServer!.SendAsync(responseData, clientEndpoint, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 UDP DNS 请求时出错");
        }
    }

    /// <summary>
    /// 处理 DNS 查询（UDP/TCP 共用）。
    /// maxResponseSize 为 null 表示由请求的 EDNS0 决定（UDP 场景）。
    /// </summary>
    private async Task<byte[]?> ProcessDnsQueryAsync(
        ReadOnlyMemory<byte> requestData, IPEndPoint? clientEndpoint, string protocol,
        int? maxResponseSize, CancellationToken cancellationToken)
    {
        DnsQuery query;

        try
        {
            query = DnsMessageParser.Parse(requestData.Span);
        }
        catch (InvalidDataException ex)
        {
            // 畸形报文属于预期输入：记 Debug 并静默丢弃，不打 Error 日志刷屏
            logger.LogDebug(ex, "收到畸形 DNS 报文({Protocol})，来自 {Client}", protocol, clientEndpoint);
            return null;
        }

        // 报文解析成功即计入：FormErr / NotImp 这类请求服务同样处理并应答了，
        // 属于真实到达的查询流量。只有解析失败的垃圾包不计（上面已 return）。
        statistics.RecordQuery();

        // 计时起点必须紧跟计数：两者若覆盖的返回路径不同，/api/qps 的 totalQueries
        // 会与 /api/qps/latency 的 totalRequests 分叉（FormErr/NotImp 曾只计其一）。
        // 用 Stopwatch 而非 DateTime.UtcNow：后者是墙上时钟，NTP 回拨会算出负延迟，
        // 污染最小值与平均值。
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var header = query.Header;

            if (query.Questions.Count == 0)
            {
                logger.LogDebug("DNS 请求无 question 区({Protocol})", protocol);
                return BuildErrorResponse(query, DnsResponseCode.FormErr, maxResponseSize);
            }

            // 只支持标准查询（Opcode 0）
            if (header.Opcode != 0)
            {
                logger.LogDebug("不支持的 Opcode {Opcode}({Protocol})", header.Opcode, protocol);
                return BuildErrorResponse(query, DnsResponseCode.NotImp, maxResponseSize);
            }

            var question = query.Questions[0];

            if (options.LogEveryQuery)
            {
                // 域名来自不可信输入，转义后再记录，避免日志注入
                logger.LogInformation("收到 DNS 查询({Protocol}): {Domain} {Type} 来自 {Client}",
                    protocol, Sanitize(question.Name), question.Type, clientEndpoint);
            }

            // 0. 自动应答服务器自身的 PTR 查询（修复 nslookup 显示 "UnKnown"）
            if (question.Type == DnsRecordType.PTR && !string.IsNullOrWhiteSpace(options.Hostname))
            {
                var ptrAnswer = TryBuildServerPtrResponse(question.Name);
                if (ptrAnswer is not null)
                {
                    logger.LogDebug("以服务器主机名应答 PTR 查询({Protocol}): {Domain} → {Hostname}",
                        protocol, Sanitize(question.Name), options.Hostname);

                    return BuildResponse(query, new List<DnsRecord> { ptrAnswer },
                        DnsResponseCode.NoError, isAuthoritative: true, maxResponseSize);
                }
            }

            // 1. 优先查自定义记录
            var customAnswers = customRecordStore.Query(question.Name, question.Type);

            if (customAnswers is { Count: > 0 })
            {
                logger.LogDebug("以自定义记录应答({Protocol}): {Domain} {Type}",
                    protocol, Sanitize(question.Name), question.Type);

                return BuildResponse(query, customAnswers, DnsResponseCode.NoError,
                    isAuthoritative: true, maxResponseSize);
            }

            // 2. 该类型没有记录，但域名本身在本地存在 → NODATA（权威应答，不转发上游）
            //
            // 本地域名（如只配了 A 记录的 test.cc）在权威语义上就是"存在但无此类型"，
            // 把它转发给上游既不正确也很慢：上游对这类私有域名通常无响应，
            // 会一直等到超时。nslookup 每次查询都并发 A + AAAA，因此表现为
            // 每次固定卡 2 秒（上游超时时长），关闭上游转发反而"变快"。
            if (customRecordStore.ContainsDomain(question.Name))
            {
                logger.LogDebug("域名存在但无此类型记录，返回 NODATA({Protocol}): {Domain} {Type}",
                    protocol, Sanitize(question.Name), question.Type);

                return BuildResponse(query, [], DnsResponseCode.NoError,
                    isAuthoritative: true, maxResponseSize);
            }

            // 3. 自定义记录未命中且未启用上游：返回 SERVFAIL 让客户端换服务器
            if (!options.EnableUpstreamDnsQuery)
            {
                logger.LogDebug("未命中自定义记录且上游查询已禁用，返回 SERVFAIL({Protocol}): {Domain}",
                    protocol, Sanitize(question.Name));

                return BuildErrorResponse(query, DnsResponseCode.ServFail, maxResponseSize);
            }

            // 4. 转发上游
            var upstream = await upstreamResolver.QueryAsync(
                question.Name, question.Type, question.Class, cancellationToken);

            // 上游全部失败是服务端问题，必须返回 SERVFAIL。
            // 原实现返回 NXDOMAIN，等于谎称域名不存在，会被客户端负缓存。
            if (upstream is null)
            {
                logger.LogDebug("上游查询失败，返回 SERVFAIL({Protocol}): {Domain}",
                    protocol, Sanitize(question.Name));

                return BuildErrorResponse(query, DnsResponseCode.ServFail, maxResponseSize);
            }

            // 转发的应答不得置 AA 位；RCODE 沿用上游结果，
            // 从而区分 NXDOMAIN（域名不存在）与 NODATA（域名存在但无该类型记录）
            return BuildResponse(query, upstream.Answers, upstream.ResponseCode,
                isAuthoritative: false, maxResponseSize);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // question 现在声明在 try 内，catch 作用域不可见；从 query 安全取用于日志
            var domain = query.Questions.Count > 0 ? Sanitize(query.Questions[0].Name) : "(无 question)";
            logger.LogError(ex, "处理 DNS 查询出错({Protocol}): {Domain}", protocol, domain);
            return BuildErrorResponse(query, DnsResponseCode.ServFail, maxResponseSize);
        }
        finally
        {
            latencyStatistics.RecordLatency(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private byte[] BuildResponse(
        DnsQuery query, List<DnsRecord> answers, DnsResponseCode code,
        bool isAuthoritative, int? maxResponseSize)
        => DnsMessageParser.BuildResponse(new DnsResponseBuildRequest
        {
            Header = query.Header,
            Questions = query.Questions,
            Answers = answers,
            ResponseCode = code,
            IsAuthoritative = isAuthoritative,
            MaxSize = maxResponseSize ?? query.MaxUdpResponseSize,
            IncludeEdnsOpt = query.Edns is not null,
            EdnsPayloadSize = DnsLimits.MaxEdnsPayloadSize
        });

    private byte[] BuildErrorResponse(DnsQuery query, DnsResponseCode code, int? maxResponseSize)
        => BuildResponse(query, [], code, isAuthoritative: false, maxResponseSize);

    /// <summary>
    /// 尝试为服务器自身 IP 的 PTR 查询构建响应。
    /// 当 nslookup 启动时会反向查询 DNS 服务器的主机名，如果没有 PTR 记录会显示 "UnKnown"。
    /// </summary>
    private DnsRecord? TryBuildServerPtrResponse(string ptrQuery)
    {
        // PTR 查询格式：IPv4 为 "90.100.168.192.in-addr.arpa"，IPv6 为 "x.x.x...ip6.arpa"
        // 需要反向解析出 IP，检查是否是服务器监听的地址

        IPAddress? queryIp = null;

        // 解析 IPv4 PTR 查询
        if (ptrQuery.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = ptrQuery[..^13]; // 去掉 ".in-addr.arpa"
            var octets = prefix.Split('.');
            if (octets.Length == 4 && octets.All(o => byte.TryParse(o, out _)))
            {
                // PTR 查询中 IP 是反向的，需要翻转
                Array.Reverse(octets);
                if (IPAddress.TryParse(string.Join('.', octets), out var ip))
                    queryIp = ip;
            }
        }
        // 解析 IPv6 PTR 查询：每个十六进制位一个标签，逆序
        // 例如 ::1 → 1.0.0.0.(...共32个半字节...).ip6.arpa
        else if (ptrQuery.EndsWith(".ip6.arpa", StringComparison.OrdinalIgnoreCase))
        {
            var nibbles = ptrQuery[..^9].Split('.');
            if (nibbles.Length != 32)
                return null;

            // 逆序还原成 32 个十六进制字符，再按 4 个一组组成 8 段
            Span<char> hex = stackalloc char[32];
            for (var i = 0; i < 32; i++)
            {
                var nibble = nibbles[31 - i];
                if (nibble.Length != 1 || !Uri.IsHexDigit(nibble[0]))
                    return null;
                hex[i] = nibble[0];
            }

            Span<char> text = stackalloc char[39]; // 8 段 × 4 字符 + 7 个冒号
            var pos = 0;
            for (var seg = 0; seg < 8; seg++)
            {
                if (seg > 0)
                    text[pos++] = ':';
                hex.Slice(seg * 4, 4).CopyTo(text[pos..]);
                pos += 4;
            }

            if (IPAddress.TryParse(text[..pos], out var ip6))
                queryIp = ip6;
        }
        else
        {
            return null;
        }

        if (queryIp is null)
            return null;

        // 检查是否是服务器监听的地址
        // ListenAddress 可能是 "::" (所有), "0.0.0.0" (所有IPv4), 或具体 IP
        var listenAddr = options.ListenAddress;

        bool isServerIp = false;

        if (listenAddr == "::" || listenAddr == "0.0.0.0")
        {
            // 监听所有地址，检查查询的 IP 是否是本机的任一地址
            // 简化处理：只要是查本机 IP 的 PTR，都返回主机名
            isServerIp = IsLocalAddress(queryIp);
        }
        else if (IPAddress.TryParse(listenAddr, out var specificIp))
        {
            isServerIp = queryIp.Equals(specificIp);
        }

        if (!isServerIp)
            return null;

        // 构造 PTR 记录：owner name 是查询名，value 是目标主机名
        return new DnsRecord
        {
            Domain = ptrQuery,
            Type = DnsRecordType.PTR,
            Value = options.Hostname!,
            TTL = 3600
        };
    }

    // 本机地址集合。启动时枚举一次并缓存：
    // 原实现每次 PTR 查询都调用 Dns.GetHostEntry()，那是同步阻塞的 DNS 调用，
    // 放在查询热路径上会拖慢应答，被恶意 PTR 洪泛时还会放大成串行阻塞。
    private static readonly Lazy<HashSet<IPAddress>> LocalAddresses = new(() =>
    {
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    set.Add(addr.Address);
            }
        }
        catch (NetworkInformationException)
        {
            // 拿不到网卡信息时退化为只认回环，不影响主流程
        }
        return set;
    });

    /// <summary>检查 IP 是否是本机地址</summary>
    private static bool IsLocalAddress(IPAddress ip)
        => IPAddress.IsLoopback(ip) || LocalAddresses.Value.Contains(ip);

    /// <summary>清理不可信字符串中的控制字符，防止日志注入</summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var trimmed = value.Length > 253 ? value[..253] : value;
        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
        });
    }
}
