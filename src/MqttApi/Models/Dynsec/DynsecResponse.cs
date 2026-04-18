using System.Text.Json.Serialization;

namespace MqttApi.Models.Dynsec;

public class DynsecResponse
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("correlationData")]
    public string? CorrelationData { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("data")]
    public DynsecResponseData? Data { get; set; }

    public bool IsSuccess => string.IsNullOrEmpty(Error);
}
