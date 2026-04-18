namespace MqttApi.Services;

public interface IMqttPasswordVerifier
{
    Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default);
}
