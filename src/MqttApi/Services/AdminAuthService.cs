using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MqttApi.Configuration;
using MqttApi.Constants;

namespace MqttApi.Services;

public class AdminAuthService : IAdminAuthService
{
    public const string CookieName = "diatar_admin";

    private readonly HashSet<string> _admins;
    private readonly IMqttPasswordVerifier _verifier;
    private readonly ILogger<AdminAuthService> _logger;
    private readonly byte[] _secret;
    private readonly TimeSpan _sessionLength;
    private readonly TimeSpan _attemptWindow;
    private readonly int _attemptLimit;
    private readonly ConcurrentDictionary<string, AttemptCounter> _attempts = new(StringComparer.Ordinal);
    private long _lastPruneTicks;

    public AdminAuthService(
        IOptions<AdminOptions> options,
        IMqttPasswordVerifier verifier,
        ILogger<AdminAuthService> logger)
    {
        var config = options.Value;
        _verifier = verifier;
        _logger = logger;

        _admins = ParseAdmins(config.Usernames);
        _sessionLength = TimeSpan.FromMinutes(Math.Max(1, config.SessionMinutes));
        _attemptWindow = TimeSpan.FromMinutes(Math.Max(1, config.LoginAttemptWindowMinutes));
        _attemptLimit = Math.Max(1, config.LoginAttemptLimit);

        if (string.IsNullOrWhiteSpace(config.CookieSecret))
        {
            _secret = RandomNumberGenerator.GetBytes(32);
            _logger.LogWarning(
                "Admin:CookieSecret nincs beállítva - véletlen titok lett generálva, minden újraindításkor kilépteti az adminokat");
        }
        else
        {
            _secret = Encoding.UTF8.GetBytes(config.CookieSecret);
            if (_secret.Length < 32)
                _logger.LogWarning("Admin:CookieSecret rövidebb mint 32 bájt");
        }
    }

    public bool IsEnabled => _admins.Count > 0;

    public bool IsAdmin(string username) => _admins.Contains(username);

    public async Task<AdminLoginStatus> AuthenticateAsync(HttpContext context, string username, string password, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            _logger.LogError("Admin:Usernames nincs beállítva - az admin felület le van tiltva");
            return AdminLoginStatus.Disabled;
        }

        var ip = RemoteIp(context);

        if (IsRateLimited(ip))
            return AdminLoginStatus.RateLimited;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            RegisterFailure(ip);
            return AdminLoginStatus.InvalidCredentials;
        }

        // Dynsec client usernames are case sensitive, so the allow list is compared ordinally.
        if (!_admins.Contains(username))
        {
            RegisterFailure(ip);
            _logger.LogWarning("Sikertelen admin belépés: {Username} ({Ip}) - nincs az engedélyezett listában", username, ip);
            return AdminLoginStatus.InvalidCredentials;
        }

        if (!await _verifier.VerifyAsync(username, password, ct))
        {
            RegisterFailure(ip);
            _logger.LogWarning("Sikertelen admin belépés: {Username} ({Ip}) - hibás jelszó", username, ip);
            return AdminLoginStatus.InvalidCredentials;
        }

        ResetFailures(ip);
        return AdminLoginStatus.Ok;
    }

    public string? GetSessionUsername(HttpContext context)
    {
        var value = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(value)) return null;

        var parts = value.Split('|');
        if (parts.Length != 3) return null;

        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry))
            return null;

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiry) return null;

        // The username is base64url encoded in the cookie so that any character in it
        // (a '|' would break the format, a '+' or a space would break the header).
        string username;
        try { username = Base64UrlDecode(parts[0]); }
        catch (FormatException) { return null; }

        if (!VerifySignature(username, expiry, parts[2])) return null;
        if (!_admins.Contains(username)) return null;

        SignIn(context, username);
        return username;
    }

    public void SignIn(HttpContext context, string username)
    {
        context.Response.Cookies.Append(CookieName, CreateCookieValue(username), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = ApiRoutes.AdminRoot,
            Expires = DateTimeOffset.UtcNow.Add(_sessionLength)
        });
    }

    public void SignOut(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = ApiRoutes.AdminRoot
        });
    }

    private HashSet<string> ParseAdmins(string? usernames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in (usernames ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            names.Add(name);
        }

        return names;
    }

    private string CreateCookieValue(string username)
    {
        var expiry = DateTimeOffset.UtcNow.Add(_sessionLength).ToUnixTimeSeconds();
        var encoded = Base64UrlEncode(Encoding.UTF8.GetBytes(username));
        return string.Join('|', encoded, expiry.ToString(CultureInfo.InvariantCulture), Sign(username, expiry));
    }

    private string Sign(string username, long expiry)
    {
        var payload = string.Create(CultureInfo.InvariantCulture, $"{username}|{expiry}");
        return Base64UrlEncode(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload)));
    }

    private bool VerifySignature(string username, long expiry, string signature)
    {
        var expected = Encoding.UTF8.GetBytes(Sign(username, expiry));
        var actual = Encoding.UTF8.GetBytes(signature);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException($"Invalid base64url length: {value.Length}")
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private bool IsRateLimited(string ip)
    {
        if (!_attempts.TryGetValue(ip, out var counter)) return false;
        if (DateTimeOffset.UtcNow - counter.WindowStart <= _attemptWindow) return counter.Count >= _attemptLimit;

        _attempts.TryRemove(ip, out _);
        return false;
    }

    private void RegisterFailure(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        _attempts.AddOrUpdate(ip,
            _ => new AttemptCounter(1, now),
            (_, existing) => now - existing.WindowStart > _attemptWindow
                ? new AttemptCounter(1, now)
                : new AttemptCounter(existing.Count + 1, existing.WindowStart));

        PruneExpired(now);
    }

    private void ResetFailures(string ip) => _attempts.TryRemove(ip, out _);

    private void PruneExpired(DateTimeOffset now)
    {
        var previous = Interlocked.Exchange(ref _lastPruneTicks, now.Ticks);
        if (now - new DateTimeOffset(previous, TimeSpan.Zero) <= _attemptWindow) return;

        foreach (var entry in _attempts)
            if (now - entry.Value.WindowStart > _attemptWindow)
                _attempts.TryRemove(entry.Key, out _);
    }

    private static string RemoteIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private readonly record struct AttemptCounter(int Count, DateTimeOffset WindowStart);
}
