using System.Text.Json.Serialization;

namespace MqttApi.Models.Dynsec;

public class DynsecCommandEnvelope
{
    [JsonPropertyName("commands")]
    public List<DynsecCommand> Commands { get; set; } = [];
}
