using DnsCore.Models;
using DnsCore.Repositories;
using System.Collections.Concurrent;

namespace DnsCore.Services;

/// <summary>
/// 自定义 DNS 记录存储
/// </summary>
public sealed class CustomRecordStore(
    ILogger<CustomRecordStore> logger,
    IDnsRecordRepository? repository = null)
{
    private readonly ConcurrentDictionary<string, List<DnsRecord>> _records = new();
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    /// <summary>
    /// 从持久化存储加载记录
    /// </summary>
    public async Task LoadFromPersistenceAsync()
    {
        if (repository is null)
        {
            logger.LogDebug("未配置持久化存储，跳过加载");
            return;
        }

        try
        {
            var records = await repository.LoadAllAsync();
            foreach (var record in records)
            {
                var key = GetKey(record.Domain, record.Type);
                _records.AddOrUpdate(
                    key,
                    _ => [record],
                    (_, list) =>
                    {
                        list.Add(record);
                        return list;
                    });
            }

            logger.LogInformation("从持久化存储加载了 {Count} 条记录", records.Count());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "从持久化存储加载记录失败");
        }
    }

    /// <summary>
    /// 保存到持久化存储
    /// </summary>
    private async Task SaveToPersistenceAsync()
    {
        if (repository is null)
        {
            return;
        }

        await _persistLock.WaitAsync();
        try
        {
            var allRecords = GetAllRecords().ToList();
            await repository.SaveAllAsync(allRecords);
            logger.LogDebug("保存了 {Count} 条记录到持久化存储", allRecords.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存记录到持久化存储失败");
        }
        finally
        {
            _persistLock.Release();
        }
    }

    /// <summary>
    /// 添加自定义记录
    /// </summary>
    public async Task AddRecordAsync(DnsRecord record)
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
        await SaveToPersistenceAsync();
    }

    /// <summary>
    /// 添加自定义记录（同步版本，用于向后兼容）
    /// </summary>
    public void AddRecord(DnsRecord record)
    {
        AddRecordAsync(record).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 批量添加记录
    /// </summary>
    public async Task AddRecordsAsync(IEnumerable<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        foreach (var record in records)
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

        await SaveToPersistenceAsync();
    }

    /// <summary>
    /// 批量添加记录（同步版本，用于向后兼容）
    /// </summary>
    public void AddRecords(IEnumerable<DnsRecord> records)
    {
        AddRecordsAsync(records).GetAwaiter().GetResult();
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
    public async Task<bool> RemoveRecordAsync(string domain, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var key = GetKey(domain, type);
        var removed = _records.TryRemove(key, out _);

        if (removed)
        {
            logger.LogInformation("移除自定义记录: {Domain} {Type}", domain, type);
            await SaveToPersistenceAsync();
        }

        return removed;
    }

    /// <summary>
    /// 移除记录（同步版本，用于向后兼容）
    /// </summary>
    public bool RemoveRecord(string domain, DnsRecordType type)
    {
        return RemoveRecordAsync(domain, type).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 清空所有记录
    /// </summary>
    public async Task ClearAsync()
    {
        _records.Clear();
        logger.LogInformation("清空所有自定义记录");
        await SaveToPersistenceAsync();
    }

    /// <summary>
    /// 清空所有记录（同步版本，用于向后兼容）
    /// </summary>
    public void Clear()
    {
        ClearAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 获取所有记录
    /// </summary>
    public IEnumerable<DnsRecord> GetAllRecords() =>
        _records.Values.SelectMany(records => records);

    private static string GetKey(string domain, DnsRecordType type) =>
        $"{domain.ToLowerInvariant()}:{type}";
}
