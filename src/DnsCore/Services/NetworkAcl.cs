using System.Net;
using System.Net.Sockets;

namespace DnsCore.Services;

/// <summary>
/// CIDR 网段访问控制表。用于限制可查询 DNS 的客户端与可访问管理 API 的来源。
/// </summary>
public sealed class NetworkAcl
{
    private readonly List<(IPAddress Network, int PrefixLength)> _entries = [];

    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// 从 CIDR 字符串列表构建；单个 IP（无 "/"）视为全长前缀。
    /// 非法条目会抛异常，避免配置写错却静默放行。
    /// </summary>
    public NetworkAcl(IEnumerable<string>? cidrs)
    {
        foreach (var raw in cidrs ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var entry = raw.Trim();
            var slashIndex = entry.IndexOf('/');

            if (slashIndex < 0)
            {
                if (!IPAddress.TryParse(entry, out var single))
                    throw new FormatException($"非法的网段配置: {raw}");

                _entries.Add((single, single.GetAddressBytes().Length * 8));
                continue;
            }

            var addressPart = entry[..slashIndex];
            var prefixPart = entry[(slashIndex + 1)..];

            if (!IPAddress.TryParse(addressPart, out var network)
                || !int.TryParse(prefixPart, out var prefixLength))
                throw new FormatException($"非法的 CIDR 配置: {raw}");

            var maxPrefix = network.GetAddressBytes().Length * 8;
            if (prefixLength < 0 || prefixLength > maxPrefix)
                throw new FormatException($"CIDR 前缀长度超出范围: {raw}");

            _entries.Add((network, prefixLength));
        }
    }

    /// <summary>判断地址是否在允许列表内；列表为空视为允许</summary>
    public bool IsAllowed(IPAddress? address)
    {
        if (address is null)
            return false;

        if (_entries.Count == 0)
            return true;

        // IPv4-mapped IPv6（::ffff:a.b.c.d）需还原为 IPv4 再比对，
        // 否则双栈监听下 IPv4 客户端会被 IPv4 规则漏判
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        foreach (var (network, prefixLength) in _entries)
        {
            if (Matches(candidate, network, prefixLength))
                return true;

            // 同时允许以映射形式匹配 IPv6 规则
            if (candidate.AddressFamily == AddressFamily.InterNetwork
                && network.AddressFamily == AddressFamily.InterNetworkV6
                && Matches(candidate.MapToIPv6(), network, prefixLength))
                return true;
        }

        return false;
    }

    private static bool Matches(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();

        if (addressBytes.Length != networkBytes.Length)
            return false;

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
