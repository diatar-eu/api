using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using MqttApi.Constants;
using MqttApi.Models.Requests;
using MqttApi.Models.Responses;
using MqttApi.Services;

namespace MqttApi.Endpoints;

public static class UserEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.UsersGroup).WithTags("Users");

        group.MapPost("/create", CreateUserAsync).WithSummary("Create a new user (sends verification email)");
        group.MapGet("/verify", VerifyUserAsync).WithSummary("Verify email address via token link");
        group.MapPost("/resend-verification", ResendVerificationAsync).WithSummary("Resend verification email for unverified accounts");
        group.MapPost("/request-password-reset", RequestPasswordResetAsync).WithSummary("Request password reset email");
        group.MapGet("/reset-password", ResetPasswordPageAsync).WithSummary("Reset password page from email link");
        group.MapPost("/reset-password", ResetPasswordAsync).WithSummary("Reset password with token");
        group.MapPost("/delete", DeleteUserAsync).WithSummary("Delete a user (requires password)");
        group.MapPost("/change-password", ChangePasswordAsync).WithSummary("Change password (requires current password)");
        group.MapPost("/change-email", ChangeEmailAsync).WithSummary("Change email (requires password, sends verification)");
        group.MapPost("/change-username", ChangeUsernameAsync).WithSummary("Change username (requires current password)");
        group.MapGet("/list", ListUsersAsync).WithSummary("List all registered MQTT usernames");
    }

    private static async Task<IResult> CreateUserAsync(CreateUserRequest req, IDynsecService dynsec, IEmailService email, ILocalizationService loc)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        try
        {
            if (await UsernameExistsAsync(dynsec, req.Username))
                return Results.Conflict(ApiResponse.Fail(loc.Get("username_taken", req.Username)));

            var emailConflict = await FindEmailOwnerAsync(dynsec, req.Email, excludeUsername: null);
            if (emailConflict != null)
                return Results.Conflict(ApiResponse.Fail(loc.Get("email_in_use", req.Email)));

            var token = Guid.NewGuid().ToString("N");
            await dynsec.CreateUserAsync(req.Username, req.Password, req.Email, [], disabled: true, textDescription: token);
            await email.SendVerificationEmailAsync(req.Email, req.Username, token);
            return Results.Ok(ApiResponse.Ok(loc.Get("user_created")));
        }
        catch (InvalidOperationException ex) { return Results.Conflict(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> VerifyUserAsync(string username, string token, IDynsecService dynsec, ILocalizationService loc)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
            return Results.Content(HtmlPage(loc.Get("verification_failed_title"), loc.Get("missing_username_or_token")), "text/html", statusCode: 400);
        try
        {
            await dynsec.VerifyUserAsync(username, token);
            return Results.Content(HtmlPage(loc.Get("account_verified_title"), loc.Get("account_verified_message")), "text/html");
        }
        catch (KeyNotFoundException)
        {
            return Results.Content(HtmlPage(loc.Get("verification_failed_title"), loc.Get("verification_link_invalid")), "text/html", statusCode: 400);
        }
        catch (InvalidOperationException)
        {
            return Results.Content(HtmlPage(loc.Get("verification_failed_title"), loc.Get("verification_link_invalid")), "text/html", statusCode: 400);
        }
        catch (OperationCanceledException)
        {
            return Results.Content(HtmlPage(loc.Get("error_title"), loc.Get("service_unavailable")), "text/html", statusCode: 503);
        }
        catch (Exception ex)
        {
            return Results.Content(HtmlPage(loc.Get("error_title"), ex.Message), "text/html", statusCode: 500);
        }
    }

    private static async Task<IResult> ResendVerificationAsync(ResendVerificationRequest req, IDynsecService dynsec, IEmailService email, ILocalizationService loc)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        var generic = loc.Get("resend_verification_sent");
        try
        {
            var user = await dynsec.GetUserAsync(req.Username);
            if (user == null || user.Disabled == false)
                return Results.Ok(ApiResponse.Ok(generic));

            if (!string.Equals(user.TextName, req.Email, StringComparison.OrdinalIgnoreCase))
                return Results.Ok(ApiResponse.Ok(generic));

            var token = Guid.NewGuid().ToString("N");
            await dynsec.SetVerificationTokenAsync(req.Username, token);
            await email.SendVerificationEmailAsync(req.Email, req.Username, token);
            return Results.Ok(ApiResponse.Ok(generic));
        }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception) { return Results.Ok(ApiResponse.Ok(generic)); }
    }

    private static async Task<IResult> RequestPasswordResetAsync(RequestPasswordResetRequest req, IDynsecService dynsec, IEmailService email, ILocalizationService loc)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        var generic = loc.Get("password_reset_requested");
        try
        {
            var user = await dynsec.GetUserAsync(req.Username);
            if (user == null)
                return Results.Ok(ApiResponse.Ok(generic));

            if (!string.Equals(user.TextName, req.Email, StringComparison.OrdinalIgnoreCase))
                return Results.Ok(ApiResponse.Ok(generic));

            var token = Guid.NewGuid().ToString("N");
            await dynsec.SetVerificationTokenAsync(req.Username, token);
            await email.SendPasswordResetEmailAsync(req.Email, req.Username, token);
            return Results.Ok(ApiResponse.Ok(generic));
        }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception) { return Results.Ok(ApiResponse.Ok(generic)); }
    }

    private static IResult ResetPasswordPageAsync(string username, string token, ILocalizationService loc)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
            return Results.Content(HtmlPage(loc.Get("password_reset_failed_title"), loc.Get("missing_username_or_token")), "text/html", statusCode: 400);

        return Results.Content(ResetPasswordFormPage(loc, username, token), "text/html");
    }

    private static async Task<IResult> ResetPasswordAsync(ResetPasswordRequest req, IDynsecService dynsec, ILocalizationService loc)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        try
        {
            var user = await dynsec.GetUserAsync(req.Username);
            if (user == null || !TokenMatches(user.TextDescription, req.Token))
                return Results.BadRequest(ApiResponse.Fail(loc.Get("password_reset_invalid_token")));

            await dynsec.ChangePasswordAsync(req.Username, req.NewPassword);
            await dynsec.SetVerificationTokenAsync(req.Username, string.Empty);
            return Results.Ok(ApiResponse.Ok(loc.Get("password_reset_completed")));
        }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static string HtmlPage(string title, string message) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8"><title>{{title}}</title>
        <style>body{font-family:sans-serif;display:flex;justify-content:center;align-items:center;min-height:100vh;margin:0;background:#f5f5f5}
        .card{background:#fff;padding:2rem 3rem;border-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,.1);text-align:center;max-width:480px}</style>
        </head>
        <body><div class="card"><h2>{{title}}</h2><p>{{message}}</p></div></body>
        </html>
        """;

        private static string ResetPasswordFormPage(ILocalizationService loc, string username, string token) => $$"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="utf-8">
                    <meta name="viewport" content="width=device-width,initial-scale=1">
                    <title>{{loc.Get("password_reset_title")}}</title>
                    <style>
                        body{font-family:sans-serif;display:flex;justify-content:center;align-items:center;min-height:100vh;margin:0;background:#f5f5f5}
                        .card{background:#fff;padding:2rem;border-radius:10px;box-shadow:0 2px 10px rgba(0,0,0,.1);max-width:420px;width:100%}
                        label{display:block;margin:0 0 .4rem;font-weight:600}
                        input{width:100%;padding:.7rem .75rem;border:1px solid #ccc;border-radius:8px;box-sizing:border-box}
                        button{margin-top:1rem;width:100%;padding:.8rem 1rem;border:0;border-radius:8px;background:#4f46e5;color:#fff;font-weight:600;cursor:pointer}
                        p{margin:.8rem 0 0}
                    </style>
                </head>
                <body>
                    <div class="card">
                        <h2>{{loc.Get("password_reset_title")}}</h2>
                        <p>{{loc.Get("password_reset_intro")}}</p>
                        <form id="resetForm">
                            <label for="newPassword">{{loc.Get("password_reset_new_password")}}</label>
                            <input id="newPassword" name="newPassword" type="password" minlength="8" required>
                            <button type="submit">{{loc.Get("password_reset_submit")}}</button>
                        </form>
                        <p id="result"></p>
                    </div>
                    <script>
                        const form = document.getElementById('resetForm');
                        const result = document.getElementById('result');
                        form.addEventListener('submit', async (e) => {
                            e.preventDefault();
                            result.textContent = '';
                            const newPassword = document.getElementById('newPassword').value;
                            try {
                                const resp = await fetch('/api/v1/users/reset-password', {
                                    method: 'POST',
                                    headers: { 'Content-Type': 'application/json' },
                                    body: JSON.stringify({ username: '{{username}}', token: '{{token}}', newPassword })
                                });
                                const data = await resp.json();
                                if (resp.ok) {
                                    result.textContent = data.message || '{{loc.Get("password_reset_completed")}}';
                                } else {
                                    result.textContent = data.message || '{{loc.Get("password_reset_invalid_token")}}';
                                }
                            } catch {
                                result.textContent = '{{loc.Get("service_unavailable")}}';
                            }
                        });
                    </script>
                </body>
                </html>
                """;

    private static async Task<IResult> DeleteUserAsync(DeleteUserRequest req, IDynsecService dynsec, IMqttPasswordVerifier verifier, ILocalizationService loc)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        if (!await verifier.VerifyAsync(req.Username, req.Password)) return Results.Unauthorized();
        try
        {
            await dynsec.DeleteUserAsync(req.Username);
            return Results.Ok(ApiResponse.Ok(loc.Get("user_deleted")));
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest req, IDynsecService dynsec, IMqttPasswordVerifier verifier, ILocalizationService loc)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        if (!await verifier.VerifyAsync(req.Username, req.Password)) return Results.Unauthorized();
        try
        {
            await dynsec.ChangePasswordAsync(req.Username, req.NewPassword);
            return Results.Ok(ApiResponse.Ok(loc.Get("password_updated")));
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> ChangeEmailAsync(ChangeEmailRequest req, IDynsecService dynsec, IEmailService email, IMqttPasswordVerifier verifier, ILocalizationService loc)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        if (!await verifier.VerifyAsync(req.Username, req.Password)) return Results.Unauthorized();
        try
        {
            var existing = await dynsec.GetUserAsync(req.Username);
            if (existing == null)
                return Results.NotFound(ApiResponse.Fail(loc.Get("user_not_found", req.Username)));

            var emailConflict = await FindEmailOwnerAsync(dynsec, req.NewEmail, excludeUsername: req.Username);
            if (emailConflict != null)
                return Results.Conflict(ApiResponse.Fail(loc.Get("email_in_use", req.NewEmail)));

            var token = Guid.NewGuid().ToString("N");
            await dynsec.ChangeEmailAsync(req.Username, req.NewEmail, token);
            await email.SendVerificationEmailAsync(req.NewEmail, req.Username, token);
            return Results.Ok(ApiResponse.Ok(loc.Get("email_updated")));
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> ChangeUsernameAsync(ChangeUsernameRequest req, IDynsecService dynsec, IMqttPasswordVerifier verifier, ILocalizationService loc)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        if (!await verifier.VerifyAsync(req.Username, req.Password)) return Results.Unauthorized();
        try
        {
            var existing = await dynsec.GetUserAsync(req.Username);
            if (existing == null)
                return Results.NotFound(ApiResponse.Fail(loc.Get("user_not_found", req.Username)));

            if (await UsernameExistsAsync(dynsec, req.NewUsername))
                return Results.Conflict(ApiResponse.Fail(loc.Get("username_taken", req.NewUsername)));

            await dynsec.ChangeUsernameAsync(req.Username, req.NewUsername, req.NewPassword);
            return Results.Ok(ApiResponse.Ok(loc.Get("username_updated")));
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(ApiResponse.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return Results.Conflict(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> ListUsersAsync(IDynsecService dynsec, ILocalizationService loc)
    {
        try
        {
            var names = await dynsec.ListClientNamesAsync();
            var filteredNames = new List<string>();

            foreach (var name in names)
            {
                var user = await dynsec.GetUserAsync(name);
                var hasUserRole = user?.Roles?.Any(role =>
                    role.Rolename.StartsWith(DynsecConstants.Acl.RolePrefix, StringComparison.Ordinal)) == true;

                if (hasUserRole)
                    filteredNames.Add(name);
            }

            return Results.Ok(ApiResponse.Ok(loc.Get("users_retrieved"), data: filteredNames));
        }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<bool> UsernameExistsAsync(IDynsecService dynsec, string username) =>
        await dynsec.GetUserAsync(username) != null;

    // Returns the username that owns the email, or null if the email is free.
    // Pass excludeUsername to skip the current user (used when changing their own email).
    private static async Task<string?> FindEmailOwnerAsync(IDynsecService dynsec, string email, string? excludeUsername)
    {
        var names = await dynsec.ListClientNamesAsync();
        foreach (var name in names)
        {
            if (excludeUsername != null && string.Equals(name, excludeUsername, StringComparison.OrdinalIgnoreCase))
                continue;
            var user = await dynsec.GetUserAsync(name);
            if (user?.TextName != null &&
                string.Equals(user.TextName, email, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    private static bool Validate<T>(T model, out Dictionary<string, string[]> errors)
    {
        var context = new ValidationContext(model!);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(model!, context, results, validateAllProperties: true))
        {
            errors = [];
            return true;
        }
        errors = results
            .GroupBy(r => r.MemberNames.FirstOrDefault() ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ErrorMessage ?? "Invalid").ToArray());
        return false;
    }

    private static bool TokenMatches(string? storedToken, string inputToken)
    {
        var stored = storedToken?.Trim() ?? string.Empty;
        var input = inputToken.Trim();
        if (stored.Length == 0 || input.Length == 0)
            return false;

        var storedBytes = Encoding.UTF8.GetBytes(stored);
        var inputBytes = Encoding.UTF8.GetBytes(input);
        return CryptographicOperations.FixedTimeEquals(storedBytes, inputBytes);
    }
}
