using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using System.Net;
using System.Net.Sockets;

namespace DnsCore.Services;

/// <summary>
/// DNS server
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
    /// Start DNS server
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Load custom records
            customRecordStore.AddRecords(options.CustomRecords);

            // Set upstream DNS servers
            upstreamResolver.SetUpstreamServers(options.UpstreamDnsServers);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Create UDP server
            _udpServer = new UdpClient(options.Port);
            logger.LogInformation("DNS server UDP listener started on port: {Port}", options.Port);

            // Create TCP server
            _tcpServer = new TcpListener(IPAddress.Any, options.Port);
            _tcpServer.Start();
            logger.LogInformation("DNS server TCP listener started on port: {Port}", options.Port);

            logger.LogInformation("Custom record count: {Count}", options.CustomRecords.Count);

            // Start both UDP and TCP listening
            var udpTask = ListenUdpAsync(_cts.Token);
            var tcpTask = ListenTcpAsync(_cts.Token);

            await Task.WhenAll(udpTask, tcpTask);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DNS server failed to start");
            throw;
        }
    }

    /// <summary>
    /// Stop DNS server
    /// </summary>
    public void Stop()
    {
        logger.LogInformation("Stopping DNS server...");

        _cts?.Cancel();

        _udpServer?.Close();
        _udpServer?.Dispose();

        _tcpServer?.Stop();

        logger.LogInformation("DNS server stopped");
    }

    /// <summary>
    /// Listen and process UDP DNS requests
    /// </summary>
    private async Task ListenUdpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpServer!.ReceiveAsync(cancellationToken);

                // Process request asynchronously without blocking receive loop
                _ = Task.Run(() => ProcessUdpRequestAsync(result.Buffer, result.RemoteEndPoint), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred while receiving UDP DNS request");
            }
        }
    }

    /// <summary>
    /// Listen and process TCP DNS requests
    /// </summary>
    private async Task ListenTcpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpServer!.AcceptTcpClientAsync(cancellationToken);

                // Process request asynchronously without blocking receive loop
                _ = Task.Run(() => ProcessTcpClientAsync(client), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred while receiving TCP DNS request");
            }
        }
    }

    /// <summary>
    /// Process TCP client connection
    /// </summary>
    private async Task ProcessTcpClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var clientEndpoint = client.Client.RemoteEndPoint as IPEndPoint;

                // TCP DNS message format: first 2 bytes are message length (big-endian)
                var lengthBuffer = new byte[2];
                var bytesRead = await stream.ReadAsync(lengthBuffer, 0, 2);

                if (bytesRead != 2)
                {
                    logger.LogWarning("TCP request length insufficient, from {Client}", clientEndpoint);
                    return;
                }

                // Parse message length (big-endian)
                var messageLength = (lengthBuffer[0] << 8) | lengthBuffer[1];

                // Read DNS message
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
                    logger.LogWarning("TCP DNS message read incomplete, from {Client}", clientEndpoint);
                    return;
                }

                logger.LogDebug("Received TCP DNS query, length: {Length} bytes, from {Client}", messageLength, clientEndpoint);

                // Process DNS query
                var responseData = await ProcessDnsQueryAsync(requestData, clientEndpoint!, "TCP");

                if (responseData != null)
                {
                    // TCP response format: first 2 bytes are message length (big-endian) + DNS message
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
            logger.LogError(ex, "Error occurred while processing TCP DNS request");
        }
    }

    /// <summary>
    /// Process UDP DNS request
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
            logger.LogError(ex, "Error occurred while processing UDP DNS request");
        }
    }

    /// <summary>
    /// Process DNS query (shared by UDP and TCP)
    /// </summary>
    private async Task<byte[]?> ProcessDnsQueryAsync(byte[] requestData, IPEndPoint clientEndpoint, string protocol)
    {
        try
        {
            // Parse request
            var (header, questions) = DnsMessageParser.ParseQuery(requestData);

            if (questions.Count == 0)
            {
                logger.LogWarning("Received invalid DNS request (no question section), protocol: {Protocol}", protocol);
                return null;
            }

            var question = questions[0];
            logger.LogInformation("Received DNS query ({Protocol}): {Domain} {Type} from {Client}",
                protocol, question.Name, question.Type, clientEndpoint);

            // 1. Query custom records first
            var answers = customRecordStore.Query(question.Name, question.Type);

            byte[] responseData;
            if (answers is { Count: > 0 })
            {
                // Found custom record, return success
                logger.LogInformation("Responding with custom record ({Protocol}): {Domain} {Type}", protocol, question.Name, question.Type);
                responseData = BuildSuccessResponse(header, questions, answers, question, protocol);
            }
            else if (options.EnableUpstreamDnsQuery)
            {
                // 2. Custom record not found, upstream DNS query is enabled
                logger.LogInformation("Custom record not found, querying upstream DNS ({Protocol}): {Domain} {Type}", protocol, question.Name, question.Type);
                answers = await upstreamResolver.QueryAsync(question.Name, question.Type, requestData);

                responseData = answers is { Count: > 0 }
                    ? BuildSuccessResponse(header, questions, answers, question, protocol)
                    : BuildNxDomainResponse(header, questions, question, protocol);
            }
            else
            {
                // 3. Custom record not found, upstream DNS query is disabled
                // Return SERVFAIL to let client try next DNS server
                logger.LogInformation("Custom record not found and upstream DNS query is disabled, returning SERVFAIL to let client try next DNS server ({Protocol}): {Domain} {Type}", protocol, question.Name, question.Type);
                responseData = BuildServFailResponse(header, questions, question, protocol);
            }

            return responseData;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while processing DNS query, protocol: {Protocol}", protocol);
            return null;
        }
    }

    /// <summary>
    /// Build success response
    /// </summary>
    private byte[] BuildSuccessResponse(DnsHeader header, List<DnsQuestion> questions,
        List<DnsRecord> answers, DnsQuestion question, string protocol)
    {
        var response = DnsMessageParser.BuildResponse(header, questions, answers);
        logger.LogInformation("Returning DNS response ({Protocol}): {Domain} {Type}, {Count} records",
            protocol, question.Name, question.Type, answers.Count);
        return response;
    }

    /// <summary>
    /// Build NXDOMAIN response (domain does not exist)
    /// </summary>
    private byte[] BuildNxDomainResponse(DnsHeader header, List<DnsQuestion> questions, DnsQuestion question, string protocol)
    {
        header.SetAsResponse();
        header.Flags |= 0x0003; // RCODE = 3 (NXDOMAIN)
        var response = DnsMessageParser.BuildResponse(header, questions, []);
        logger.LogWarning("DNS query failed, returning NXDOMAIN ({Protocol}): {Domain} {Type}", protocol, question.Name, question.Type);
        return response;
    }

    /// <summary>
    /// Build SERVFAIL response (server failure, client should try next DNS server)
    /// </summary>
    private byte[] BuildServFailResponse(DnsHeader header, List<DnsQuestion> questions, DnsQuestion question, string protocol)
    {
        header.SetAsResponse();
        header.Flags |= 0x0002; // RCODE = 2 (SERVFAIL)
        var response = DnsMessageParser.BuildResponse(header, questions, []);
        logger.LogInformation("Returning SERVFAIL ({Protocol}): {Domain} {Type}", protocol, question.Name, question.Type);
        return response;
    }
}
