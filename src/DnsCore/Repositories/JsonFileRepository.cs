using System.Text.Json;
using DnsCore.Models;

namespace DnsCore.Repositories;

/// <summary>
/// 基于 JSON 文件的 DNS 记录仓储实现
/// </summary>
public sealed class JsonFileRepository : IDnsRecordRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public JsonFileRepository(string filePath)
    {
        _filePath = filePath;
        EnsureDirectoryExists();
    }

    private void EnsureDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<IEnumerable<DnsRecord>> LoadAllAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var json = await File.ReadAllTextAsync(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            var records = JsonSerializer.Deserialize<List<DnsRecord>>(json, _jsonOptions);
            return records ?? [];
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAllAsync(IEnumerable<DnsRecord> records)
    {
        await _fileLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(records, _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task AddAsync(DnsRecord record)
    {
        var records = (await LoadAllAsync()).ToList();

        // 仅替换完全相同的记录，同域名同类型下的不同值应保留
        records.RemoveAll(r =>
            r.Domain.Equals(record.Domain, StringComparison.OrdinalIgnoreCase) &&
            r.Type == record.Type &&
            string.Equals(r.Value, record.Value, StringComparison.Ordinal) &&
            r.TTL == record.TTL &&
            r.Weight == record.Weight);

        records.Add(record);
        await SaveAllAsync(records);
    }

    public async Task DeleteAsync(string domain, DnsRecordType type)
    {
        var records = (await LoadAllAsync()).ToList();
        records.RemoveAll(r =>
            r.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
            r.Type == type);
        await SaveAllAsync(records);
    }

    public async Task ClearAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (File.Exists(_filePath))
            {
                await File.WriteAllTextAsync(_filePath, "[]");
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
