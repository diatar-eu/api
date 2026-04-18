using System.Text.Json.Serialization;

namespace MqttApi.Models.Dynsec;

public class DynsecResponseData
{
    [JsonPropertyName("client")]
    public DynsecClientData? Client { get; set; }

    [JsonPropertyName("clients")]
    public List<string>? Clients { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}
