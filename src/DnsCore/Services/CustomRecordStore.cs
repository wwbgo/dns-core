using DnsCore.Models;
using System.Collections.Concurrent;

namespace DnsCore.Services;

/// <summary>
/// 自定义 DNS 记录存储
/// </summary>
public sealed class CustomRecordStore(ILogger<CustomRecordStore> logger)
{
    private readonly ConcurrentDictionary<string, List<DnsRecord>> _records = new();

    /// <summary>
    /// 添加自定义记录
    /// </summary>
    public void AddRecord(DnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var key = GetKey(record.Domain, record.Type);
        _records.AddOrUpdate(
            key,
            _ => [record],
            (_, list) =>
            {
                list.Add(record);
                return list;
            });

        logger.LogInformation("添加自定义记录: {Record}", record);
    }

    /// <summary>
    /// 批量添加记录
    /// </summary>
    public void AddRecords(IEnumerable<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        foreach (var record in records)
        {
            AddRecord(record);
        }
    }

    /// <summary>
    /// 查询自定义记录（支持泛域名）
    /// </summary>
    public List<DnsRecord>? Query(string domain, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        // 1. 精确匹配
        var key = GetKey(domain, type);
        if (_records.TryGetValue(key, out var records))
        {
            logger.LogDebug("找到自定义记录（精确匹配）: {Domain} {Type}", domain, type);
            return [..records];
        }

        // 2. 泛域名匹配（*.example.com）
        var wildcardRecords = FindWildcardMatch(domain, type);
        if (wildcardRecords is not null)
        {
            logger.LogDebug("找到自定义记录（泛域名匹配）: {Domain} {Type}", domain, type);
            return wildcardRecords;
        }

        // 3. 如果查询 ANY 类型，返回该域名的所有记录（包括泛域名）
        if (type == DnsRecordType.ANY)
        {
            var prefix = $"{domain.ToLowerInvariant()}:";
            var allRecords = _records
                .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(kvp => kvp.Value)
                .ToList();

            if (allRecords.Count > 0)
            {
                logger.LogDebug("找到自定义记录（ANY）: {Domain}", domain);
                return allRecords;
            }
        }

        logger.LogDebug("未找到自定义记录: {Domain} {Type}", domain, type);
        return null;
    }

    /// <summary>
    /// 查找泛域名匹配的记录
    /// 从最具体到最不具体的顺序匹配泛域名
    /// 例如: api.dev.example.com 会按顺序匹配:
    /// 1. *.dev.example.com
    /// 2. *.example.com
    /// 3. *.com
    /// </summary>
    private List<DnsRecord>? FindWildcardMatch(string domain, DnsRecordType type)
    {
        var parts = domain.Split('.');

        // 域名至少要有两部分才能匹配泛域名（如 example.com）
        if (parts.Length < 2)
        {
            return null;
        }

        // 从最具体到最不具体的泛域名
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var wildcardDomain = "*." + string.Join('.', parts.Skip(i + 1));
            var key = GetKey(wildcardDomain, type);

            if (_records.TryGetValue(key, out var records))
            {
                logger.LogDebug("泛域名匹配: {Domain} -> {WildcardDomain}", domain, wildcardDomain);
                return [..records];
            }
        }

        return null;
    }

    /// <summary>
    /// 移除记录
    /// </summary>
    public bool RemoveRecord(string domain, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var key = GetKey(domain, type);
        var removed = _records.TryRemove(key, out _);

        if (removed)
        {
            logger.LogInformation("移除自定义记录: {Domain} {Type}", domain, type);
        }

        return removed;
    }

    /// <summary>
    /// 清空所有记录
    /// </summary>
    public void Clear()
    {
        _records.Clear();
        logger.LogInformation("清空所有自定义记录");
    }

    /// <summary>
    /// 获取所有记录
    /// </summary>
    public IEnumerable<DnsRecord> GetAllRecords() =>
        _records.Values.SelectMany(records => records);

    private static string GetKey(string domain, DnsRecordType type) =>
        $"{domain.ToLowerInvariant()}:{type}";
}
