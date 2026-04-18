using Microsoft.Extensions.Options;
using MqttApi.Configuration;
using MQTTnet;
using MQTTnet.Client;

namespace MqttApi.Services;

public class MqttPasswordVerifier(IOptions<MqttOptions> options) : IMqttPasswordVerifier
{
    private readonly MqttOptions _options = options.Value;

    public async Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();

        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithCredentials(username, password)
            .WithClientId($"mqttapi-verify-{Guid.NewGuid():N}")
            .WithTimeout(TimeSpan.FromSeconds(5))
            .Build();

        try
        {
            var result = await client.ConnectAsync(clientOptions, ct);
            if (client.IsConnected)
                await client.DisconnectAsync(cancellationToken: ct);
            return result.ResultCode == MqttClientConnectResultCode.Success;
        }
        catch
        {
            return false;
        }
    }
}
