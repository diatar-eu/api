namespace MqttApi.Services;

public interface IEmailService
{
    Task SendVerificationEmailAsync(string toEmail, string username, string token, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(string toEmail, string username, string token, CancellationToken ct = default);
}
