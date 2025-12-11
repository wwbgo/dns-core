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
    ANY = 255   // 任意类型
}
