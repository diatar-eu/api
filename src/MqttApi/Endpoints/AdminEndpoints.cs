using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using MqttApi.Constants;
using MqttApi.Models;
using MqttApi.Models.Admin;
using MqttApi.Models.Dynsec;
using MqttApi.Models.Requests;
using MqttApi.Models.Responses;
using MqttApi.Services;

namespace MqttApi.Endpoints;

public static class AdminEndpoints
{
    private const string ActorKey = "admin.actor";
    private const string AdminUserKey = "__adminUser";

    private static readonly string[] UiStrings =
    [
        "admin_title", "admin_login_title", "admin_login_intro", "admin_username", "admin_password",
        "admin_login_button", "admin_logout", "admin_login_failed", "admin_login_rate_limited",
        "admin_not_configured", "admin_search_placeholder", "admin_filter_all", "admin_filter_verified",
        "admin_filter_pending", "admin_filter_disabled", "admin_column_user", "admin_column_email",
        "admin_column_status", "admin_column_roles", "admin_column_actions", "admin_new_user",
        "admin_create_user", "admin_skip_verification", "admin_no_users", "admin_loading",
        "admin_action_verify", "admin_action_enable", "admin_action_disable", "admin_action_resend",
        "admin_action_password", "admin_action_email", "admin_action_delete", "admin_confirm_delete",
        "admin_confirm_disable", "admin_confirm_verify", "admin_new_password", "admin_new_email",
        "admin_refresh", "admin_not_diatar_user", "admin_yes", "admin_no", "admin_cancel", "admin_save",
        "user_created", "user_deleted", "password_updated", "username_taken", "email_in_use",
        "user_not_found", "admin_user_verified", "admin_user_enabled", "admin_user_disabled",
        "admin_password_saved", "admin_email_saved", "admin_verification_resent", "admin_verification_not_needed",
        "admin_cannot_disable_self", "admin_cannot_delete_self", "admin_mqtt_client"
    ];

    public static void Map(WebApplication app)
    {
        var pages = app.MapGroup(ApiRoutes.AdminRoot).ExcludeFromDescription();

        pages.MapGet("", DashboardAsync);
        pages.MapGet("/login", LoginPageAsync);
        pages.MapPost("/login", LoginAsync);
        pages.MapPost("/logout", LogoutAsync);

        // Served from the AdminAssets folder instead of wwwroot on purpose: the static
        // files middleware runs before ours, and it must not hand out the admin UI
        // on the public port.
        pages.MapGet("/admin.css", (HttpContext context) => ServeAsset(context, "admin.css", "text/css; charset=utf-8"));
        pages.MapGet("/admin.js", (HttpContext context) => ServeAsset(context, "admin.js", "text/javascript; charset=utf-8"));

        var api = pages.MapGroup("/api").ExcludeFromDescription();
        api.AddEndpointFilter(new AdminAuthFilter());

        api.MapGet("/users", ListUsersAsync);
        api.MapPost("/users", CreateUserAsync);
        api.MapPost("/users/{username}/verify", VerifyUserAsync);
        api.MapPost("/users/{username}/enable", EnableUserAsync);
        api.MapPost("/users/{username}/disable", DisableUserAsync);
        api.MapPost("/users/{username}/resend-verification", ResendVerificationAsync);
        api.MapPost("/users/{username}/password", SetPasswordAsync);
        api.MapPost("/users/{username}/email", SetEmailAsync);
        api.MapDelete("/users/{username}", DeleteUserAsync);
    }

    // ---------------------------------------------------------------- pages

    private static Task<IResult> DashboardAsync(HttpContext context, IAdminAuthService auth, ILocalizationService loc)
    {
        if (!auth.IsEnabled) return Task.FromResult(NotConfigured(loc));

        var adminUser = auth.GetSessionUsername(context);
        return Task.FromResult(adminUser is null
            ? ServePage(context, "login.html", loc)
            : ServePage(context, "index.html", loc, adminUser));
    }

    private static Task<IResult> LoginPageAsync(HttpContext context, IAdminAuthService auth, ILocalizationService loc)
    {
        if (!auth.IsEnabled) return Task.FromResult(NotConfigured(loc));
        if (auth.GetSessionUsername(context) is not null) return Task.FromResult(Results.Redirect(ApiRoutes.AdminRoot));
        return Task.FromResult(ServePage(context, "login.html", loc));
    }

    private static async Task<IResult> LoginAsync(AdminLoginRequest req, HttpContext context,
        IAdminAuthService auth, IAuditLogger audit, ILocalizationService loc)
    {
        if (!auth.IsEnabled) return NotConfigured(loc);
        if (!ModelValidator.TryValidate(req, out var errors)) return ModelValidator.Invalid(errors);

        var ip = RemoteIp(context);
        var status = await auth.AuthenticateAsync(context, req.Username, req.Password, context.RequestAborted);

        switch (status)
        {
            case AdminLoginStatus.Ok:
                auth.SignIn(context, req.Username);
                await audit.WriteAsync(req.Username, "admin.login", req.Username, ip, true);
                return Results.Ok(ApiResponse.Ok(loc.Get("admin_title")));

            case AdminLoginStatus.Disabled:
                return NotConfigured(loc);

            case AdminLoginStatus.RateLimited:
                await audit.WriteAsync(req.Username, "admin.login", req.Username, ip, false, "rate limited");
                return Results.Json(
                    ApiResponse.Fail(loc.Get("admin_login_rate_limited")),
                    statusCode: StatusCodes.Status429TooManyRequests);

            default:
                await audit.WriteAsync(req.Username, "admin.login", req.Username, ip, false, "invalid credentials");
                return Results.Json(ApiResponse.Fail(loc.Get("admin_login_failed")), statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAdminAuthService auth, IAuditLogger audit)
    {
        var actor = auth.GetSessionUsername(context) ?? "unknown";
        auth.SignOut(context);
        await audit.WriteAsync(actor, "admin.logout", actor, RemoteIp(context), true);
        return Results.Ok(ApiResponse.Ok("bye"));
    }

    // ---------------------------------------------------------------- api

    private static Task<IResult> ListUsersAsync(HttpContext context, IDynsecService dynsec, ILocalizationService loc)
    {
        return ExecuteAsync(context, null, "user.list", "*", async () =>
        {
            var users = await dynsec.ListClientsDetailedAsync(context.RequestAborted);
            var views = users
                .Select(AdminUserView.From)
                .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(ApiResponse.Ok(loc.Get("users_retrieved"), views));
        });
    }

    private static Task<IResult> CreateUserAsync(AdminCreateUserRequest req, HttpContext context,
        IUserManagementService users, IAuditLogger audit, ILocalizationService loc)
    {
        return ExecuteAsync(context, audit, "user.create", req.Username, async () =>
        {
            if (!ModelValidator.TryValidate(req, out var errors)) return ModelValidator.Invalid(errors);

            var status = await users.CreateUserAsync(
                req.Username, req.Password, req.Email, req.SkipVerification, context.RequestAborted);

            return status switch
            {
                UserCreateStatus.UsernameTaken => Results.Conflict(ApiResponse.Fail(loc.Get("username_taken", req.Username))),
                UserCreateStatus.EmailTaken => Results.Conflict(ApiResponse.Fail(loc.Get("email_in_use", req.Email))),
                _ => Results.Ok(ApiResponse.Ok(loc.Get("user_created"), new { username = req.Username }))
            };
        });
    }

    private static Task<IResult> VerifyUserAsync(string username, HttpContext context, IDynsecService dynsec, IAuditLogger audit, ILocalizationService loc)
    {
        return ExecuteAsync(context, audit, "user.verify", username, async () =>
        {
            await dynsec.ForceVerifyAsync(username, context.RequestAborted);
            return Results.Ok(ApiResponse.Ok(loc.Get("admin_user_verified", username)));
        });
    }

    private static Task<IResult> EnableUserAsync(string username, HttpContext context, IDynsecService dynsec, IAuditLogger audit, ILocalizationService loc)
    {
        return ExecuteAsync(context, audit, "user.enable", username, async () =>
        {
            // A role nélküli kliens (például egy törölt token) engedélyezve nem tudna
            // publikálni és nem tudna belépni, ezért a szerep itt is létrejön.
            await dynsec.SetEnabledAsync(username, context.RequestAborted);
            return Results.Ok(ApiResponse.Ok(loc.Get("admin_user_enabled", username)));
        });
    }

    private static Task<IResult> DisableUserAsync(string username, HttpContext context, IDynsecService dynsec, IAuditLogger audit, ILocalizationService loc)
    {
        return ExecuteAsync(context, audit, "user.disable", username, async () =>
        {
            // A disabled dynsec client cannot connect at all, so locking yourself out
            // would mean nobody could undo it from the admin UI.
            if (IsSelf(context, username))
                return Results.BadRequest(ApiResponse.Fail(loc.Get("admin_cannot_disable_self")));

            await dynsec.SetDisabledAsync(username, true, context.RequestAborted);
            return Results.Ok(ApiResponse.Ok(loc.Get("admin_user_disabled", username)));
        });
    }

    private static Task<IResult> ResendVerificationAsync(string username, HttpContext context,
        IDynsecService dynsec, IUserManagementService users, IAuditLogger audit, ILocalizationService loc)
    {
        return ExecuteAsync(context, audit, "user.resend_verification", username, async () =>
        {
            var user = await dynsec.GetUserAsync(username, context.RequestAborted);

            if (user?.TextName is null || string.IsNullOrWhiteSpace(user.TextName))
                return Results.BadRequest(ApiResponse.Fail(loc.Get("user_not_found", username)));

            var sent = await users.ResendVerificationAsync(username, user.TextName, context.RequestAborted);
            return sent
                ? Results.Ok(ApiResponse.Ok(loc.Get("admin_verification_resent")))
                : Results.BadRequest(ApiResponse.Fail(loc.Get("admin_verification_not_needed")));
        });
    }

    private static Task<IResult> SetPasswordAsync(string username, AdminSetPasswordRequest req, HttpContext context,
        IDynsecService dynsec, IAuditLogger audit, ILocalizationService loc)
    {
        return ExecuteAsync(context, audit, "user.password", username, async () =>
        {
            if (!ModelValidator.TryValidate(req, out var errors)) return ModelValidator.Invalid(errors);

            await dynsec.ChangePasswordAsync(username, req.NewPassword, context.RequestAborted);
            return Results.Ok(ApiResponse.Ok(loc.Get("admin_password_saved")));
        });
    }

    private static Task<IResult> SetEmailAsync(string username, AdminSetEmailRequest req, HttpContext context,
        IDynsecService dynsec, IUserManagementService users, IAuditLogger audit, ILocalizationService loc)
    {
        return ExecuteAsync(context, audit, "user.email", username, async () =>
        {
            if (!ModelValidator.TryValidate(req, out var errors)) return ModelValidator.Invalid(errors);

            var conflict = await users.FindEmailOwnerAsync(req.NewEmail, excludeUsername: username, context.RequestAborted);
            if (conflict != null) return Results.Conflict(ApiResponse.Fail(loc.Get("email_in_use", req.NewEmail)));

            // Plain text name only: changing the address from here must not disable
            // the account the way the self service email change flow does.
            await dynsec.SetEmailAsync(username, req.NewEmail, context.RequestAborted);
            return Results.Ok(ApiResponse.Ok(loc.Get("admin_email_saved")));
        });
    }

    private static Task<IResult> DeleteUserAsync(string username, HttpContext context, IDynsecService dynsec, IAuditLogger audit, ILocalizationService loc)
    {
        return ExecuteAsync(context, audit, "user.delete", username, async () =>
        {
            if (IsSelf(context, username))
                return Results.BadRequest(ApiResponse.Fail(loc.Get("admin_cannot_delete_self")));

            await dynsec.DeleteUserAsync(username, context.RequestAborted);
            return Results.Ok(ApiResponse.Ok(loc.Get("user_deleted")));
        });
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<IResult> ExecuteAsync(
        HttpContext context, IAuditLogger? audit, string action, string target, Func<Task<IResult>> operation)
    {
        var actor = Actor(context);
        var ip = RemoteIp(context);

        try
        {
            var result = await operation();
            if (audit is not null)
                await audit.WriteAsync(actor, action, target, ip, !IsFailure(result));
            return result;
        }
        catch (Exception ex)
        {
            if (audit is not null)
                await audit.WriteAsync(actor, action, target, ip, false, ex.Message);
            return Error(ex);
        }
    }

    private static IResult Error(Exception ex) => ex switch
    {
        KeyNotFoundException notFound => Results.NotFound(ApiResponse.Fail(notFound.Message)),
        InvalidOperationException invalid => Results.Conflict(ApiResponse.Fail(invalid.Message)),
        OperationCanceledException => Results.StatusCode(503),
        _ => Results.Problem(ex.Message)
    };

    private static bool IsFailure(IResult result) =>
        result is IStatusCodeHttpResult status && status.StatusCode >= 400;

    private static bool IsSelf(HttpContext context, string username) =>
        string.Equals(Actor(context), username, StringComparison.Ordinal);

    private static string Actor(HttpContext context) =>
        context.Items.TryGetValue(ActorKey, out var actor) && actor is string value ? value : "unknown";

    private static string RemoteIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static IResult NotConfigured(ILocalizationService loc) =>
        Results.Json(
            ApiResponse.Fail(loc.Get("admin_not_configured")),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult ServePage(HttpContext context, string fileName, ILocalizationService loc, string? adminUser = null)
    {
        var i18n = UiStrings.ToDictionary(key => key, key => loc.Get(key));
        if (adminUser is not null) i18n[AdminUserKey] = adminUser;

        // The JSON lands inside a script element, so escape the characters that could
        // close it. Everything else is safe there thanks to JSON string escaping.
        var json = JsonSerializer.Serialize(i18n)
            .Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace(">", "\\u003e", StringComparison.Ordinal)
            .Replace("&", "\\u0026", StringComparison.Ordinal);

        return ServeAsset(context, fileName, "text/html; charset=utf-8",
            html => html.Replace("__ADMIN_I18N__", json, StringComparison.Ordinal));
    }

    private static IResult ServeAsset(HttpContext context, string fileName, string contentType, Func<string, string>? transform = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AdminAssets", fileName);
        if (!File.Exists(path)) return Results.NotFound();

        var content = File.ReadAllText(path, Encoding.UTF8);
        if (transform is not null) content = transform(content);

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Content(content, contentType);
    }

    // Blocks every admin API call without a valid session cookie.
    private sealed class AdminAuthFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var auth = context.HttpContext.RequestServices.GetRequiredService<IAdminAuthService>();

            if (!auth.IsEnabled)
            {
                var loc = context.HttpContext.RequestServices.GetRequiredService<ILocalizationService>();
                return Results.Json(
                    ApiResponse.Fail(loc.Get("admin_not_configured")),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var actor = auth.GetSessionUsername(context.HttpContext);
            if (actor is null)
            {
                var loc = context.HttpContext.RequestServices.GetRequiredService<ILocalizationService>();
                return Results.Json(
                    ApiResponse.Fail(loc.Get("admin_login_failed")),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            context.HttpContext.Items[ActorKey] = actor;
            return await next(context);
        }
    }
}
