namespace DnsCore.Models;

/// <summary>
/// DNS 记录类型
/// </summary>
public enum DnsRecordType : ushort
{
    A = 1,      // IPv4 地址
    NS = 2,     // 名称服务器
    CNAME = 5,  // 规范名称
    SOA = 6,    // 授权开始
    PTR = 12,   // 指针记录
    MX = 15,    // 邮件交换
    TXT = 16,   // 文本
    AAAA = 28,  // IPv6 地址
    SRV = 33,   // 服务定位
    OPT = 41,   // EDNS0 伪记录（RFC 6891）
    CAA = 257,  // 证书颁发机构授权
    ANY = 255   // 任意类型
}

/// <summary>
/// DNS 协议常量与限制（RFC 1035 / 6891）
/// </summary>
public static class DnsLimits
{
    /// <summary>单个 label 最大字节数</summary>
    public const int MaxLabelLength = 63;

    /// <summary>域名线格式最大字节数</summary>
    public const int MaxDomainNameLength = 255;

    /// <summary>无 EDNS0 时 UDP 应答最大字节数</summary>
    public const int MaxUdpMessageSize = 512;

    /// <summary>DNS 报文最大字节数（TCP 两字节长度前缀上限）</summary>
    public const int MaxMessageSize = 65535;

    /// <summary>本服务器声明的 EDNS0 接收缓冲上限</summary>
    public const int MaxEdnsPayloadSize = 4096;

    /// <summary>单段 TXT 字符串最大字节数</summary>
    public const int MaxTxtChunkLength = 255;

    /// <summary>压缩指针最大跳转次数，防御指针环</summary>
    public const int MaxCompressionJumps = 16;

    /// <summary>单个查询允许的最大 question 数（正常查询恒为 1）</summary>
    public const int MaxQuestionCount = 4;
}
