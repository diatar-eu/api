using MqttApi.Configuration;
using MqttApi.Constants;
using MqttApi.Endpoints;
using MqttApi.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IMqttPasswordVerifier, MqttPasswordVerifier>();
builder.Services.AddSingleton<IMqttClientService, MqttClientService>();
builder.Services.AddSingleton<IDynsecService, DynsecService>();
builder.Services.AddHostedService(sp => (MqttClientService)sp.GetRequiredService<IMqttClientService>());
builder.Services.AddHostedService(sp => (DynsecService)sp.GetRequiredService<IDynsecService>());
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks(ApiRoutes.Health);
UserEndpoints.Map(app);

app.Run();
