using DnsCore.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace DnsCore.Services;

/// <summary>
/// 管理 API 鉴权与来源限制中间件。
/// 原实现的管理 API 完全无认证：任何能访问 HTTP 端口的人都能改写 DNS 记录。
/// </summary>
public sealed class ApiSecurityMiddleware(
    RequestDelegate next,
    ILogger<ApiSecurityMiddleware> logger,
    ApiSecurityOptions options)
{
    private readonly NetworkAcl _acl = new(
        options.EnableIpRestriction ? options.AllowedNetworks : null);

    private readonly byte[] _expectedKey = Encoding.UTF8.GetBytes(options.ApiKey ?? string.Empty);

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        // 只保护管理接口；健康检查与静态资源放行
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress;

        if (options.EnableIpRestriction && !_acl.IsAllowed(remoteIp))
        {
            logger.LogWarning("拒绝来自 {Ip} 的管理 API 请求：不在允许网段内", remoteIp);
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "来源地址不被允许");
            return;
        }

        if (options.RequireApiKey && !IsKeyValid(context))
        {
            logger.LogWarning("拒绝来自 {Ip} 的管理 API 请求：API Key 无效", remoteIp);
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "缺少或无效的 API Key");
            return;
        }

        await next(context);
    }

    private bool IsKeyValid(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(options.HeaderName, out var provided))
            return false;

        var providedBytes = Encoding.UTF8.GetBytes(provided.ToString());

        // 定长时间比较，避免通过响应时间侧信道逐字节爆破 Key
        return CryptographicOperations.FixedTimeEquals(providedBytes, _expectedKey);
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { error = detail });
    }
}
