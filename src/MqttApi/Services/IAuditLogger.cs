namespace MqttApi.Services;

public interface IAuditLogger
{
    Task WriteAsync(string actor, string action, string target, string ip, bool success, string? detail = null);
}
