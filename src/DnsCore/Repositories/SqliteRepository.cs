using System.Data;
using Microsoft.Data.Sqlite;
using DnsCore.Models;

namespace DnsCore.Repositories;

/// <summary>
/// 基于 SQLite 的 DNS 记录仓储实现
/// </summary>
public sealed class SqliteRepository : IDnsRecordRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    public SqliteRepository(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
        EnsureDatabaseExists();
    }

    private void EnsureDatabaseExists()
    {
        var directory = Path.GetDirectoryName(_connectionString.Replace("Data Source=", ""));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS DnsRecords (
                Domain TEXT NOT NULL,
                Type TEXT NOT NULL,
                Value TEXT NOT NULL,
                TTL INTEGER NOT NULL,
                PRIMARY KEY (Domain, Type)
            )";

        using var command = new SqliteCommand(createTableSql, connection);
        command.ExecuteNonQuery();
    }

    public async Task<IEnumerable<DnsRecord>> LoadAllAsync()
    {
        await _dbLock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "SELECT Domain, Type, Value, TTL FROM DnsRecords";
            using var command = new SqliteCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();

            var records = new List<DnsRecord>();
            while (await reader.ReadAsync())
            {
                records.Add(new DnsRecord
                {
                    Domain = reader.GetString(0),
                    Type = Enum.Parse<DnsRecordType>(reader.GetString(1)),
                    Value = reader.GetString(2),
                    TTL = reader.GetInt32(3)
                });
            }

            return records;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task SaveAllAsync(IEnumerable<DnsRecord> records)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            try
            {
                // 清空现有记录
                var deleteSql = "DELETE FROM DnsRecords";
                using (var deleteCommand = new SqliteCommand(deleteSql, connection, transaction))
                {
                    await deleteCommand.ExecuteNonQueryAsync();
                }

                // 插入新记录
                var insertSql = "INSERT INTO DnsRecords (Domain, Type, Value, TTL) VALUES (@Domain, @Type, @Value, @TTL)";
                foreach (var record in records)
                {
                    using var insertCommand = new SqliteCommand(insertSql, connection, transaction);
                    insertCommand.Parameters.AddWithValue("@Domain", record.Domain);
                    insertCommand.Parameters.AddWithValue("@Type", record.Type.ToString());
                    insertCommand.Parameters.AddWithValue("@Value", record.Value);
                    insertCommand.Parameters.AddWithValue("@TTL", record.TTL);
                    await insertCommand.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task AddAsync(DnsRecord record)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                INSERT OR REPLACE INTO DnsRecords (Domain, Type, Value, TTL)
                VALUES (@Domain, @Type, @Value, @TTL)";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@Domain", record.Domain);
            command.Parameters.AddWithValue("@Type", record.Type.ToString());
            command.Parameters.AddWithValue("@Value", record.Value);
            command.Parameters.AddWithValue("@TTL", record.TTL);

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task DeleteAsync(string domain, DnsRecordType type)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "DELETE FROM DnsRecords WHERE Domain = @Domain AND Type = @Type";
            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@Domain", domain);
            command.Parameters.AddWithValue("@Type", type.ToString());

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _dbLock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "DELETE FROM DnsRecords";
            using var command = new SqliteCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void Dispose()
    {
        _dbLock.Dispose();
    }
}
