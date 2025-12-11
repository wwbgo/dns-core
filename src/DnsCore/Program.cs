using System.Text;
using DnsCore.Configuration;
using DnsCore.Repositories;
using DnsCore.Services;

// 设置控制台输出编码为 UTF-8（修复 Docker 中文乱码）
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

Console.WriteLine("========================================");
Console.WriteLine("      DNS Core Server - DNS 服务器");
Console.WriteLine("========================================");
Console.WriteLine();

var builder = WebApplication.CreateBuilder(args);

// 配置服务
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 配置 JSON 序列化器支持驼峰命名（与前端 JSON 格式匹配）
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    // 支持字符串到枚举的转换（允许前端发送 "A" 而不是数字 1）
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// 添加 DNS 服务器配置
var dnsOptions = builder.Configuration.GetSection("DnsServer").Get<DnsServerOptions>() ?? new();
builder.Services.AddSingleton(dnsOptions);

// 注册持久化仓储
builder.Services.AddSingleton<IDnsRecordRepository>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var options = dnsOptions.Persistence;

    logger.LogInformation("配置持久化提供者: {Provider}, 文件路径: {FilePath}",
        options.Provider, options.FilePath);

    return options.Provider switch
    {
        PersistenceProvider.JsonFile => new JsonFileRepository(options.FilePath),
        PersistenceProvider.Sqlite => new SqliteRepository(options.FilePath),
        PersistenceProvider.LiteDb => new LiteDbRepository(options.FilePath),
        _ => throw new InvalidOperationException($"不支持的持久化提供者: {options.Provider}")
    };
});

// 注册 DNS 服务
builder.Services.AddSingleton<CustomRecordStore>();
builder.Services.AddSingleton<UpstreamDnsResolver>();
builder.Services.AddSingleton<DnsServer>();
builder.Services.AddHostedService<DnsServerHostedService>();

var app = builder.Build();

// 加载持久化的 DNS 记录
var customRecordStore = app.Services.GetRequiredService<CustomRecordStore>();
await customRecordStore.LoadFromPersistenceAsync();

// 然后加载配置文件中的初始记录（如果有的话）
if (dnsOptions.CustomRecords.Count > 0)
{
    app.Logger.LogInformation("从配置文件加载 {Count} 条初始记录", dnsOptions.CustomRecords.Count);
    customRecordStore.AddRecords(dnsOptions.CustomRecords);
}

// 配置中间件
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 启用静态文件和默认文件（index.html）
app.UseDefaultFiles();
app.UseStaticFiles();

// 健康检查端点
app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "DNS Core Server",
    Timestamp = DateTime.UtcNow
}))
.WithName("Health Check");

// DNS 管理 API
var dnsApi = app.MapGroup("/api/dns").WithTags("DNS Management");

// 获取所有自定义记录
dnsApi.MapGet("/records", (CustomRecordStore store) =>
{
    var records = store.GetAllRecords();
    return Results.Ok(records);
})
.WithName("GetAllRecords");

// 添加自定义记录
dnsApi.MapPost("/records", async (DnsCore.Models.DnsRecord record, CustomRecordStore store) =>
{
    try
    {
        // 验证必填字段
        if (string.IsNullOrWhiteSpace(record.Domain))
        {
            return Results.BadRequest(new { error = "Domain is required" });
        }

        if (string.IsNullOrWhiteSpace(record.Value))
        {
            return Results.BadRequest(new { error = "Value is required" });
        }

        if (record.TTL <= 0)
        {
            return Results.BadRequest(new { error = "TTL must be greater than 0" });
        }

        await store.AddRecordAsync(record);
        return Results.Created($"/api/dns/records/{record.Domain}", record);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("AddRecord");

// 删除自定义记录
dnsApi.MapDelete("/records/{domain}/{type}", async (string domain, string type, CustomRecordStore store) =>
{
    if (!Enum.TryParse<DnsCore.Models.DnsRecordType>(type, ignoreCase: true, out var recordType))
    {
        return Results.BadRequest(new { Error = "Invalid record type" });
    }

    var removed = await store.RemoveRecordAsync(domain, recordType);
    return removed ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteRecord");

// 查询自定义记录
dnsApi.MapGet("/records/{domain}/{type}", (string domain, string type, CustomRecordStore store) =>
{
    if (!Enum.TryParse<DnsCore.Models.DnsRecordType>(type, ignoreCase: true, out var recordType))
    {
        return Results.BadRequest(new { Error = "Invalid record type" });
    }

    var records = store.Query(domain, recordType);
    return records is not null ? Results.Ok(records) : Results.NotFound();
})
.WithName("QueryRecord");

// 清空所有自定义记录
dnsApi.MapDelete("/records", async (CustomRecordStore store) =>
{
    await store.ClearAsync();
    return Results.NoContent();
})
.WithName("ClearAllRecords");

app.Logger.LogInformation("DNS Core Server 正在启动...");
app.Logger.LogInformation("监听端口: UDP {DnsPort}, HTTP {HttpPort}",
    dnsOptions.Port, builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000");
app.Logger.LogInformation("Web 管理界面: {WebUrl}", builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000");

await app.RunAsync();
