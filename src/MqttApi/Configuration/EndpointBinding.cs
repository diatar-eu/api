using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace MqttApi.Configuration;

public readonly record struct EndpointUri(string Scheme, string Host, int Port)
{
    public bool IsHttps => Scheme == "https";
    public bool IsAnyAddress => Host is "+" or "*" or "0.0.0.0" or "::";
    public bool IsLocalhost => Host == "localhost";
}

// The public API listener. It lives next to the Admin section so that every listener of
// the app is described in appsettings.json (or its environment variable override) instead
// of being hidden in ASPNETCORE_URLS.
public static class EndpointBinding
{
    // Precedence, same as Kestrel's own: ASPNETCORE_URLS, then the Public:Url setting,
    // then the framework's HTTP_PORTS/HTTPS_PORTS, and finally the container default.
    public static IReadOnlyList<string> ResolvePublicUrls(IConfiguration configuration, string? configuredUrl)
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
                   ?? configuration[WebHostDefaults.ServerUrlsKey];

        if (!string.IsNullOrWhiteSpace(urls)) return Split(urls);
        if (!string.IsNullOrWhiteSpace(configuredUrl)) return Split(configuredUrl);

        var fromPorts = new List<string>();
        AddPorts(fromPorts, "http",
            Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? configuration["ASPNETCORE_HTTP_PORTS"]);
        AddPorts(fromPorts, "https",
            Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS") ?? configuration["ASPNETCORE_HTTPS_PORTS"]);

        return fromPorts.Count > 0 ? fromPorts : ["http://0.0.0.0:8080"];
    }

    // Kestrel does not use Uri parsing for these addresses: the wildcard hosts ('+', '*')
    // and the bracketed IPv6 literals are not valid URIs. This is the same manual parse.
    public static bool TryParse(string? url, out EndpointUri endpoint)
    {
        endpoint = default;
        if (string.IsNullOrWhiteSpace(url)) return false;
        url = url.Trim();

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return false;

        var scheme = url[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https")) return false;

        // A path is not part of a Kestrel endpoint.
        var authority = url[(schemeEnd + 3)..];
        var pathStart = authority.IndexOf('/');
        if (pathStart >= 0) authority = authority[..pathStart];
        if (authority.Length == 0) return false;

        string host;
        int? explicitPort = null;
        if (authority.StartsWith('['))
        {
            // IPv6 literal, for example [::1]:8080
            var bracketEnd = authority.IndexOf(']');
            if (bracketEnd < 0) return false;

            host = authority[1..bracketEnd];
            var afterBracket = authority[(bracketEnd + 1)..];
            if (afterBracket.Length > 0)
            {
                // A trailing colon with nothing after it is a typo, not a missing port.
                if (!afterBracket.StartsWith(':')) return false;
                if (!TryParsePort(afterBracket[1..], out var bracketPort)) return false;
                explicitPort = bracketPort;
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                host = authority[..colon];

                // An IPv6 address without brackets is not a valid authority.
                if (host.Contains(':')) return false;

                if (!TryParsePort(authority[(colon + 1)..], out var parsedPort)) return false;
                explicitPort = parsedPort;
            }
            else
            {
                host = authority;
            }
        }

        if (host.Length == 0) return false;

        // DNS names are case insensitive, so 'LocalHost' has to mean 'localhost'.
        host = host.ToLowerInvariant();

        endpoint = new EndpointUri(scheme, host, explicitPort ?? (scheme == "https" ? 443 : 80));
        return true;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    // True when the public listener would take the same address as the admin one, which
    // Kestrel would only report as an obscure bind failure at startup.
    public static bool ConflictsWithAdmin(EndpointUri endpoint, IPAddress adminAddress, int adminPort) =>
        endpoint.Port == adminPort
        && (endpoint.IsAnyAddress
            || IPAddress.TryParse(endpoint.Host, out var address) && address.Equals(adminAddress)
            || endpoint.IsLocalhost && IPAddress.Loopback.Equals(adminAddress));

    // Binds one address, the same way Kestrel would. Returns false and sets error for
    // anything that cannot be used, so that a misconfiguration is visible in the log.
    public static bool TryBind(KestrelServerOptions kestrel, string url, out string? error)
    {
        error = null;

        if (!TryParse(url, out var endpoint))
        {
            error = $"Érvénytelen URL: {url}";
            return false;
        }

        try
        {
            if (endpoint.IsAnyAddress)
                kestrel.ListenAnyIP(endpoint.Port, Listen(endpoint));
            else if (endpoint.IsLocalhost)
                kestrel.ListenLocalhost(endpoint.Port, Listen(endpoint));
            else if (IPAddress.TryParse(endpoint.Host, out var address))
                kestrel.Listen(address, endpoint.Port, Listen(endpoint));
            else
            {
                error = $"Nem támogatott host a(z) '{url}' URL-ben, csak localhost vagy IP cím lehet";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Nem sikerült a(z) '{url}' URL figyelése: {ex.Message}";
            return false;
        }
    }

    private static Action<ListenOptions> Listen(EndpointUri endpoint) =>
        endpoint.IsHttps ? options => options.UseHttps() : _ => { };

    private static IReadOnlyList<string> Split(string urls) =>
        urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddPorts(ICollection<string> target, string scheme, string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports)) return;
        foreach (var port in ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            target.Add($"{scheme}://+:{port}");
    }
}
