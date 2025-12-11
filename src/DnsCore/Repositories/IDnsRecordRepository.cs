using DnsCore.Models;

namespace DnsCore.Repositories;

/// <summary>
/// DNS 记录仓储接口，定义持久化操作
/// </summary>
public interface IDnsRecordRepository
{
    /// <summary>
    /// 加载所有 DNS 记录
    /// </summary>
    Task<IEnumerable<DnsRecord>> LoadAllAsync();

    /// <summary>
    /// 保存所有 DNS 记录
    /// </summary>
    Task SaveAllAsync(IEnumerable<DnsRecord> records);

    /// <summary>
    /// 添加单条记录
    /// </summary>
    Task AddAsync(DnsRecord record);

    /// <summary>
    /// 删除指定记录
    /// </summary>
    Task DeleteAsync(string domain, DnsRecordType type);

    /// <summary>
    /// 清空所有记录
    /// </summary>
    Task ClearAsync();
}
