using DnsCore.Models;
using DnsCore.Repositories;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace DnsCore.Services;

/// <summary>
/// 自定义 DNS 记录存储。
/// value 使用 ImmutableList 并整体替换：原实现把可变 List 放进 ConcurrentDictionary，
/// 写入线程 list.Add 与查询线程复制会并发访问同一个非线程安全的 List。
/// </summary>
public sealed class CustomRecordStore(
    ILogger<CustomRecordStore> logger,
    IDnsRecordRepository? repository = null)
{
    private static readonly RecordKeyComparer KeyComparer = new();

    private readonly ConcurrentDictionary<RecordKey, ImmutableList<DnsRecord>> _records = new(KeyComparer);
    private readonly ConcurrentDictionary<string, byte> _domainIndex = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ImmutableList<DnsRecord>> _recordsByDomain = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    /// <summary>从持久化存储加载记录</summary>
    public async Task LoadFromPersistenceAsync()
    {
        if (repository is null)
        {
            logger.LogDebug("No persistence repository configured; skipping load");
            return;
        }

        try
        {
            var records = await repository.LoadAllAsync();

            var grouped = records
                .Where(r => r is not null)
                .GroupBy(r => GetKey(r.Domain, r.Type), KeyComparer)
                .ToDictionary(
                    g => g.Key,
                    // DnsRecord 是 record 类型，Distinct 直接按值语义去重
                    g => g.Distinct().ToImmutableList(),
                    KeyComparer);

            _records.Clear();
            foreach (var (key, value) in grouped)
                _records[key] = value;

            RebuildIndexes();

            var totalCount = _records.Values.Sum(list => list.Count);
            logger.LogInformation("Loaded {Count} records from persistence storage (deduplicated)", totalCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load records from persistence storage");
        }
    }

    /// <summary>保存到持久化存储</summary>
    private async Task SaveToPersistenceAsync()
    {
        if (repository is null)
            return;

        await _persistLock.WaitAsync();
        try
        {
            var allRecords = GetAllRecords().ToList();
            await repository.SaveAllAsync(allRecords);
            logger.LogDebug("Saved {Count} records to persistence storage", allRecords.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save records to persistence storage");
        }
        finally
        {
            _persistLock.Release();
        }
    }

    /// <summary>
    /// 尝试插入一条记录，返回是否实际新增。
    /// AddOrUpdate 的委托在竞争下可能被多次调用，因此用返回值判断而非捕获变量。
    /// </summary>
    private bool TryInsert(DnsRecord record)
    {
        var key = GetKey(record.Domain, record.Type);

        while (true)
        {
            if (_records.TryGetValue(key, out var existing))
            {
                if (existing.Contains(record))
                    return false;

                if (_records.TryUpdate(key, existing.Add(record), existing))
                {
                    RegisterDomainRecord(record);
                    return true;
                }
            }
            else if (_records.TryAdd(key, [record]))
            {
                RegisterDomainRecord(record);
                return true;
            }
            // CAS 失败说明有并发写入，重试
        }
    }

    /// <summary>添加自定义记录</summary>
    public async Task AddRecordAsync(DnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (TryInsert(record))
        {
            logger.LogInformation("Added custom record: {Record}", record);
            await SaveToPersistenceAsync();
        }
        else
        {
            logger.LogDebug("Record already exists; skipped: {Record}", record);
        }
    }

    /// <summary>添加自定义记录（同步版本，保持向后兼容）</summary>
    public void AddRecord(DnsRecord record)
        => AddRecordAsync(record).GetAwaiter().GetResult();

    /// <summary>
    /// 批量添加记录。全部插入完成后只落盘一次：
    /// 原实现每条都写全表，批量导入是 O(n²)。
    /// </summary>
    public async Task AddRecordsAsync(IEnumerable<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var addedCount = 0;

        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (TryInsert(record))
            {
                logger.LogDebug("Added custom record: {Record}", record);
                addedCount++;
            }
        }

        if (addedCount > 0)
        {
            logger.LogInformation("Added {Count} custom records in batch", addedCount);
            await SaveToPersistenceAsync();
        }
    }

    /// <summary>批量添加记录（同步版本，保持向后兼容）</summary>
    public void AddRecords(IEnumerable<DnsRecord> records)
        => AddRecordsAsync(records).GetAwaiter().GetResult();

    /// <summary>
    /// 查询自定义记录（支持泛域名）。
    /// 泛域名命中时把 owner name 改写为实际查询名：
    /// 直接返回 "*.example.com" 会让客户端因 owner 与 question 不匹配而丢弃应答。
    /// </summary>
    public List<DnsRecord>? Query(string domain, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var queryName = domain.TrimEnd('.');
        var normalizedQueryName = NormalizeDomain(queryName);

        // 1. 精确匹配
        if (_records.TryGetValue(GetKey(queryName, type), out var exact))
        {
            logger.LogDebug("Custom record matched exactly: {Domain} {Type}", queryName, type);
            return [.. exact];
        }

        // 2. 泛域名匹配
        var wildcard = FindWildcardMatch(queryName, type);
        if (wildcard is not null)
        {
            logger.LogDebug("Custom record matched by wildcard: {Domain} {Type}", queryName, type);
            return wildcard;
        }

        // 3. ANY 查询：通过域名二级索引返回，不扫描全部记录键
        if (type == DnsRecordType.ANY
            && _recordsByDomain.TryGetValue(normalizedQueryName, out var allRecords)
            && !allRecords.IsEmpty)
        {
            logger.LogDebug("Custom record matched ANY query: {Domain}", queryName);
            return [.. allRecords];
        }

        logger.LogDebug("Custom record not found: {Domain} {Type}", queryName, type);
        return null;
    }

    /// <summary>
    /// 判断该域名是否存在任意类型的本地记录（精确或泛域名匹配）。
    ///
    /// 用于区分 NXDOMAIN 与 NODATA：域名在本地存在、但没有被查询的那个类型时，
    /// 应就地返回 NODATA，而不是转发上游——否则像 test.cc 这种仅存在于本地的
    /// 域名，其 AAAA 查询会一直打到上游并等满超时（nslookup 每次都查 A+AAAA，
    /// 表现为固定 2 秒卡顿）。
    /// </summary>
    public bool ContainsDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var queryName = NormalizeDomain(domain);

        if (_domainIndex.ContainsKey(queryName))
            return true;

        // 与 FindWildcardMatch 一样，从最具体的泛域名开始向宽泛后缀匹配。
        // 通过 _domainIndex 判断，避免每个未命中查询都遍历全部记录类型。
        for (var dot = queryName.IndexOf('.'); dot >= 0; dot = queryName.IndexOf('.', dot + 1))
        {
            var wildcardDomain = string.Concat("*".AsSpan(), queryName.AsSpan(dot));
            if (_domainIndex.ContainsKey(wildcardDomain))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 该域名下是否存在任意类型的记录。
    ///
    /// 走 O(1) 字典查找而非遍历 _records.Keys：后者是 O(记录数 × 域名层级)，
    /// 实测 1 万条记录时单次调用达 1.1ms（约为 Query 的 1100 倍），
    /// 而这是每个未命中查询的必经路径，会直接拖垮吞吐。
    /// </summary>
    private bool HasAnyType(string domain)
    {
        foreach (var type in StorableRecordTypes)
        {
            if (_records.ContainsKey(GetKey(domain, type)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 可能作为存储键出现的记录类型。
    ///
    /// 只排除 OPT：它是 EDNS0 伪记录，仅存在于报文中，不会被当作记录存储。
    /// ANY 虽被 API 层拒绝写入，但配置文件的 CustomRecords 与历史持久化文件
    /// 不经过该校验，因此仍纳入查找，避免漏判导致本该 NODATA 的查询被转发上游。
    /// </summary>
    private static readonly DnsRecordType[] StorableRecordTypes =
        [.. Enum.GetValues<DnsRecordType>().Where(t => t is not DnsRecordType.OPT)];

    /// <summary>
    /// 查找泛域名匹配，从最具体到最宽泛。
    /// 例：api.dev.example.com 依次匹配 *.dev.example.com、*.example.com、*.com
    /// </summary>
    private List<DnsRecord>? FindWildcardMatch(string domain, DnsRecordType type)
    {
        // 逐级放宽，但直接定位点号，不再构造标签数组和重复 Join。
        for (var dot = domain.IndexOf('.'); dot >= 0; dot = domain.IndexOf('.', dot + 1))
        {
            var wildcardDomain = string.Concat("*".AsSpan(), domain.AsSpan(dot));

            if (_records.TryGetValue(GetKey(wildcardDomain, type), out var records))
            {
                logger.LogDebug("Wildcard match: {Domain} -> {WildcardDomain}", domain, wildcardDomain);

                // owner name 必须改写为客户端查询的名字
                var result = new List<DnsRecord>(records.Count);
                foreach (var record in records)
                    result.Add(record with { Domain = domain });

                return result;
            }
        }

        return null;
    }

    /// <summary>删除记录</summary>
    public async Task<bool> RemoveRecordAsync(string domain, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var normalizedDomain = NormalizeDomain(domain);
        var removed = _records.TryRemove(GetKey(normalizedDomain, type), out _);

        if (removed)
        {
            RemoveDomainTypeFromIndex(normalizedDomain, type);
            RemoveDomainIndexIfUnused(normalizedDomain);
            logger.LogInformation("Deleted custom record: {Domain} {Type}", domain, type);
            await SaveToPersistenceAsync();
        }

        return removed;
    }

    /// <summary>删除指定值的单条记录，同域名同类型下其它值保持不变</summary>
    public async Task<bool> RemoveRecordAsync(string domain, DnsRecordType type, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalizedDomain = NormalizeDomain(domain);
        var key = GetKey(normalizedDomain, type);
        const int MaxRetries = 100;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (!_records.TryGetValue(key, out var existing))
                return false;

            var match = existing.FirstOrDefault(r =>
                string.Equals(r.Value, value, StringComparison.Ordinal));

            if (match is null)
                return false;

            var updated = existing.Remove(match);
            if (updated.IsEmpty)
            {
                var pair = KeyValuePair.Create(key, existing);
                if (((ICollection<KeyValuePair<RecordKey, ImmutableList<DnsRecord>>>)_records).Remove(pair))
                {
                    RemoveDomainRecordFromIndex(normalizedDomain, match);
                    RemoveDomainIndexIfUnused(normalizedDomain);
                    logger.LogInformation("Deleted custom record: {Domain} {Type} {Value}", domain, type, value);
                    await SaveToPersistenceAsync();
                    return true;
                }

                // CAS 失败说明有并发写入，让出时间片后重试
                await Task.Yield();
                continue;
            }

            if (_records.TryUpdate(key, updated, existing))
            {
                RemoveDomainRecordFromIndex(normalizedDomain, match);
                logger.LogInformation("Deleted custom record: {Domain} {Type} {Value}", domain, type, value);
                await SaveToPersistenceAsync();
                return true;
            }

            // CAS 失败说明有并发写入，让出时间片后重试
            await Task.Yield();
        }

        throw new InvalidOperationException(
            $"删除记录失败：超过最大重试次数 ({MaxRetries})，存在高并发冲突");
    }

    /// <summary>删除指定值的单条记录（同步版本）</summary>
    public bool RemoveRecord(string domain, DnsRecordType type, string value)
        => RemoveRecordAsync(domain, type, value).GetAwaiter().GetResult();

    /// <summary>删除记录（同步版本，保持向后兼容）</summary>
    public bool RemoveRecord(string domain, DnsRecordType type)
        => RemoveRecordAsync(domain, type).GetAwaiter().GetResult();

    /// <summary>清空所有记录</summary>
    public async Task ClearAsync()
    {
        _records.Clear();
        _domainIndex.Clear();
        _recordsByDomain.Clear();
        logger.LogInformation("Cleared all custom records");
        await SaveToPersistenceAsync();
    }

    /// <summary>清空所有记录（同步版本，保持向后兼容）</summary>
    public void Clear() => ClearAsync().GetAwaiter().GetResult();

    /// <summary>获取所有记录</summary>
    public IEnumerable<DnsRecord> GetAllRecords()
        => _records.Values.SelectMany(records => records);

    /// <summary>记录总数</summary>
    public int Count => _records.Values.Sum(list => list.Count);

    private static RecordKey GetKey(string domain, DnsRecordType type)
        => new(domain, type);

    private static string NormalizeDomain(string domain)
        => domain.TrimEnd('.').ToLowerInvariant();

    private void RegisterDomainRecord(DnsRecord record)
    {
        var normalizedDomain = NormalizeDomain(record.Domain);
        _domainIndex.TryAdd(normalizedDomain, 0);

        while (true)
        {
            if (_recordsByDomain.TryGetValue(normalizedDomain, out var existing))
            {
                if (existing.Contains(record)
                    || _recordsByDomain.TryUpdate(normalizedDomain, existing.Add(record), existing))
                {
                    return;
                }
            }
            else if (_recordsByDomain.TryAdd(normalizedDomain, [record]))
            {
                return;
            }
        }
    }

    private void RemoveDomainTypeFromIndex(string normalizedDomain, DnsRecordType type)
    {
        while (true)
        {
            if (!_recordsByDomain.TryGetValue(normalizedDomain, out var existing))
                return;

            var updated = existing.RemoveAll(r => r.Type == type);
            if (updated.IsEmpty)
            {
                var pair = KeyValuePair.Create(normalizedDomain, existing);
                if (((ICollection<KeyValuePair<string, ImmutableList<DnsRecord>>>)_recordsByDomain).Remove(pair))
                    return;
            }
            else if (_recordsByDomain.TryUpdate(normalizedDomain, updated, existing))
            {
                return;
            }
        }
    }

    private void RemoveDomainRecordFromIndex(string normalizedDomain, DnsRecord record)
    {
        while (true)
        {
            if (!_recordsByDomain.TryGetValue(normalizedDomain, out var existing))
                return;

            var updated = existing.Remove(record);
            if (updated.IsEmpty)
            {
                var pair = KeyValuePair.Create(normalizedDomain, existing);
                if (((ICollection<KeyValuePair<string, ImmutableList<DnsRecord>>>)_recordsByDomain).Remove(pair))
                    return;
            }
            else if (_recordsByDomain.TryUpdate(normalizedDomain, updated, existing))
            {
                return;
            }
        }
    }

    private void RemoveDomainIndexIfUnused(string normalizedDomain)
    {
        if (!HasAnyType(normalizedDomain))
            _domainIndex.TryRemove(normalizedDomain, out _);
    }

    private void RebuildIndexes()
    {
        _domainIndex.Clear();
        _recordsByDomain.Clear();

        foreach (var record in _records.Values.SelectMany(records => records))
        {
            var normalizedDomain = NormalizeDomain(record.Domain);
            _domainIndex.TryAdd(normalizedDomain, 0);
            _recordsByDomain.AddOrUpdate(
                normalizedDomain,
                [record],
                (_, existing) => existing.Contains(record) ? existing : existing.Add(record));
        }
    }

    private readonly record struct RecordKey(string Domain, DnsRecordType Type);

    private sealed class RecordKeyComparer : IEqualityComparer<RecordKey>
    {
        public bool Equals(RecordKey x, RecordKey y)
            => x.Type == y.Type
               && x.Domain.AsSpan().TrimEnd('.').Equals(
                   y.Domain.AsSpan().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(RecordKey key)
        {
            var hash = new HashCode();
            var domain = key.Domain.AsSpan().TrimEnd('.');

            foreach (var c in domain)
                hash.Add(char.ToLowerInvariant(c));

            hash.Add((ushort)key.Type);
            return hash.ToHashCode();
        }
    }
}
