using DnsCore.Configuration;
using DnsCore.Models;
using DnsCore.Protocol;
using DnsCore.Repositories;
using DnsCore.Services;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("========================================");
Console.WriteLine("         DNS Core Server");
Console.WriteLine("========================================");
Console.WriteLine();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// ==== 配置 ====
var dnsOptions = builder.Configuration.GetSection("DnsServer").Get<DnsServerOptions>() ?? new();
builder.Services.AddSingleton(dnsOptions);
builder.Services.AddSingleton(dnsOptions.Cache);

var apiSecurity = builder.Configuration.GetSection("ApiSecurity").Get<ApiSecurityOptions>() ?? new();

// 环境变量优先，避免把密钥写进配置文件并提交到仓库
apiSecurity.ApiKey = Environment.GetEnvironmentVariable("DNSCORE_API_KEY") ?? apiSecurity.ApiKey;

// 启用鉴权却没有 Key 属于危险的误配置：直接拒绝启动，而不是静默放行管理接口
if (apiSecurity.RequireApiKey && string.IsNullOrWhiteSpace(apiSecurity.ApiKey))
{
    throw new InvalidOperationException(
        "管理 API 已启用鉴权但未配置 API Key。请设置环境变量 DNSCORE_API_KEY，" +
        "或在配置中显式设置 ApiSecurity:RequireApiKey=false（仅限完全可信的隔离网络）。");
}

builder.Services.AddSingleton(apiSecurity);

// ==== 持久化 ====
builder.Services.AddSingleton<IDnsRecordRepository>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var persistence = dnsOptions.Persistence;

    logger.LogInformation("持久化提供者: {Provider}, 文件路径: {FilePath}",
        persistence.Provider, persistence.FilePath);

    return persistence.Provider switch
    {
        PersistenceProvider.JsonFile => new JsonFileRepository(persistence.FilePath),
        PersistenceProvider.Sqlite => new SqliteRepository(persistence.FilePath),
        PersistenceProvider.LiteDb => new LiteDbRepository(persistence.FilePath),
        _ => throw new InvalidOperationException($"不支持的持久化提供者: {persistence.Provider}")
    };
});

// ==== DNS 服务 ====
builder.Services.AddSingleton<CustomRecordStore>();
builder.Services.AddSingleton<DnsCache>();
builder.Services.AddSingleton<UpstreamDnsResolver>();
builder.Services.AddSingleton<UpstreamSettingsStore>();
builder.Services.AddSingleton<DnsServer>();
builder.Services.AddHostedService<DnsServerHostedService>();
builder.Services.AddHostedService<DnsCacheCleanupService>();

var app = builder.Build();

// 先载持久化记录，再合入配置文件中的初始记录（配置作为种子，不覆盖已有数据）
var customRecordStore = app.Services.GetRequiredService<CustomRecordStore>();
await customRecordStore.LoadFromPersistenceAsync();

if (dnsOptions.CustomRecords.Count > 0)
{
    // 批量添加只落盘一次；DnsServer 启动时不再重复加载同一批记录
    await customRecordStore.AddRecordsAsync(dnsOptions.CustomRecords);
    app.Logger.LogInformation("已合入配置文件中的 {Count} 条初始记录", dnsOptions.CustomRecords.Count);
}

// 加载前端保存过的上游设置，覆盖 appsettings.json 中的初始值。
// 必须在 DnsServer 启动（HostedService）之前完成，否则首批查询会用到旧配置。
var upstreamSettingsStore = app.Services.GetRequiredService<UpstreamSettingsStore>();
await upstreamSettingsStore.LoadAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// 管理 API 鉴权必须在路由处理之前
app.UseMiddleware<ApiSecurityMiddleware>();

// ==== 健康检查 ====
// 真实反映 DNS 监听状态：原实现恒返回 Healthy，DNS 端口挂了也看不出来
app.MapGet("/health", (DnsServer server, CustomRecordStore store) =>
{
    var healthy = server.IsListening;

    var payload = new
    {
        status = healthy ? "Healthy" : "Unhealthy",
        service = "DNS Core Server",
        dnsListening = server.IsListening,
        recordCount = store.Count,
        timestamp = DateTime.UtcNow
    };

    return healthy ? Results.Ok(payload) : Results.Json(payload, statusCode: 503);
})
.WithName("HealthCheck");

var dnsApi = app.MapGroup("/api/dns").WithTags("DNS Management");

// 获取全部自定义记录
dnsApi.MapGet("/records", (CustomRecordStore store) => Results.Ok(store.GetAllRecords()))
    .WithName("GetAllRecords");

// 添加自定义记录
dnsApi.MapPost("/records", async (DnsRecord record, CustomRecordStore store) =>
{
    if (!TryValidateRecord(record, out var error))
        return Results.BadRequest(new { error });

    await store.AddRecordAsync(record);
    return Results.Created($"/api/dns/records/{Uri.EscapeDataString(record.Domain)}/{record.Type}", record);
})
.WithName("AddRecord");

// 更新记录（先删后加，整组替换）
dnsApi.MapPut("/records/{domain}/{type}", async (
    string domain, string type, DnsRecord record, CustomRecordStore store) =>
{
    if (!Enum.TryParse<DnsRecordType>(type, ignoreCase: true, out var recordType))
        return Results.BadRequest(new { error = "无效的记录类型" });

    if (!TryValidateRecord(record, out var error))
        return Results.BadRequest(new { error });

    await store.RemoveRecordAsync(domain, recordType);
    await store.AddRecordAsync(record);
    return Results.Ok(record);
})
.WithName("UpdateRecord");

// 查询记录
dnsApi.MapGet("/records/{domain}/{type}", (string domain, string type, CustomRecordStore store) =>
{
    if (!Enum.TryParse<DnsRecordType>(type, ignoreCase: true, out var recordType))
        return Results.BadRequest(new { error = "无效的记录类型" });

    var records = store.Query(domain, recordType);
    return records is not null ? Results.Ok(records) : Results.NotFound();
})
.WithName("QueryRecord");

// 删除记录
dnsApi.MapDelete("/records/{domain}/{type}", async (string domain, string type, CustomRecordStore store) =>
{
    if (!Enum.TryParse<DnsRecordType>(type, ignoreCase: true, out var recordType))
        return Results.BadRequest(new { error = "无效的记录类型" });

    var removed = await store.RemoveRecordAsync(domain, recordType);
    return removed ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteRecord");

// 清空全部记录
dnsApi.MapDelete("/records", async (CustomRecordStore store) =>
{
    await store.ClearAsync();
    return Results.NoContent();
})
.WithName("ClearAllRecords");

// ==== 上游 DNS 配置 ====
// 注意：改写上游等于改写全部客户端的解析结果，属于高危操作。
// 该分组位于 /api 下，已被 ApiSecurityMiddleware 的鉴权与来源限制覆盖。
var upstreamApi = app.MapGroup("/api/upstream").WithTags("Upstream DNS");

upstreamApi.MapGet("/", (UpstreamSettingsStore store) => Results.Ok(store.GetStatus()))
    .WithName("GetUpstreamSettings");

upstreamApi.MapPut("/", async (UpstreamSettings settings, UpstreamSettingsStore store) =>
{
    var result = await store.SaveAsync(settings);

    return result.IsValid
        ? Results.Ok(store.GetStatus())
        : Results.BadRequest(new { error = result.Error });
})
.WithName("UpdateUpstreamSettings");

// ==== 缓存管理 ====
var cacheApi = app.MapGroup("/api/cache").WithTags("Cache Management");

cacheApi.MapGet("/stats", (DnsCache cache) => Results.Ok(cache.GetStats()))
    .WithName("GetCacheStats");

cacheApi.MapDelete("/", (DnsCache cache) =>
{
    cache.Clear();
    return Results.NoContent();
})
.WithName("ClearCache");

app.Logger.LogInformation("DNS Core Server 正在启动...");
app.Logger.LogInformation("DNS 监听: {Address}:{Port} (UDP/TCP)", dnsOptions.ListenAddress, dnsOptions.Port);
app.Logger.LogInformation("管理 API 鉴权: {State}", apiSecurity.RequireApiKey ? "已启用" : "已禁用");

if (!apiSecurity.RequireApiKey)
    app.Logger.LogWarning("管理 API 鉴权已禁用，任何可访问该端口的人都能修改 DNS 记录");

if (apiSecurity.EnableIpRestriction)
    app.Logger.LogInformation("管理 API 来源限制: {Networks}", string.Join(", ", apiSecurity.AllowedNetworks));
else
    app.Logger.LogWarning("管理 API 来源限制已禁用");

// 两道防线同时关闭时，管理接口对所有能访问该端口的人完全开放，
// 单独看任一条告警都不足以体现严重性，这里显式合并提示
if (!apiSecurity.RequireApiKey && !apiSecurity.EnableIpRestriction)
{
    app.Logger.LogWarning(
        "【安全风险】API Key 与来源限制均已关闭：/api/* 全部端点无任何防护，" +
        "任何人都可改写 DNS 记录与上游配置，进而劫持全部客户端的域名解析。" +
        "生产环境请设置 DNSCORE_API_KEY 并启用 ApiSecurity:RequireApiKey。");
}

if (!dnsOptions.Security.EnableClientRestriction)
{
    app.Logger.LogWarning(
        "DNS 客户端网段限制已禁用：本服务将应答任意来源的查询，" +
        "若暴露在公网会成为开放解析器（可被用于 DNS 放大攻击）。");
}

await app.RunAsync();

// ==== 校验 ====

/// <summary>
/// 入口即校验记录合法性：域名长度/label、TTL、以及该类型的值能否真正编码成 RDATA。
/// 原实现只查非空，非法值会一路带到应答编码阶段才抛异常，毁掉整个查询响应。
/// </summary>
static bool TryValidateRecord(DnsRecord? record, out string error)
{
    error = string.Empty;

    if (record is null)
    {
        error = "请求体不能为空";
        return false;
    }

    if (string.IsNullOrWhiteSpace(record.Domain))
    {
        error = "Domain 不能为空";
        return false;
    }

    if (string.IsNullOrWhiteSpace(record.Value))
    {
        error = "Value 不能为空";
        return false;
    }

    // TTL 上限取 RFC 2181 建议的 2^31-1 秒
    if (record.TTL <= 0 || record.TTL > int.MaxValue / 2)
    {
        error = "TTL 必须大于 0 且不超过 1073741823";
        return false;
    }

    // 泛域名的 "*" 不参与域名语法校验
    var nameToCheck = record.Domain.StartsWith("*.", StringComparison.Ordinal)
        ? record.Domain[2..]
        : record.Domain;

    try
    {
        // 写入路径启用严格字符集：拒绝引号/尖括号等，
        // 避免非法域名存入后回显到管理界面成为注入载荷
        DnsWriter.ValidateDomainName(nameToCheck, strictCharset: true);
    }
    catch (ArgumentException ex)
    {
        error = ex.Message;
        return false;
    }

    if (record.Type == DnsRecordType.ANY)
    {
        error = "ANY 不能作为记录类型写入";
        return false;
    }

    if (!DnsRdataWriter.TryValidate(record.Type, record.Value, out var rdataError))
    {
        error = rdataError ?? $"记录类型 {record.Type} 的值非法";
        return false;
    }

    return true;
}
