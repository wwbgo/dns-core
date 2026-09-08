using System.Text.Json;
using DnsCore.Models;

namespace DnsCore.Services;

/// <summary>
/// 单个 hosts URL 来源当前拥有的记录。
/// </summary>
public sealed record HostsOwnedRecord(
    string Domain,
    DnsRecordType Type,
    string Value,
    int TTL,
    int Weight = 1);

/// <summary>
/// 记录 URL 来源导入的 DNS 记录归属关系。
///
/// 仅靠 CustomRecordStore 无法知道一条记录是用户手动创建，还是来自某个 hosts URL。
/// 该存储维护 sourceId -> recordKey 和 recordKey -> sourceIds 两个反向索引，
/// 使自动同步可以安全地清理某个来源已消失的记录。
/// </summary>
public sealed class HostsSourceRecordStore(
    ILogger<HostsSourceRecordStore> logger,
    string filePath)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly Dictionary<string, Dictionary<string, HostsOwnedRecord>> _recordsBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _sourceIdsByRecord =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
                return;

            var json = await File.ReadAllTextAsync(filePath);

            _recordsBySource.Clear();
            _sourceIdsByRecord.Clear();

            var loaded = TryReadNestedFormat(json);
            if (loaded is not null)
            {
                LoadNested(loaded);
                return;
            }

            var legacy = TryReadLegacyFormat(json);
            if (legacy is not null)
            {
                foreach (var (sourceId, records) in legacy)
                {
                    foreach (var record in records)
                    {
                        var key = GetKey(record);
                        AddToIndexes(sourceId, key, record);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load hosts source record ownership");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<HostsOwnedRecord>> GetSourceRecordsAsync(string sourceId)
    {
        await _lock.WaitAsync();
        try
        {
            return _recordsBySource.TryGetValue(sourceId, out var records)
                ? [.. records.Values]
                : [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetSourceIdsForRecordAsync(
        string domain,
        DnsRecordType type,
        string value)
    {
        var key = GetRecordKey(domain, type, value);

        await _lock.WaitAsync();
        try
        {
            return _sourceIdsByRecord.TryGetValue(key, out var owners)
                ? [.. owners]
                : [];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>批量读取记录来源，避免记录列表接口逐条加锁。</summary>
    public async Task<Dictionary<string, IReadOnlyList<string>>> GetSourceIdsByRecordAsync(
        IReadOnlyList<DnsRecord> records)
    {
        await _lock.WaitAsync();
        try
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in records)
            {
                var key = GetRecordKey(record.Domain, record.Type, record.Value);
                result[key] = _sourceIdsByRecord.TryGetValue(key, out var owners)
                    ? [.. owners]
                    : [];
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static string GetRecordKey(string domain, DnsRecordType type, string value)
        => $"{domain.TrimEnd('.').ToLowerInvariant()}|{(ushort)type}|{value}";

    public async Task AddOwnershipAsync(string sourceId, HostsOwnedRecord record)
    {
        var key = GetKey(record);

        await _lock.WaitAsync();
        try
        {
            if (!_recordsBySource.TryGetValue(sourceId, out var records))
            {
                records = new Dictionary<string, HostsOwnedRecord>(StringComparer.OrdinalIgnoreCase);
                _recordsBySource[sourceId] = records;
            }

            records[key] = record;

            if (!_sourceIdsByRecord.TryGetValue(key, out var owners))
            {
                owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _sourceIdsByRecord[key] = owners;
            }

            owners.Add(sourceId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>移除来源对记录的归属；返回该记录剩余的归属来源数。</summary>
    public async Task<int> RemoveOwnershipAsync(string sourceId, string key)
    {
        await _lock.WaitAsync();
        try
        {
            if (_recordsBySource.TryGetValue(sourceId, out var records))
            {
                records.Remove(key);
                if (records.Count == 0)
                    _recordsBySource.Remove(sourceId);
            }

            if (_sourceIdsByRecord.TryGetValue(key, out var owners))
            {
                owners.Remove(sourceId);
                if (owners.Count == 0)
                    _sourceIdsByRecord.Remove(key);
            }

            return _sourceIdsByRecord.TryGetValue(key, out var current)
                ? current.Count
                : 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_recordsBySource, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    private Dictionary<string, Dictionary<string, HostsOwnedRecord>>? TryReadNestedFormat(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, HostsOwnedRecord>>>(
                json,
                _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Dictionary<string, List<HostsOwnedRecord>>? TryReadLegacyFormat(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<HostsOwnedRecord>>>(
                json,
                _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void LoadNested(Dictionary<string, Dictionary<string, HostsOwnedRecord>> loaded)
    {
        foreach (var (sourceId, recordsByKey) in loaded)
        {
            foreach (var (key, record) in recordsByKey)
                AddToIndexes(sourceId, key, record);
        }
    }

    private void AddToIndexes(string sourceId, string key, HostsOwnedRecord record)
    {
        if (!_recordsBySource.TryGetValue(sourceId, out var records))
        {
            records = new Dictionary<string, HostsOwnedRecord>(StringComparer.OrdinalIgnoreCase);
            _recordsBySource[sourceId] = records;
        }

        records[key] = record;

        if (!_sourceIdsByRecord.TryGetValue(key, out var owners))
        {
            owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _sourceIdsByRecord[key] = owners;
        }

        owners.Add(sourceId);
    }

    private static string GetKey(HostsOwnedRecord record)
        => GetRecordKey(record.Domain, record.Type, record.Value);
}
