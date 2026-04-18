using System.Text.Json.Serialization;

namespace MqttApi.Models.Dynsec;

public class DynsecClientData
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("textname")]
    public string? TextName { get; set; }

    [JsonPropertyName("textdescription")]
    public string? TextDescription { get; set; }

    [JsonPropertyName("roles")]
    public List<DynsecRoleRef>? Roles { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }
}
