using System.Text.Json;
using Microsoft.Extensions.Options;
using MqttApi.Configuration;

namespace MqttApi.Services;

public class AdminAuditService : IAuditLogger
{
    private readonly ILogger<AdminAuditService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string? _logPath;

    public AdminAuditService(IOptions<AdminOptions> options, ILogger<AdminAuditService> logger)
    {
        _logger = logger;
        _logPath = string.IsNullOrWhiteSpace(options.Value.AuditLogPath)
            ? null
            : options.Value.AuditLogPath;
    }

    public async Task WriteAsync(string actor, string action, string target, string ip, bool success, string? detail = null)
    {
        _logger.LogInformation(
            "Admin művelet: {Action} {Target} - sikeres: {Success}, actor: {Actor}, ip: {Ip}{Detail}",
            action, target, success, actor, ip,
            string.IsNullOrEmpty(detail) ? string.Empty : $", detail: {detail}");

        if (_logPath is null) return;

        var entry = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            actor,
            action,
            target,
            ip,
            success,
            detail
        });

        try
        {
            await _writeLock.WaitAsync();
            try
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                await File.AppendAllTextAsync(_logPath, entry + Environment.NewLine);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hiba az audit napló írása közben: {Path}", _logPath);
        }
    }
}
