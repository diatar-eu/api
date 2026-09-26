using System.Net;
using MqttApi.Configuration;
using MqttApi.Constants;
using MqttApi.Endpoints;
using MqttApi.Middleware;
using MqttApi.Services;
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
// listeners are set up here: the public one from Public:Url (or ASPNETCORE_URLS) the
// same way Kestrel would bind it, and the admin one from the Admin section.
var publicUrls = EndpointBinding.ResolvePublicUrls(builder.Configuration, builder.Configuration["Public:Url"]);
var adminAddress = IPAddress.TryParse(adminOptions.ListenAddress, out var parsedAdminAddress)
    ? parsedAdminAddress
    : null;

if (adminAddress is not null && adminOptions.Enabled)
{
    var clashing = publicUrls.FirstOrDefault(url =>
        EndpointBinding.TryParse(url, out var endpoint) && EndpointBinding.ConflictsWithAdmin(endpoint, adminAddress, adminOptions.Port));
    if (clashing is not null)
        throw new InvalidOperationException(
            $"A(z) '{clashing}' publikus URL és az admin listener ({adminOptions.ListenAddress}:{adminOptions.Port}) ugyanaz a cím");
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    var publicListeners = 0;
    foreach (var url in publicUrls)
    {
        if (EndpointBinding.TryBind(kestrel, url, out var error)) publicListeners++;
        else Console.Error.WriteLine(error);
    }

    if (publicListeners == 0)
        Console.Error.WriteLine("Nincs érvényes publikus URL - a publikus API nem lesz elérhető");

    if (adminAddress is not null && adminOptions.Enabled)
        kestrel.Listen(adminAddress, adminOptions.Port);
    else if (adminOptions.Enabled)
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
