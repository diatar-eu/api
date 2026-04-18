using System.Threading.Channels;
using MQTTnet;

namespace MqttApi.Services;

public interface IMqttClientService
{
    Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default);
    ChannelReader<MqttApplicationMessage> Messages { get; }
    bool IsConnected { get; }
}
