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
    /// 是否启用自动保存
    /// </summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>
    /// 自动保存间隔（秒），0 表示每次修改立即保存
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
