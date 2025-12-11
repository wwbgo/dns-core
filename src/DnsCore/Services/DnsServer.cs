using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using System.Net;
using System.Net.Sockets;

namespace DnsCore.Services;

/// <summary>
/// DNS 服务器
/// </summary>
public sealed class DnsServer(
    ILogger<DnsServer> logger,
    CustomRecordStore customRecordStore,
    UpstreamDnsResolver upstreamResolver,
    DnsServerOptions options)
{
    private UdpClient? _udpServer;
    private TcpListener? _tcpServer;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 启动 DNS 服务器
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 加载自定义记录
            customRecordStore.AddRecords(options.CustomRecords);

            // 设置上游 DNS 服务器
            upstreamResolver.SetUpstreamServers(options.UpstreamDnsServers);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 创建 UDP 服务器
            _udpServer = new UdpClient(options.Port);
            logger.LogInformation("DNS 服务器 UDP 监听启动，端口: {Port}", options.Port);

            // 创建 TCP 服务器
            _tcpServer = new TcpListener(IPAddress.Any, options.Port);
            _tcpServer.Start();
            logger.LogInformation("DNS 服务器 TCP 监听启动，端口: {Port}", options.Port);

            logger.LogInformation("自定义记录数量: {Count}", options.CustomRecords.Count);

            // 同时启动 UDP 和 TCP 监听
            var udpTask = ListenUdpAsync(_cts.Token);
            var tcpTask = ListenTcpAsync(_cts.Token);

            await Task.WhenAll(udpTask, tcpTask);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DNS 服务器启动失败");
            throw;
        }
    }

    /// <summary>
    /// 停止 DNS 服务器
    /// </summary>
    public void Stop()
    {
        logger.LogInformation("正在停止 DNS 服务器...");

        _cts?.Cancel();

        _udpServer?.Close();
        _udpServer?.Dispose();

        _tcpServer?.Stop();

        logger.LogInformation("DNS 服务器已停止");
    }

    /// <summary>
    /// 监听并处理 UDP DNS 请求
    /// </summary>
    private async Task ListenUdpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpServer!.ReceiveAsync(cancellationToken);

                // 异步处理请求，不阻塞接收循环
                _ = Task.Run(() => ProcessUdpRequestAsync(result.Buffer, result.RemoteEndPoint), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "接收 UDP DNS 请求时发生错误");
            }
        }
    }

    /// <summary>
    /// 监听并处理 TCP DNS 请求
    /// </summary>
    private async Task ListenTcpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpServer!.AcceptTcpClientAsync(cancellationToken);

                // 异步处理请求，不阻塞接收循环
                _ = Task.Run(() => ProcessTcpClientAsync(client), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "接收 TCP DNS 请求时发生错误");
            }
        }
    }

    /// <summary>
    /// 处理 TCP 客户端连接
    /// </summary>
    private async Task ProcessTcpClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var clientEndpoint = client.Client.RemoteEndPoint as IPEndPoint;

                // TCP DNS 消息格式：前2字节是消息长度（大端序）
                var lengthBuffer = new byte[2];
                var bytesRead = await stream.ReadAsync(lengthBuffer, 0, 2);

                if (bytesRead != 2)
                {
                    logger.LogWarning("TCP 请求长度不足，来自 {Client}", clientEndpoint);
                    return;
                }

                // 解析消息长度（大端序）
                var messageLength = (lengthBuffer[0] << 8) | lengthBuffer[1];

                // 读取 DNS 消息
                var requestData = new byte[messageLength];
                var totalRead = 0;

                while (totalRead < messageLength)
                {
                    bytesRead = await stream.ReadAsync(requestData, totalRead, messageLength - totalRead);
                    if (bytesRead == 0)
                        break;
                    totalRead += bytesRead;
                }

                if (totalRead != messageLength)
                {
                    logger.LogWarning("TCP DNS 消息读取不完整，来自 {Client}", clientEndpoint);
                    return;
                }

                logger.LogDebug("收到 TCP DNS 查询，长度: {Length} 字节，来自 {Client}", messageLength, clientEndpoint);

                // 处理 DNS 查询
                var responseData = await ProcessDnsQueryAsync(requestData, clientEndpoint!, "TCP");

                if (responseData != null)
                {
                    // TCP 响应格式：前2字节是消息长度（大端序）+ DNS 消息
                    var responseLength = responseData.Length;
                    var tcpResponse = new byte[responseLength + 2];

                    tcpResponse[0] = (byte)(responseLength >> 8);
                    tcpResponse[1] = (byte)(responseLength & 0xFF);
                    Array.Copy(responseData, 0, tcpResponse, 2, responseLength);

                    await stream.WriteAsync(tcpResponse, 0, tcpResponse.Length);
                    await stream.FlushAsync();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 TCP DNS 请求时发生错误");
        }
    }

    /// <summary>
    /// 处理 UDP DNS 请求
    /// </summary>
    private async Task ProcessUdpRequestAsync(byte[] requestData, IPEndPoint clientEndpoint)
    {
        try
        {
            var responseData = await ProcessDnsQueryAsync(requestData, clientEndpoint, "UDP");

            if (responseData != null)
            {
                await _udpServer!.SendAsync(responseData, responseData.Length, clientEndpoint);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 UDP DNS 请求时发生错误");
        }
    }

    /// <summary>
    /// 处理 DNS 查询（UDP 和 TCP 共用）
    /// </summary>
    private async Task<byte[]?> ProcessDnsQueryAsync(byte[] requestData, IPEndPoint clientEndpoint, string protocol)
    {
        try
        {
            // 解析请求
            var (header, questions) = DnsMessageParser.ParseQuery(requestData);

            if (questions.Count == 0)
            {
                logger.LogWarning("收到无效的 DNS 请求（无问题部分），协议: {Protocol}", protocol);
                return null;
            }

            var question = questions[0];
            logger.LogInformation("收到 DNS 查询 ({Protocol}): {Domain} {Type} 来自 {Client}",
                protocol, question.Name, question.Type, clientEndpoint);

            // 1. 先查询自定义记录
            var answers = customRecordStore.Query(question.Name, question.Type);

            if (answers is { Count: > 0 })
            {
                logger.LogInformation("使用自定义记录响应 ({Protocol}): {Domain} {Type}", protocol, question.Name, question.Type);
            }
            else
            {
                // 2. 如果没有找到，查询上游 DNS
                logger.LogInformation("自定义记录未找到，查询上游 DNS ({Protocol}): {Domain} {Type}", protocol, question.Name, question.Type);
                answers = await upstreamResolver.QueryAsync(question.Name, question.Type, requestData);
            }

            // 3. 构建响应
            byte[] responseData = answers is { Count: > 0 }
                ? BuildSuccessResponse(header, questions, answers, question, protocol)
                : BuildErrorResponse(header, questions, question, protocol);

            return responseData;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 DNS 查询时发生错误，协议: {Protocol}", protocol);
            return null;
        }
    }

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private byte[] BuildSuccessResponse(DnsHeader header, List<DnsQuestion> questions,
        List<DnsRecord> answers, DnsQuestion question, string protocol)
    {
        var response = DnsMessageParser.BuildResponse(header, questions, answers);
        logger.LogInformation("返回 DNS 响应 ({Protocol}): {Domain} {Type}, {Count} 条记录",
            protocol, question.Name, question.Type, answers.Count);
        return response;
    }

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private byte[] BuildErrorResponse(DnsHeader header, List<DnsQuestion> questions, DnsQuestion question, string protocol)
    {
        header.SetAsResponse();
        header.Flags |= 0x0003; // RCODE = 3 (NXDOMAIN)
        var response = DnsMessageParser.BuildResponse(header, questions, []);
        logger.LogWarning("DNS 查询失败，返回 NXDOMAIN ({Protocol}): {Domain} {Type}", protocol, question.Name, question.Type);
        return response;
    }
}
