using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using MqttApi.Configuration;

namespace MqttApi.Services;

public class EmailService(IOptions<EmailOptions> options, ILocalizationService loc) : IEmailService
{
    private readonly EmailOptions _options = options.Value;

    private string BaseUrl => _options.BaseUrl.TrimEnd('/');

    public async Task SendVerificationEmailAsync(string toEmail, string username, string token, CancellationToken ct = default)
    {
        var verifyUrl = $"{BaseUrl}/api/v1/users/verify?username={Uri.EscapeDataString(username)}&token={Uri.EscapeDataString(token)}";

        var body = $"""
            <p>{loc.Get("verify_email_greeting", username)}</p>
            <p>{loc.Get("verify_email_intro")}</p>
            <p style="text-align:center;margin:2rem 0"><a href="{verifyUrl}" style="background:#4f46e5;color:#fff;padding:.75rem 2rem;border-radius:6px;text-decoration:none;font-weight:600;display:inline-block">{loc.Get("verify_email_button")}</a></p>
            <p>{loc.Get("verify_email_disclaimer")}</p>
            """;

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = new NetworkCredential(_options.Username, _options.Password)
        };

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = loc.Get("verify_email_subject"),
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(toEmail);

        await client.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string toEmail, string username, string token, CancellationToken ct = default)
    {
        var resetUrl = $"{BaseUrl}/api/v1/users/reset-password?username={Uri.EscapeDataString(username)}&token={Uri.EscapeDataString(token)}";

        var body = $"""
            <p>{loc.Get("reset_password_email_greeting", username)}</p>
            <p>{loc.Get("reset_password_email_intro")}</p>
            <p style="text-align:center;margin:2rem 0"><a href="{resetUrl}" style="background:#4f46e5;color:#fff;padding:.75rem 2rem;border-radius:6px;text-decoration:none;font-weight:600;display:inline-block">{loc.Get("reset_password_email_button")}</a></p>
            <p>{loc.Get("reset_password_email_disclaimer")}</p>
            """;

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = new NetworkCredential(_options.Username, _options.Password)
        };

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = loc.Get("reset_password_email_subject"),
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(toEmail);

        await client.SendMailAsync(message, ct);
    }
}
