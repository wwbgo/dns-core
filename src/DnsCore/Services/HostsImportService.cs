using System.Net;
using System.Net.Sockets;
using DnsCore.Models;

namespace DnsCore.Services;

/// <summary>
/// hosts 导入结果。
/// </summary>
public sealed record HostsImportResult(
    int Imported,
    int SkippedDuplicates,
    IReadOnlyList<string> Errors);

/// <summary>
/// 将 hosts 文本或 URL 内容导入到自定义 DNS 记录。
/// </summary>
public sealed class HostsImportService(
    ILogger<HostsImportService> logger,
    CustomRecordStore recordStore,
    IHttpClientFactory? httpClientFactory = null,
    bool allowLoopback = false)
{
    private static readonly TimeSpan UrlTimeout = TimeSpan.FromSeconds(10);
    private const int MaxUrlBytes = 1024 * 1024;

    public async Task<HostsImportResult> ImportTextAsync(string text, int ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ttl = NormalizeTtl(ttl);

        var parsed = HostsFileParser.Parse(text);
        var imported = await AddUniqueRecordsAsync(parsed.Records, ttl);

        logger.LogInformation(
            "hosts 文本导入完成：解析 {Parsed} 条，导入 {Imported} 条，跳过重复 {Skipped} 条",
            parsed.Records.Count,
            imported.Imported,
            imported.SkippedDuplicates);

        return new HostsImportResult(
            imported.Imported,
            imported.SkippedDuplicates,
            [.. parsed.Errors, .. imported.Errors]);
    }

    public async Task<HostsImportResult> ImportUrlAsync(string url, int ttl)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("请输入有效的 URL", nameof(url));

        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("仅支持 http/https hosts URL", nameof(url));

        ValidateRemoteUrl(uri, allowLoopback);

        if (httpClientFactory is null)
            throw new InvalidOperationException("未配置 HttpClientFactory，无法导入 URL");

        using var client = httpClientFactory.CreateClient();
        client.Timeout = UrlTimeout;

        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxUrlBytes)
            throw new InvalidOperationException("hosts URL 内容超过 1MB 限制");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxUrlBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (read == 0)
                break;

            total += read;
        }

        if (total >= buffer.Length && reader.Peek() >= 0)
            throw new InvalidOperationException("hosts URL 内容超过 1MB 限制");

        var text = new string(buffer, 0, total);
        return await ImportTextAsync(text, ttl);
    }

    private async Task<HostsImportResult> AddUniqueRecordsAsync(
        IReadOnlyList<DnsRecord> parsedRecords,
        int ttl)
    {
        var existing = recordStore.GetAllRecords().ToList();
        HashSet<string> existingKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (var record in existing)
            existingKeys.Add(GetDnsKey(record));

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<DnsRecord> toAdd = [];
        List<string> errors = [];
        var skipped = 0;

        foreach (var source in parsedRecords)
        {
            var key = GetDnsKey(source);
            if (!seen.Add(key))
            {
                skipped++;
                continue;
            }

            if (existingKeys.Contains(key))
            {
                skipped++;
                continue;
            }

            try
            {
                var record = source with { TTL = ttl, Weight = 1 };
                toAdd.Add(record);
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Domain} {source.Type} {source.Value}: {ex.Message}");
            }
        }

        if (toAdd.Count > 0)
            await recordStore.AddRecordsAsync(toAdd);

        return new HostsImportResult(toAdd.Count, skipped, errors);
    }

    private static void ValidateRemoteUrl(Uri uri, bool allowLoopback)
    {
        if (allowLoopback)
            return;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不允许导入 localhost 地址的 hosts URL", nameof(uri));

        if (!IPAddress.TryParse(host, out var ip))
            return;

        if (IPAddress.IsLoopback(ip)
            || ip.Equals(IPAddress.Any)
            || ip.Equals(IPAddress.IPv6Any)
            || ip.IsIPv6LinkLocal)
        {
            throw new ArgumentException("不允许导入环回或链路本地地址的 hosts URL", nameof(uri));
        }

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork
            && bytes.Length == 4
            && bytes[0] == 169
            && bytes[1] == 254)
        {
            throw new ArgumentException("不允许导入链路本地地址的 hosts URL", nameof(uri));
        }
    }

    private static int NormalizeTtl(int ttl)
        => ttl is > 0 and <= int.MaxValue / 2 ? ttl : 3600;

    private static string GetDnsKey(DnsRecord record)
        => $"{record.Domain.TrimEnd('.').ToLowerInvariant()}|{(ushort)record.Type}|{record.Value}";
}
