using System.ComponentModel.DataAnnotations;

namespace MqttApi.Models.Requests;

public class ChangeEmailRequest
{
    [Required, MinLength(1), MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required, MinLength(1), MaxLength(256)]
    public string Password { get; set; } = string.Empty;

    [Required, MaxLength(256), EmailAddress]
    public string NewEmail { get; set; } = string.Empty;
}
