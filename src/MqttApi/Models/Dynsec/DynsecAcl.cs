using System.Text.Json.Serialization;

namespace MqttApi.Models.Dynsec;

public class DynsecAcl
{
    [JsonPropertyName("acltype")]
    public string AclType { get; set; } = string.Empty;

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("allow")]
    public bool Allow { get; set; }
}
