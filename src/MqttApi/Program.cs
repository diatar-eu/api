using System.Net;
using MqttApi.Configuration;
using MqttApi.Constants;
using MqttApi.Endpoints;
using MqttApi.Middleware;
using MqttApi.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));

var adminOptions = builder.Configuration.GetSection("Admin").Get<AdminOptions>() ?? new AdminOptions();
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ILocalizationService, LocalizationService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IUserManagementService, UserManagementService>();
// Singleton, because the login rate limit and the cookie secret have to outlive a request.
builder.Services.AddSingleton<IMqttPasswordVerifier, MqttPasswordVerifier>();
builder.Services.AddSingleton<IAdminAuthService, AdminAuthService>();
builder.Services.AddSingleton<IAuditLogger, AdminAuditService>();
builder.Services.AddSingleton<IMqttClientService, MqttClientService>();
builder.Services.AddSingleton<IDynsecService, DynsecService>();
builder.Services.AddHostedService(sp => (MqttClientService)sp.GetRequiredService<IMqttClientService>());
builder.Services.AddHostedService(sp => (DynsecService)sp.GetRequiredService<IDynsecService>());
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// A single Listen call in code would make Kestrel drop the hosting addresses, so both
// listeners are set up here: the public one from urls / ASPNETCORE_URLS exactly as
// Kestrel would bind it, and the admin one from the Admin section.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    foreach (var url in HostingUrls(builder.Configuration))
        AddListener(kestrel, url);

    if (!adminOptions.Enabled) return;

    if (IPAddress.TryParse(adminOptions.ListenAddress, out var adminAddress))
        kestrel.Listen(adminAddress, adminOptions.Port);
    else
        Console.Error.WriteLine($"Admin:ListenAddress érvénytelen: {adminOptions.ListenAddress} - az admin felület nem indul el");
});

const string AllowWebDiatarEuPolicy = "AllowWebDiatarEu";
builder.Services.AddCors(options =>
{
    options.AddPolicy(AllowWebDiatarEuPolicy, policy =>
    {
        policy.WithOrigins(
                "https://web.diatar.eu",
                "http://web.diatar.eu")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseAdminPortGuard();
app.UseCors(AllowWebDiatarEuPolicy);

app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks(ApiRoutes.Health);
UserEndpoints.Map(app);
AdminEndpoints.Map(app);

if (!adminOptions.Enabled)
    app.Logger.LogWarning("Az admin felület le van tiltva (Admin:Enabled=false)");
else if (string.IsNullOrWhiteSpace(adminOptions.Usernames))
    app.Logger.LogWarning("Az admin felület nincs használható: az Admin:Usernames (illetve az Admin_Usernames környezeti változó) üres");

app.Run();

static IEnumerable<string> HostingUrls(IConfiguration configuration)
{
    // Same precedence Kestrel would apply, the environment variable first.
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
               ?? configuration[WebHostDefaults.ServerUrlsKey]
               ?? configuration["ASPNETCORE_URLS"]
               ?? "http://0.0.0.0:8080";

    return urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static void AddListener(KestrelServerOptions kestrel, string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        Console.Error.WriteLine($"Érvénytelen URL: {url}");
        return;
    }

    var host = uri.Host;
    var loopback = host is "localhost";
    var anyIp = host is "+" or "*" or "0.0.0.0" or "::";

    try
    {
        if (uri.Scheme == Uri.UriSchemeHttps)
            kestrel.ListenLocalhost(uri.Port, https => https.UseHttps());
        else if (anyIp)
            kestrel.ListenAnyIP(uri.Port);
        else if (loopback)
            kestrel.ListenLocalhost(uri.Port);
        else if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
            kestrel.Listen(address, uri.Port);
        else
            Console.Error.WriteLine($"Nem támogatott host a(z) '{url}' URL-ben, csak localhost vagy IP cím lehet");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Nem sikerült a(z) '{url}' URL figyelése: {ex.Message}");
    }
}
