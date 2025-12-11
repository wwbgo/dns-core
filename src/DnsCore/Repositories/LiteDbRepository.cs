using LiteDB;
using DnsCore.Models;

namespace DnsCore.Repositories;

/// <summary>
/// 基于 LiteDB 的 DNS 记录仓储实现
/// </summary>
public sealed class LiteDbRepository : IDnsRecordRepository, IDisposable
{
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<DnsRecord> _collection;

    public LiteDbRepository(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _database = new LiteDatabase(databasePath);
        _collection = _database.GetCollection<DnsRecord>("dns_records");

        // 创建索引以提高查询性能
        _collection.EnsureIndex(x => x.Domain);
        _collection.EnsureIndex(x => x.Type);
    }

    public Task<IEnumerable<DnsRecord>> LoadAllAsync()
    {
        var records = _collection.FindAll().ToList();
        return Task.FromResult<IEnumerable<DnsRecord>>(records);
    }

    public Task SaveAllAsync(IEnumerable<DnsRecord> records)
    {
        _collection.DeleteAll();
        _collection.InsertBulk(records);
        return Task.CompletedTask;
    }

    public Task AddAsync(DnsRecord record)
    {
        // 删除已存在的相同记录
        _collection.DeleteMany(r =>
            r.Domain.Equals(record.Domain, StringComparison.OrdinalIgnoreCase) &&
            r.Type == record.Type);

        _collection.Insert(record);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string domain, DnsRecordType type)
    {
        _collection.DeleteMany(r =>
            r.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
            r.Type == type);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        _collection.DeleteAll();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _database?.Dispose();
    }
}
