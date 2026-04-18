using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MqttApi.Configuration;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace MqttApi.Services;

public class MqttClientService : IMqttClientService, IHostedService
{
    private readonly IManagedMqttClient _client;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttClientService> _logger;
    private readonly Channel<MqttApplicationMessage> _channel;

    public ChannelReader<MqttApplicationMessage> Messages => _channel.Reader;
    public bool IsConnected => _client.IsConnected;

    private const string ResponseTopic = "$CONTROL/dynamic-security/v1/response";
    private const string CommandTopic = "$CONTROL/dynamic-security/v1";

    public MqttClientService(IOptions<MqttOptions> options, ILogger<MqttClientService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _channel = Channel.CreateUnbounded<MqttApplicationMessage>(
            new UnboundedChannelOptions { SingleReader = true });

        var factory = new MqttFactory();
        _client = factory.CreateManagedMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceived;
        _client.ConnectedAsync += OnConnected;
        _client.DisconnectedAsync += OnDisconnected;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithCredentials(_options.AdminUsername, _options.AdminPassword)
            .WithClientId(_options.AdminClientId)
            .WithCleanSession(true)
            .Build();

        var managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(clientOptions)
            .Build();

        await _client.StartAsync(managedOptions);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await _client.StopAsync();
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .Build();

        await _client.EnqueueAsync(message);
    }

    private async Task OnConnected(MqttClientConnectedEventArgs args)
    {
        _logger.LogInformation("Connected to MQTT broker {Host}:{Port}", _options.Host, _options.Port);
        await _client.SubscribeAsync(ResponseTopic);
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs args)
    {
        _logger.LogWarning("Disconnected from MQTT broker. Reason: {Reason}", args.ReasonString);
        return Task.CompletedTask;
    }

    private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        _channel.Writer.TryWrite(args.ApplicationMessage);
        return Task.CompletedTask;
    }
}
