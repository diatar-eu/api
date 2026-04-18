using System.ComponentModel.DataAnnotations;
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
        group.MapPost("/delete", DeleteUserAsync).WithSummary("Delete a user (requires password)");
        group.MapPost("/change-password", ChangePasswordAsync).WithSummary("Change password (requires current password)");
        group.MapPost("/change-email", ChangeEmailAsync).WithSummary("Change email (requires password, sends verification)");
        group.MapPost("/change-username", ChangeUsernameAsync).WithSummary("Change username (requires current password)");
    }

    private static async Task<IResult> CreateUserAsync(CreateUserRequest req, IDynsecService dynsec, IEmailService email)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        try
        {
            if (await UsernameExistsAsync(dynsec, req.Username))
                return Results.Conflict(ApiResponse.Fail($"Username '{req.Username}' is already taken."));

            var emailConflict = await FindEmailOwnerAsync(dynsec, req.Email, excludeUsername: null);
            if (emailConflict != null)
                return Results.Conflict(ApiResponse.Fail($"Email '{req.Email}' is already in use."));

            var token = Guid.NewGuid().ToString("N");
            await dynsec.CreateUserAsync(req.Username, req.Password, req.Email, [], disabled: true, textDescription: token);
            await email.SendVerificationEmailAsync(req.Email, req.Username, token);
            return Results.Ok(ApiResponse.Ok("User created. A verification email has been sent."));
        }
        catch (InvalidOperationException ex) { return Results.Conflict(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> VerifyUserAsync(string username, string token, IDynsecService dynsec)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
            return Results.Content(HtmlPage("Verification Failed", "Missing username or token."), "text/html", statusCode: 400);
        try
        {
            await dynsec.VerifyUserAsync(username, token);
            return Results.Content(HtmlPage("Account Verified", "Your account has been verified! You can now log in."), "text/html");
        }
        catch (KeyNotFoundException)
        {
            return Results.Content(HtmlPage("Verification Failed", "Invalid or expired verification link."), "text/html", statusCode: 400);
        }
        catch (InvalidOperationException)
        {
            return Results.Content(HtmlPage("Verification Failed", "Invalid or expired verification link."), "text/html", statusCode: 400);
        }
        catch (OperationCanceledException)
        {
            return Results.Content(HtmlPage("Error", "Service unavailable. Please try again later."), "text/html", statusCode: 503);
        }
        catch (Exception ex)
        {
            return Results.Content(HtmlPage("Error", ex.Message), "text/html", statusCode: 500);
        }
    }

    private static async Task<IResult> ResendVerificationAsync(ResendVerificationRequest req, IDynsecService dynsec, IEmailService email)
    {
        const string generic = "If the account exists and is unverified, a new verification email has been sent.";
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
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

    private static async Task<IResult> DeleteUserAsync(DeleteUserRequest req, IDynsecService dynsec, IMqttPasswordVerifier verifier)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        if (!await verifier.VerifyAsync(req.Username, req.Password)) return Results.Unauthorized();
        try
        {
            await dynsec.DeleteUserAsync(req.Username);
            return Results.Ok(ApiResponse.Ok("User deleted successfully."));
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest req, IDynsecService dynsec, IMqttPasswordVerifier verifier)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        if (!await verifier.VerifyAsync(req.Username, req.Password)) return Results.Unauthorized();
        try
        {
            await dynsec.ChangePasswordAsync(req.Username, req.NewPassword);
            return Results.Ok(ApiResponse.Ok("Password updated successfully."));
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> ChangeEmailAsync(ChangeEmailRequest req, IDynsecService dynsec, IEmailService email, IMqttPasswordVerifier verifier)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        if (!await verifier.VerifyAsync(req.Username, req.Password)) return Results.Unauthorized();
        try
        {
            var existing = await dynsec.GetUserAsync(req.Username);
            if (existing == null)
                return Results.NotFound(ApiResponse.Fail($"User '{req.Username}' not found."));

            var emailConflict = await FindEmailOwnerAsync(dynsec, req.NewEmail, excludeUsername: req.Username);
            if (emailConflict != null)
                return Results.Conflict(ApiResponse.Fail($"Email '{req.NewEmail}' is already in use."));

            var token = Guid.NewGuid().ToString("N");
            await dynsec.ChangeEmailAsync(req.Username, req.NewEmail, token);
            await email.SendVerificationEmailAsync(req.NewEmail, req.Username, token);
            return Results.Ok(ApiResponse.Ok("Email updated. A verification email has been sent to the new address."));
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(ApiResponse.Fail(ex.Message)); }
        catch (OperationCanceledException) { return Results.StatusCode(503); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static async Task<IResult> ChangeUsernameAsync(ChangeUsernameRequest req, IDynsecService dynsec, IMqttPasswordVerifier verifier)
    {
        if (!Validate(req, out var errors)) return Results.ValidationProblem(errors);
        if (!await verifier.VerifyAsync(req.Username, req.Password)) return Results.Unauthorized();
        try
        {
            var existing = await dynsec.GetUserAsync(req.Username);
            if (existing == null)
                return Results.NotFound(ApiResponse.Fail($"User '{req.Username}' not found."));

            if (await UsernameExistsAsync(dynsec, req.NewUsername))
                return Results.Conflict(ApiResponse.Fail($"Username '{req.NewUsername}' is already taken."));

            await dynsec.ChangeUsernameAsync(req.Username, req.NewUsername, req.NewPassword);
            return Results.Ok(ApiResponse.Ok("Username updated successfully."));
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(ApiResponse.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return Results.Conflict(ApiResponse.Fail(ex.Message)); }
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
}
