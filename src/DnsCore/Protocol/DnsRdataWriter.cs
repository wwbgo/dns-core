using DnsCore.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DnsCore.Protocol;

/// <summary>
/// RDATA 编码。原实现对 MX/SOA/SRV 静默返回空 RDATA（rdlen=0），
/// 客户端会收到语法合法但语义为空的记录；这里补全编码并对不支持的类型显式拒绝。
/// </summary>
public static class DnsRdataWriter
{
    /// <summary>
    /// 判断记录类型 + 值是否可被编码，用于 API 层入口校验，
    /// 避免非法值一路带到编码阶段才炸掉整个应答。
    /// </summary>
    public static bool TryValidate(DnsRecordType type, string value, out string? error)
    {
        error = null;
        try
        {
            var probe = new DnsWriter(256);
            Write(probe, type, value);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>写入指定类型的 RDATA（不含 RDLENGTH，由调用方回填）</summary>
    public static void Write(DnsWriter writer, DnsRecordType type, string value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        switch (type)
        {
            case DnsRecordType.A:
                WriteIPv4(writer, value);
                break;

            case DnsRecordType.AAAA:
                WriteIPv6(writer, value);
                break;

            case DnsRecordType.CNAME:
            case DnsRecordType.NS:
            case DnsRecordType.PTR:
                // RFC 1035：CNAME/NS/PTR 的 RDATA 允许压缩
                writer.WriteDomainName(value);
                break;

            case DnsRecordType.TXT:
                WriteTxt(writer, value);
                break;

            case DnsRecordType.MX:
                WriteMx(writer, value);
                break;

            case DnsRecordType.SRV:
                WriteSrv(writer, value);
                break;

            case DnsRecordType.SOA:
                WriteSoa(writer, value);
                break;

            case DnsRecordType.CAA:
                WriteCaa(writer, value);
                break;

            default:
                throw new NotSupportedException($"不支持的 DNS 记录类型: {type}");
        }
    }

    private static void WriteIPv4(DnsWriter writer, string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"无效的 IPv4 地址: {value}", nameof(value));

        writer.WriteBytes(ip.GetAddressBytes());
    }

    private static void WriteIPv6(DnsWriter writer, string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException($"无效的 IPv6 地址: {value}", nameof(value));

        writer.WriteBytes(ip.GetAddressBytes());
    }

    /// <summary>
    /// TXT：按 255 字节分片。原实现 (byte)bytes.Length 强转，
    /// 超过 255 字节的文本会写出错误的长度前缀。
    /// </summary>
    private static void WriteTxt(DnsWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        if (bytes.Length == 0)
        {
            writer.WriteByte(0);
            return;
        }

        for (var offset = 0; offset < bytes.Length; offset += DnsLimits.MaxTxtChunkLength)
        {
            var chunk = Math.Min(DnsLimits.MaxTxtChunkLength, bytes.Length - offset);
            writer.WriteByte((byte)chunk);
            writer.WriteBytes(bytes.AsSpan(offset, chunk));
        }
    }

    /// <summary>MX 格式: "10 mail.example.com"</summary>
    private static void WriteMx(DnsWriter writer, string value)
    {
        var parts = value.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !ushort.TryParse(parts[0], out var preference))
            throw new ArgumentException($"无效的 MX 记录值，应为 \"<preference> <exchange>\": {value}", nameof(value));

        writer.WriteUInt16(preference);
        writer.WriteDomainName(parts[1]);
    }

    /// <summary>SRV 格式: "10 60 5060 sipserver.example.com"</summary>
    private static void WriteSrv(DnsWriter writer, string value)
    {
        var parts = value.Split(' ', 4, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !ushort.TryParse(parts[0], out var priority)
            || !ushort.TryParse(parts[1], out var weight)
            || !ushort.TryParse(parts[2], out var port))
            throw new ArgumentException(
                $"无效的 SRV 记录值，应为 \"<priority> <weight> <port> <target>\": {value}", nameof(value));

        writer.WriteUInt16(priority);
        writer.WriteUInt16(weight);
        writer.WriteUInt16(port);
        // RFC 2782：SRV 的 target 不压缩
        writer.WriteDomainName(parts[3], useCompression: false);
    }

    /// <summary>SOA 格式: "ns.example.com admin.example.com 2024010101 7200 3600 1209600 3600"</summary>
    private static void WriteSoa(DnsWriter writer, string value)
    {
        var parts = value.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 7)
            throw new ArgumentException(
                $"无效的 SOA 记录值，应为 \"<mname> <rname> <serial> <refresh> <retry> <expire> <minimum>\": {value}",
                nameof(value));

        var numbers = new uint[5];
        for (var i = 0; i < 5; i++)
        {
            if (!uint.TryParse(parts[i + 2], out numbers[i]))
                throw new ArgumentException($"SOA 记录第 {i + 3} 个字段应为无符号整数: {parts[i + 2]}", nameof(value));
        }

        writer.WriteDomainName(parts[0], useCompression: false);
        writer.WriteDomainName(parts[1], useCompression: false);
        foreach (var number in numbers)
            writer.WriteUInt32(number);
    }

    /// <summary>CAA 格式: "0 issue letsencrypt.org"</summary>
    private static void WriteCaa(DnsWriter writer, string value)
    {
        var parts = value.Split(' ', 3, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !byte.TryParse(parts[0], out var flags))
            throw new ArgumentException($"无效的 CAA 记录值，应为 \"<flags> <tag> <value>\": {value}", nameof(value));

        var tag = Encoding.ASCII.GetBytes(parts[1]);
        if (tag.Length is 0 or > 255)
            throw new ArgumentException($"CAA tag 长度非法: {parts[1]}", nameof(value));

        writer.WriteByte(flags);
        writer.WriteByte((byte)tag.Length);
        writer.WriteBytes(tag);
        writer.WriteBytes(Encoding.ASCII.GetBytes(parts[2].Trim('"')));
    }
}
