using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Services;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Net.Sockets;

namespace DnsCore.Tests.Services;

/// <summary>
/// DNS 服务器 TCP 协议测试
/// </summary>
public class DnsServerTcpTests
{
    private readonly Mock<ILogger<DnsServer>> _mockLogger;
    private readonly CustomRecordStore _recordStore;
    private readonly UpstreamDnsResolver _upstreamResolver;
    private readonly DnsServerOptions _options;

    public DnsServerTcpTests()
    {
        _mockLogger = new Mock<ILogger<DnsServer>>();
        var recordStoreLogger = new Mock<ILogger<CustomRecordStore>>();
        var resolverLogger = new Mock<ILogger<UpstreamDnsResolver>>();

        _recordStore = new CustomRecordStore(recordStoreLogger.Object);
        _upstreamResolver = new UpstreamDnsResolver(resolverLogger.Object);

        // 使用高端口避免权限问题
        _options = new DnsServerOptions
        {
            Port = 15353, // 使用高端口进行测试
            CustomRecords = [],
            UpstreamDnsServers = ["8.8.8.8"]
        };
    }

    [Fact]
    public void TcpMessageLength_ShouldBeEncodedAsBigEndian()
    {
        // Arrange - 测试 TCP 消息长度编码（大端序）
        var messageLength = 512;

        // Act - 编码长度
        var lengthBytes = new byte[2];
        lengthBytes[0] = (byte)(messageLength >> 8);
        lengthBytes[1] = (byte)(messageLength & 0xFF);

        // Assert
        Assert.Equal(2, lengthBytes[0]); // 高字节 = 512 / 256 = 2
        Assert.Equal(0, lengthBytes[1]); // 低字节 = 512 % 256 = 0

        // Decode and verify
        var decodedLength = (lengthBytes[0] << 8) | lengthBytes[1];
        Assert.Equal(messageLength, decodedLength);
    }

    [Fact]
    public void TcpMessageLength_ShouldHandleMaximumDnsMessageSize()
    {
        // Arrange - DNS over TCP 最大消息长度是 65535 字节
        var maxLength = 65535;

        // Act - 编码最大长度
        var lengthBytes = new byte[2];
        lengthBytes[0] = (byte)(maxLength >> 8);
        lengthBytes[1] = (byte)(maxLength & 0xFF);

        // Assert
        Assert.Equal(255, lengthBytes[0]);
        Assert.Equal(255, lengthBytes[1]);

        // Decode and verify
        var decodedLength = (lengthBytes[0] << 8) | lengthBytes[1];
        Assert.Equal(maxLength, decodedLength);
    }

    [Fact]
    public void TcpMessageLength_ShouldHandleSmallMessages()
    {
        // Arrange - 测试小消息（< 256 字节）
        var smallLength = 42;

        // Act
        var lengthBytes = new byte[2];
        lengthBytes[0] = (byte)(smallLength >> 8);
        lengthBytes[1] = (byte)(smallLength & 0xFF);

        // Assert
        Assert.Equal(0, lengthBytes[0]); // 高字节为 0
        Assert.Equal(42, lengthBytes[1]); // 低字节为 42

        // Decode and verify
        var decodedLength = (lengthBytes[0] << 8) | lengthBytes[1];
        Assert.Equal(smallLength, decodedLength);
    }

    [Fact]
    public async Task DnsServer_ShouldAcceptTcpConnections()
    {
        // Arrange
        var server = new DnsServer(_mockLogger.Object, _recordStore, _upstreamResolver, _options);

        // 添加测试记录
        _recordStore.AddRecord(new DnsRecord
        {
            Domain = "test.local",
            Type = DnsRecordType.A,
            Value = "192.168.1.100",
            TTL = 3600
        });

        // 在后台启动服务器
        var cts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.StartAsync(cts.Token));

        // 等待服务器启动
        await Task.Delay(500);

        try
        {
            // Act - 尝试建立 TCP 连接
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(IPAddress.Loopback, _options.Port);

            // Assert - 连接应该成功
            Assert.True(tcpClient.Connected, "应该能够建立 TCP 连接到 DNS 服务器");
        }
        finally
        {
            // Cleanup
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

    [Fact]
    public async Task DnsServer_ShouldRespondToTcpDnsQuery()
    {
        // Arrange
        var server = new DnsServer(_mockLogger.Object, _recordStore, _upstreamResolver, _options);

        // 添加测试记录
        _recordStore.AddRecord(new DnsRecord
        {
            Domain = "tcp.test.local",
            Type = DnsRecordType.A,
            Value = "10.0.0.1",
            TTL = 3600
        });

        // 在后台启动服务器
        var cts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.StartAsync(cts.Token));

        // 等待服务器启动
        await Task.Delay(500);

        try
        {
            // Act - 发送 TCP DNS 查询
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(IPAddress.Loopback, _options.Port);
            var stream = tcpClient.GetStream();

            // 构建一个简单的 DNS 查询消息（查询 tcp.test.local 的 A 记录）
            var queryMessage = BuildSimpleDnsQuery("tcp.test.local");

            // TCP DNS 格式：2字节长度 + DNS消息
            var tcpMessage = new byte[queryMessage.Length + 2];
            tcpMessage[0] = (byte)(queryMessage.Length >> 8);
            tcpMessage[1] = (byte)(queryMessage.Length & 0xFF);
            Array.Copy(queryMessage, 0, tcpMessage, 2, queryMessage.Length);

            // 发送查询
            await stream.WriteAsync(tcpMessage, 0, tcpMessage.Length);
            await stream.FlushAsync();

            // 读取响应长度
            var lengthBuffer = new byte[2];
            var bytesRead = await stream.ReadAsync(lengthBuffer, 0, 2);

            // Assert - 应该收到响应
            Assert.Equal(2, bytesRead);

            var responseLength = (lengthBuffer[0] << 8) | lengthBuffer[1];
            Assert.True(responseLength > 0, "响应长度应该大于 0");
            Assert.True(responseLength < 65536, "响应长度应该在有效范围内");

            // 读取响应内容
            var responseBuffer = new byte[responseLength];
            var totalRead = 0;
            while (totalRead < responseLength)
            {
                bytesRead = await stream.ReadAsync(responseBuffer, totalRead, responseLength - totalRead);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            Assert.Equal(responseLength, totalRead);
            Assert.True(responseBuffer.Length > 12, "DNS 响应应该至少包含 12 字节的头部");
        }
        finally
        {
            // Cleanup
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

    /// <summary>
    /// 构建简单的 DNS 查询消息
    /// </summary>
    private static byte[] BuildSimpleDnsQuery(string domain)
    {
        var message = new List<byte>();

        // DNS Header (12 bytes)
        message.AddRange(new byte[] {
            0x12, 0x34, // Transaction ID
            0x01, 0x00, // Flags: Standard query
            0x00, 0x01, // Questions: 1
            0x00, 0x00, // Answer RRs: 0
            0x00, 0x00, // Authority RRs: 0
            0x00, 0x00  // Additional RRs: 0
        });

        // Question section
        // Domain name (label format)
        foreach (var label in domain.Split('.'))
        {
            message.Add((byte)label.Length);
            message.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        message.Add(0x00); // End of domain name

        // Type: A (1)
        message.Add(0x00);
        message.Add(0x01);

        // Class: IN (1)
        message.Add(0x00);
        message.Add(0x01);

        return message.ToArray();
    }
}
