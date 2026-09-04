using System.Text.Json;
using DnsCore.Models;

namespace DnsCore.Services;

/// <summary>
/// hosts URL 来源的 JSON 持久化存储。
/// </summary>
public sealed class HostsSourceStore(
    ILogger<HostsSourceStore> logger,
    string filePath)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private List<HostsSource> _sources = [];

    public async Task LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
                return;

            var json = await File.ReadAllTextAsync(filePath);
            _sources = JsonSerializer.Deserialize<List<HostsSource>>(json, _jsonOptions) ?? [];
            NormalizeLoadedSources();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载 hosts URL 来源失败");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<HostsSource>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return [.. _sources];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HostsSource> AddAsync(
        string name,
        string url,
        int syncIntervalMinutes,
        int ttl)
    {
        Validate(name, url, syncIntervalMinutes, ttl);

        await _lock.WaitAsync();
        try
        {
            if (_sources.Any(s =>
                    string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("该 URL 已存在");
            }

            var source = new HostsSource
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
                Url = url.Trim(),
                SyncIntervalMinutes = syncIntervalMinutes,
                Ttl = ttl
            };

            _sources.Add(source);
            await SaveAsync();
            return source;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var removed = _sources.RemoveAll(s => s.Id == id) > 0;
            if (removed)
                await SaveAsync();

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateSyncStatusAsync(
        string id,
        DateTime syncedAtUtc,
        string? error)
    {
        await _lock.WaitAsync();
        try
        {
            var source = _sources.FirstOrDefault(s => s.Id == id);
            if (source is null)
                return;

            source.LastSyncedAtUtc = syncedAtUtc;
            source.LastSyncError = error;
            await SaveAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void NormalizeLoadedSources()
    {
        foreach (var source in _sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
                source.Id = Guid.NewGuid().ToString("N");

            source.Name = string.IsNullOrWhiteSpace(source.Name) ? "未命名来源" : source.Name.Trim();
            source.Url = source.Url?.Trim() ?? string.Empty;
            source.SyncIntervalMinutes = source.SyncIntervalMinutes is < 1 or > 10080
                ? 60
                : source.SyncIntervalMinutes;
            source.Ttl = source.Ttl is <= 0 or > int.MaxValue / 2
                ? 3600
                : source.Ttl;
        }
    }

    private async Task SaveAsync()
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_sources, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    private static void Validate(
        string name,
        string url,
        int syncIntervalMinutes,
        int ttl)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("名称不能为空", nameof(name));

        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("URL 必须是有效的 http/https 地址", nameof(url));
        }

        if (syncIntervalMinutes is < 1 or > 10080)
            throw new ArgumentException("同步周期必须在 1 到 10080 分钟之间", nameof(syncIntervalMinutes));

        if (ttl is <= 0 or > int.MaxValue / 2)
            throw new ArgumentException("TTL 必须大于 0", nameof(ttl));
    }
}
