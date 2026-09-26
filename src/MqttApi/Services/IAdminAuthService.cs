namespace MqttApi.Services;

public enum AdminLoginStatus
{
    Ok,
    InvalidCredentials,
    RateLimited,
    Disabled
}

public interface IAdminAuthService
{
    // False when Admin:Usernames is empty, in which case the admin UI stays closed.
    bool IsEnabled { get; }

    bool IsAdmin(string username);

    Task<AdminLoginStatus> AuthenticateAsync(HttpContext context, string username, string password, CancellationToken ct = default);

    // Returns the admin username from a valid session cookie, or null. Renews the
    // cookie so that active sessions do not expire while in use.
    string? GetSessionUsername(HttpContext context);

    void SignIn(HttpContext context, string username);

    void SignOut(HttpContext context);
}
