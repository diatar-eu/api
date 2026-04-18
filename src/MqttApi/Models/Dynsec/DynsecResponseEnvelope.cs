using System.Text.Json.Serialization;

namespace MqttApi.Models.Dynsec;

public class DynsecResponseEnvelope
{
    [JsonPropertyName("responses")]
    public List<DynsecResponse> Responses { get; set; } = [];
}
