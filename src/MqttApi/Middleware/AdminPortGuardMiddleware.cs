using Microsoft.Extensions.Options;
using MqttApi.Configuration;
using MqttApi.Constants;

namespace MqttApi.Middleware;

/// Keeps the admin UI on its own port: the admin listener serves nothing but /admin
/// paths, and every other listener serves no /admin path at all.
public class AdminPortGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AdminOptions _options;
    private readonly ILogger<AdminPortGuardMiddleware> _logger;

    public AdminPortGuardMiddleware(
        RequestDelegate next,
        IOptions<AdminOptions> options,
        ILogger<AdminPortGuardMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isAdminPath = context.Request.Path
            .StartsWithSegments(ApiRoutes.AdminRoot, StringComparison.OrdinalIgnoreCase);

        if (!_options.Enabled)
        {
            if (isAdminPath) context.Response.StatusCode = StatusCodes.Status404NotFound;
            else await _next(context);
            return;
        }

        var isAdminPort = context.Connection.LocalPort == _options.Port;

        if (isAdminPort == isAdminPath)
        {
            await _next(context);
            return;
        }

        if (!isAdminPort)
        {
            _logger.LogWarning(
                "Blocked {Method} {Path} on public port {Port} - the admin UI is only available on port {AdminPort}",
                context.Request.Method, context.Request.Path, context.Connection.LocalPort, _options.Port);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}

public static class AdminPortGuardMiddlewareExtensions
{
    public static IApplicationBuilder UseAdminPortGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<AdminPortGuardMiddleware>();
}
