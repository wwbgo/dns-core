using DnsCore.Configuration;
using System.Net;
using System.Text.Json;

namespace DnsCore.Services;

/// <summary>校验结果</summary>
public sealed record SettingsValidation(bool IsValid, string? Error = null)
{
    public static readonly SettingsValidation Ok = new(true);
    public static SettingsValidation Fail(string error) => new(false, error);
}

/// <summary>
/// 上游设置的运行时读写与持久化。
///
/// 生效方式：直接改写单例 DnsServerOptions 的属性。
/// DnsServer 与 UpstreamDnsResolver 都是在每次查询时读取这些属性
/// （而非构造时快照），因此改动立即对后续查询生效，无需重启。
/// </summary>
public sealed class UpstreamSettingsStore(
    ILogger<UpstreamSettingsStore> logger,
    DnsServerOptions options,
    UpstreamDnsResolver resolver,
    DnsCache cache)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>设置文件路径：与记录数据同目录，但独立文件</summary>
    private string SettingsPath
    {
        get
        {
            var dir = Path.GetDirectoryName(options.Persistence.FilePath);
            return Path.Combine(string.IsNullOrEmpty(dir) ? "data" : dir, "upstream-settings.json");
        }
    }

    /// <summary>启动时加载持久化的设置并覆盖配置文件中的初始值</summary>
    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                logger.LogDebug("未找到上游设置文件，使用配置文件中的初始值");
                return;
            }

            var json = await File.ReadAllTextAsync(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var settings = JsonSerializer.Deserialize<UpstreamSettings>(json, _jsonOptions);
            if (settings is null)
                return;

            // 持久化的值可能是历史遗留的非法数据，仍需校验
            var validation = Validate(settings);
            if (!validation.IsValid)
            {
                logger.LogWarning("持久化的上游设置非法（{Error}），忽略并使用配置文件的值", validation.Error);
                return;
            }

            Apply(settings, persistedAlready: true);
            logger.LogInformation("已从 {Path} 加载上游设置", SettingsPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载上游设置失败，使用配置文件中的值");
        }
    }

    /// <summary>读取当前状态，含实际生效的上游列表</summary>
    public UpstreamStatus GetStatus()
    {
        var configured = options.UpstreamDnsServers;
        var effective = resolver.GetEffectiveServers();

        return new UpstreamStatus
        {
            EnableUpstreamDnsQuery = options.EnableUpstreamDnsQuery,
            UpstreamDnsServers = [.. configured],
            TimeoutMilliseconds = options.Upstream.TimeoutMilliseconds,
            RaceUpstreams = options.Upstream.RaceUpstreams,
            EffectiveServers = [.. effective.Select(ip => ip.ToString())],
            // 未显式配置时，生效列表来自系统探测
            UsingSystemDns = configured.Count == 0
        };
    }

    /// <summary>校验待保存的设置</summary>
    public SettingsValidation Validate(UpstreamSettings settings)
    {
        if (settings is null)
            return SettingsValidation.Fail("请求体不能为空");

        if (settings.TimeoutMilliseconds is < 200 or > 30000)
            return SettingsValidation.Fail("超时时间必须在 200–30000 毫秒之间");

        var servers = settings.UpstreamDnsServers ?? [];

        if (servers.Count > 16)
            return SettingsValidation.Fail("上游服务器最多 16 个");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in servers)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return SettingsValidation.Fail("上游服务器地址不能为空");

            var trimmed = raw.Trim();

            if (!TryParseStrict(trimmed, out var ip))
                return SettingsValidation.Fail($"无效的 IP 地址: {trimmed}（需填写完整 IP，不支持域名）");

            if (!seen.Add(ip.ToString()))
                return SettingsValidation.Fail($"上游服务器地址重复: {trimmed}");

            // 把上游指向本机会形成查询环：未命中 -> 转发给自己 -> 再次未命中
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                return SettingsValidation.Fail($"不能将上游指向本机地址 {trimmed}，会形成查询环路");
        }

        // 关闭上游转发时不要求配置服务器；开启时空列表意味着回落系统 DNS，也是合法的
        return SettingsValidation.Ok;
    }

    /// <summary>
    /// 严格解析 IP 地址。
    ///
    /// IPAddress.TryParse 接受 inet_aton 简写：
    /// "223.5.5" 会被解析成 223.5.0.5，"10.1" 会变成 10.0.0.1，且不报错。
    /// 上游地址少打一位就静默指向另一台服务器，因此 IPv4 必须要求四段完整写法。
    /// IPv6 的规范形式允许多种等价写法（大小写、:: 压缩），只做格式校验。
    /// </summary>
    private static bool TryParseStrict(string value, out IPAddress address)
    {
        address = IPAddress.None;

        if (!IPAddress.TryParse(value, out var parsed))
            return false;

        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 必须是 a.b.c.d 四段，且每段都是 0–255 的十进制且无前导零歧义
            var parts = value.Split('.');
            if (parts.Length != 4)
                return false;

            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 3)
                    return false;

                if (!byte.TryParse(part, out _))
                    return false;
            }
        }

        address = parsed;
        return true;
    }

    /// <summary>保存并立即生效</summary>
    public async Task<SettingsValidation> SaveAsync(UpstreamSettings settings)
    {
        var validation = Validate(settings);
        if (!validation.IsValid)
            return validation;

        await _writeLock.WaitAsync();
        try
        {
            Apply(settings, persistedAlready: false);

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(SettingsPath, json);

            logger.LogInformation(
                "上游设置已更新：转发={Enabled}, 模式={Mode}, 超时={Timeout}ms, 服务器=[{Servers}]",
                settings.EnableUpstreamDnsQuery,
                settings.RaceUpstreams ? "并行竞速" : "顺序尝试",
                settings.TimeoutMilliseconds,
                string.Join(", ", settings.UpstreamDnsServers ?? []));

            return SettingsValidation.Ok;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存上游设置失败");
            return SettingsValidation.Fail($"保存失败: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>把设置应用到运行中的单例选项</summary>
    private void Apply(UpstreamSettings settings, bool persistedAlready)
    {
        options.EnableUpstreamDnsQuery = settings.EnableUpstreamDnsQuery;
        options.Upstream.TimeoutMilliseconds = settings.TimeoutMilliseconds;
        options.Upstream.RaceUpstreams = settings.RaceUpstreams;

        // 整体替换列表引用，避免其他线程读到半更新状态
        options.UpstreamDnsServers = [.. settings.UpstreamDnsServers ?? []];

        // 重建 resolver 内部已解析的地址数组
        resolver.SetUpstreamServers(options.UpstreamDnsServers);

        // 换了上游就必须清缓存：旧上游的结果可能与新上游不一致
        // （典型场景是从公网 DNS 切到内网 DNS，同一域名解析结果完全不同）
        if (!persistedAlready)
        {
            cache.Clear();
            logger.LogInformation("上游变更，已清空 DNS 缓存");
        }
    }
}
