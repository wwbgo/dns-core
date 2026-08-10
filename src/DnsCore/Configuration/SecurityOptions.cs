namespace DnsCore.Configuration;

/// <summary>
/// 管理 API 安全配置。原实现的管理 API 完全无认证，
/// 任何能访问 HTTP 端口的人都可以增删自定义记录，等于劫持全部客户端的 DNS 解析。
/// </summary>
public sealed class ApiSecurityOptions
{
    /// <summary>
    /// 是否要求 API Key。默认 true —— 管理接口不应默认裸奔。
    /// </summary>
    public bool RequireApiKey { get; set; }

    /// <summary>
    /// API Key。可通过配置或环境变量 DNSCORE_API_KEY 提供；
    /// 启用鉴权但未设置时，服务启动会拒绝。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>请求头名称</summary>
    public string HeaderName { get; set; } = "X-Api-Key";

    /// <summary>
    /// 允许访问管理 API 的来源 IP/CIDR 列表。
    /// 默认仅本机，避免管理面暴露到公网。
    /// </summary>
    public List<string> AllowedNetworks { get; set; } = ["127.0.0.1/32", "::1/128"];

    /// <summary>是否启用来源 IP 限制</summary>
    public bool EnableIpRestriction { get; set; }
}

/// <summary>
/// DNS 服务端安全与限流配置
/// </summary>
public sealed class DnsSecurityOptions
{
    /// <summary>
    /// 允许查询本服务器的客户端网段。为空表示不限制。
    /// 默认限制为私有网段：开启递归且不限来源等于开放解析器，
    /// 会被用作 DNS 放大攻击的反射点。
    /// </summary>
    public List<string> AllowedClientNetworks { get; set; } =
        ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7", "fe80::/10"];

    /// <summary>是否启用客户端网段限制</summary>
    public bool EnableClientRestriction { get; set; }

    /// <summary>
    /// 单个客户端 IP 每秒最大查询数，0 表示不限流。
    /// 默认放宽到 1000：NAT 网关或下游 resolver 会让大量用户共用一个源 IP，
    /// 阈值过低会误伤正常流量。
    /// </summary>
    public int MaxQueriesPerSecondPerClient { get; set; } = 1000;

    /// <summary>同时处理的查询数上限，防止每包一个 Task 导致的资源耗尽</summary>
    public int MaxConcurrentQueries { get; set; } = 2048;

    /// <summary>TCP 并发连接数上限</summary>
    public int MaxConcurrentTcpConnections { get; set; } = 256;

    /// <summary>TCP 读写超时（毫秒），防御 slowloris</summary>
    public int TcpTimeoutMilliseconds { get; set; } = 5000;

    /// <summary>
    /// UDP socket 接收缓冲字节数。默认缓冲在突发流量下会溢出，
    /// 内核直接丢包，表现为客户端超时。
    /// </summary>
    public int SocketReceiveBufferBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>UDP socket 发送缓冲字节数</summary>
    public int SocketSendBufferBytes { get; set; } = 4 * 1024 * 1024;
}
