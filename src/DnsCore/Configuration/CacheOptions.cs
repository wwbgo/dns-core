namespace DnsCore.Configuration;

/// <summary>
/// DNS 缓存配置。原实现 maxEntries 与默认 TTL 硬编码在构造函数里，无法配置。
/// </summary>
public sealed class CacheOptions
{
    /// <summary>是否启用缓存</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>最大缓存条目数</summary>
    public int MaxEntries { get; set; } = 10000;

    /// <summary>
    /// TTL 上限（秒）。上游返回的超长 TTL 会被夹到该值，
    /// 避免恶意/错误配置的上游长期占据缓存。
    /// </summary>
    public int MaxTtlSeconds { get; set; } = 86400;

    /// <summary>TTL 下限（秒），避免 TTL=0 的记录写入即失效、白做一次插入</summary>
    public int MinTtlSeconds { get; set; } = 5;

    /// <summary>否定应答（NXDOMAIN / NODATA）缓存时长（秒），0 表示不缓存</summary>
    public int NegativeTtlSeconds { get; set; } = 60;

    /// <summary>过期条目清理间隔（秒）</summary>
    public int CleanupIntervalSeconds { get; set; } = 60;
}
