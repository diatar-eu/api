using System.Text.Json.Serialization;

namespace MqttApi.Models.Dynsec;

public class DynsecRoleRef
{
    [JsonPropertyName("rolename")]
    public string Rolename { get; set; } = string.Empty;
}
