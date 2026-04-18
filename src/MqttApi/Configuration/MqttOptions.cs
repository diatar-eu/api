namespace MqttApi.Configuration;

public class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string AdminClientId { get; set; } = "mqttapi-admin";
    public string AdminUsername { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 10;
}
