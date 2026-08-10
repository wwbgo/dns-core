namespace DnsCore.Configuration;

/// <summary>
/// 持久化配置选项
/// </summary>
public sealed class PersistenceOptions
{
    /// <summary>
    /// 持久化提供者类型
    /// </summary>
    public PersistenceProvider Provider { get; set; } = PersistenceProvider.JsonFile;

    /// <summary>
    /// 数据文件路径（用于 JSON/SQLite/LiteDB）
    /// </summary>
    public string FilePath { get; set; } = "data/dns-records.json";

    /// <summary>
    /// 是否启用自动保存。
    ///
    /// 【当前未接线】记录变更一律立即落盘，此选项不产生任何效果。
    /// 保留是为了兼容既有配置文件（改名/删除会让老配置报错）。
    /// 若要实现延迟批量落盘，需同时处理优雅停机时的刷盘，
    /// 否则进程被强杀会丢失尚未写入的变更。
    /// </summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>
    /// 自动保存间隔（秒），0 表示每次修改立即保存。
    /// 【当前未接线】同 <see cref="AutoSave"/>，实际行为固定为立即保存。
    /// </summary>
    public int AutoSaveInterval { get; set; } = 0;
}

/// <summary>
/// 持久化提供者类型
/// </summary>
public enum PersistenceProvider
{
    /// <summary>
    /// JSON 文件存储
    /// </summary>
    JsonFile,

    /// <summary>
    /// SQLite 数据库
    /// </summary>
    Sqlite,

    /// <summary>
    /// LiteDB 数据库
    /// </summary>
    LiteDb
}
