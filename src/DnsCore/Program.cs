using System.Text;
using DnsCore.Configuration;
using DnsCore.Repositories;
using DnsCore.Services;

// Set console output encoding to UTF-8 (Fix Docker encoding issues)
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

Console.WriteLine("========================================");
Console.WriteLine("         DNS Core Server");
Console.WriteLine("========================================");
Console.WriteLine();

var builder = WebApplication.CreateBuilder(args);

// Configure services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure JSON serializer with camel case naming (matches frontend JSON format)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    // Support string to enum conversion (allows frontend to send "A" instead of number 1)
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// Add DNS server configuration
var dnsOptions = builder.Configuration.GetSection("DnsServer").Get<DnsServerOptions>() ?? new();
builder.Services.AddSingleton(dnsOptions);

// Register persistence repository
builder.Services.AddSingleton<IDnsRecordRepository>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var options = dnsOptions.Persistence;

    logger.LogInformation("Configured persistence provider: {Provider}, file path: {FilePath}",
        options.Provider, options.FilePath);

    return options.Provider switch
    {
        PersistenceProvider.JsonFile => new JsonFileRepository(options.FilePath),
        PersistenceProvider.Sqlite => new SqliteRepository(options.FilePath),
        PersistenceProvider.LiteDb => new LiteDbRepository(options.FilePath),
        _ => throw new InvalidOperationException($"Unsupported persistence provider: {options.Provider}")
    };
});

// Register DNS services
builder.Services.AddSingleton<CustomRecordStore>();
builder.Services.AddSingleton<DnsCache>(); // 性能优化：DNS 查询缓存
builder.Services.AddSingleton<UpstreamDnsResolver>();
builder.Services.AddSingleton<DnsServer>();
builder.Services.AddHostedService<DnsServerHostedService>();
builder.Services.AddHostedService<DnsCacheCleanupService>(); // 性能优化：缓存清理服务

var app = builder.Build();

// Load persisted DNS records
var customRecordStore = app.Services.GetRequiredService<CustomRecordStore>();
await customRecordStore.LoadFromPersistenceAsync();

// Then load initial records from configuration file (if any)
if (dnsOptions.CustomRecords.Count > 0)
{
    app.Logger.LogInformation("Loaded {Count} initial records from configuration file", dnsOptions.CustomRecords.Count);
    customRecordStore.AddRecords(dnsOptions.CustomRecords);
}

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Enable static files and default file (index.html)
app.UseDefaultFiles();
app.UseStaticFiles();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Service = "DNS Core Server",
    Timestamp = DateTime.UtcNow
}))
.WithName("Health Check");

// DNS Management API
var dnsApi = app.MapGroup("/api/dns").WithTags("DNS Management");

// Get all custom records
dnsApi.MapGet("/records", (CustomRecordStore store) =>
{
    var records = store.GetAllRecords();
    return Results.Ok(records);
})
.WithName("GetAllRecords");

// Add custom record
dnsApi.MapPost("/records", async (DnsCore.Models.DnsRecord record, CustomRecordStore store) =>
{
    try
    {
        // Validate required fields
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

// Delete custom record
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

// Query custom record
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

// Clear all custom records
dnsApi.MapDelete("/records", async (CustomRecordStore store) =>
{
    await store.ClearAsync();
    return Results.NoContent();
})
.WithName("ClearAllRecords");

app.Logger.LogInformation("DNS Core Server is starting...");
app.Logger.LogInformation("Listening on ports: UDP {DnsPort}, HTTP {HttpPort}",
    dnsOptions.Port, builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000");
app.Logger.LogInformation("Web management UI: {WebUrl}", builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000");

await app.RunAsync();
