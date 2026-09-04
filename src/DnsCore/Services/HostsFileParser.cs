using System.Net;
using System.Net.Sockets;
using DnsCore.Models;
using DnsCore.Protocol;

namespace DnsCore.Services;

/// <summary>
/// hosts 文件解析结果。错误行保留文本，便于导入结果反馈到界面。
/// </summary>
public sealed record HostsParseResult(
    IReadOnlyList<DnsRecord> Records,
    IReadOnlyList<string> Errors);

/// <summary>
/// 解析标准 hosts 文件格式：IP 地址开头，后续列为主机名或别名。
/// </summary>
public static class HostsFileParser
{
    public static HostsParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<DnsRecord> records = [];
        List<string> errors = [];

        var lineNumber = 0;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;

            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // 支持行尾注释，例如：10.0.0.1 app.local # application
            var commentIndex = line.IndexOf('#');
            if (commentIndex >= 0)
                line = line[..commentIndex].Trim();

            if (line.Length == 0)
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                errors.Add($"第 {lineNumber} 行缺少主机名: {rawLine.Trim()}");
                continue;
            }

            if (!IPAddress.TryParse(parts[0], out var ip))
            {
                errors.Add($"第 {lineNumber} 行不是有效 IP 地址: {parts[0]}");
                continue;
            }

            var type = ip.AddressFamily == AddressFamily.InterNetworkV6
                ? DnsRecordType.AAAA
                : DnsRecordType.A;

            foreach (var rawHost in parts.Skip(1))
            {
                var host = rawHost.TrimEnd('.');
                try
                {
                    DnsWriter.ValidateDomainName(host);
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"第 {lineNumber} 行域名无效: {rawHost}（{ex.Message}）");
                    continue;
                }

                records.Add(new DnsRecord
                {
                    Domain = host,
                    Type = type,
                    Value = ip.ToString(),
                    TTL = 3600,
                    Weight = 1
                });
            }
        }

        return new HostsParseResult(records, errors);
    }
}
