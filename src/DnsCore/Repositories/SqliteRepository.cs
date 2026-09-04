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

        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS DnsRecords (
                Domain TEXT NOT NULL,
                Type TEXT NOT NULL,
                Value TEXT NOT NULL,
                TTL INTEGER NOT NULL,
                Weight INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (Domain, Type, Value, TTL, Weight)
            )";

        using (var command = new SqliteCommand(createTableSql, connection))
        {
            command.ExecuteNonQuery();
        }

        MigrateSchema(connection);
    }

    /// <summary>
    /// 兼容两个历史版本：
    /// 1. 初版主键为 (Domain, Type)，同域名同类型只能存一个值；
    /// 2. 多值版主键为 (Domain, Type, Value, TTL)，但没有权重列。
    /// SQLite 不能直接改主键，因此复制数据到新表后替换旧表。
    /// </summary>
    private static void MigrateSchema(SqliteConnection connection)
    {
        var hasWeight = HasColumn(connection, "Weight");
        var primaryKeyIncludesWeight = PrimaryKeyIncludes(connection, "Weight");

        if (hasWeight && primaryKeyIncludesWeight)
            return;

        using var transaction = connection.BeginTransaction();

        const string createNewSql = @"
            CREATE TABLE DnsRecords_weighted (
                Domain TEXT NOT NULL,
                Type TEXT NOT NULL,
                Value TEXT NOT NULL,
                TTL INTEGER NOT NULL,
                Weight INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (Domain, Type, Value, TTL, Weight)
            )";

        ExecuteNonQuery(connection, transaction, createNewSql);

        var weightSelect = hasWeight ? "Weight" : "1";
        ExecuteNonQuery(connection, transaction, $@"
            INSERT INTO DnsRecords_weighted (Domain, Type, Value, TTL, Weight)
            SELECT Domain, Type, Value, TTL, {weightSelect} FROM DnsRecords");
        ExecuteNonQuery(connection, transaction, "DROP TABLE DnsRecords");
        ExecuteNonQuery(connection, transaction, "ALTER TABLE DnsRecords_weighted RENAME TO DnsRecords");

        transaction.Commit();
    }

    private static bool HasColumn(SqliteConnection connection, string columnName)
    {
        using var command = new SqliteCommand($@"
            SELECT COUNT(*)
            FROM pragma_table_info('DnsRecords')
            WHERE name = '{columnName}'", connection);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static bool PrimaryKeyIncludes(SqliteConnection connection, string columnName)
    {
        using var command = new SqliteCommand($@"
            SELECT COUNT(*)
            FROM pragma_table_info('DnsRecords')
            WHERE pk > 0 AND name = '{columnName}'", connection);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = new SqliteCommand(sql, connection, transaction);
        command.ExecuteNonQuery();
    }

    public async Task<IEnumerable<DnsRecord>> LoadAllAsync()
    {
        await _dbLock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "SELECT Domain, Type, Value, TTL, Weight FROM DnsRecords";
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
                    TTL = reader.GetInt32(3),
                    Weight = reader.GetInt32(4)
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
                var insertSql = @"
                    INSERT INTO DnsRecords (Domain, Type, Value, TTL, Weight)
                    VALUES (@Domain, @Type, @Value, @TTL, @Weight)";
                foreach (var record in records)
                {
                    using var insertCommand = new SqliteCommand(insertSql, connection, transaction);
                    insertCommand.Parameters.AddWithValue("@Domain", record.Domain);
                    insertCommand.Parameters.AddWithValue("@Type", record.Type.ToString());
                    insertCommand.Parameters.AddWithValue("@Value", record.Value);
                    insertCommand.Parameters.AddWithValue("@TTL", record.TTL);
                    insertCommand.Parameters.AddWithValue("@Weight", record.Weight);
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
                INSERT OR REPLACE INTO DnsRecords (Domain, Type, Value, TTL, Weight)
                VALUES (@Domain, @Type, @Value, @TTL, @Weight)";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@Domain", record.Domain);
            command.Parameters.AddWithValue("@Type", record.Type.ToString());
            command.Parameters.AddWithValue("@Value", record.Value);
            command.Parameters.AddWithValue("@TTL", record.TTL);
            command.Parameters.AddWithValue("@Weight", record.Weight);

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
